using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core;

public sealed class SubtitleCatalog(IEnumerable<ISubtitleProvider> providers)
{
    private readonly ISubtitleProvider[] _providers = providers.ToArray();

    public async Task<SubtitleSearchResult> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var tasks = _providers.Select(async provider =>
        {
            if (!provider.IsConfigured(settings))
                return (Candidates: Array.Empty<SubtitleCandidate>(), Notice: new ProviderNotice(provider.Id, "API приступ није подешен."));
            try
            {
                var items = await provider.SearchAsync(media, settings, cancellationToken);
                return (Candidates: items.Where(x => x.Language is "sr" or "hr" or "bs").Where(x => CompatibleEpisode(x, media)).Select(x => x with { Score = Rank(x, media, settings) }).ToArray(),
                    Notice: (ProviderNotice?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is Providers.ProviderException or HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                return (Candidates: Array.Empty<SubtitleCandidate>(), Notice: new ProviderNotice(provider.Id,
                    ex is TaskCanceledException ? "Извор није одговорио на време." : ex.Message));
            }
        });
        var results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return new(results.SelectMany(x => x.Candidates).DistinctBy(x => (x.ProviderId, x.Id))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Downloads).ToArray(),
            results.Where(x => x.Notice is not null).Select(x => x.Notice!).ToArray());
    }

    public async Task<string> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        string cacheDirectory, CancellationToken cancellationToken = default, MediaIdentity? media = null)
    {
        var provider = _providers.SingleOrDefault(x => x.Id == candidate.ProviderId)
            ?? throw new InvalidOperationException("Непознат извор титла.");
        if (media is not null && !CompatibleEpisode(candidate, media))
            throw new InvalidDataException("Титл припада другој епизоди.");
        var payload = await provider.DownloadAsync(candidate, settings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = SubtitleArchive.Normalize(payload, media, candidate.Language);
        var identity = candidate.ProviderId + "\n" + candidate.Id + "\n" + normalized;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + ".srt";
        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, normalized, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool CompatibleEpisode(SubtitleCandidate candidate, MediaIdentity media)
    {
        if (media.Episode is not int episode) return true;
        var parsed = MediaIdentifier.Parse(candidate.Release);
        var candidateEpisode = candidate.Episode ?? parsed.Episode;
        var candidateSeason = candidate.Season ?? parsed.Season;
        return (candidateEpisode is null || candidateEpisode == episode) &&
            (candidateSeason is null || candidateSeason == media.Season);
    }

    private static int Rank(SubtitleCandidate candidate, MediaIdentity media, PlayerSettings settings)
    {
        var score = candidate.HashMatched ? 1000 : 0;
        if (candidate.Language == settings.PreferredLanguage) score += 60;
        var releaseWords = Regex.Split(candidate.Release.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(x => x.Length > 2).ToHashSet();
        var mediaWords = Regex.Split(Path.GetFileNameWithoutExtension(media.FilePath).ToLowerInvariant(), @"[^\p{L}\p{N}]+");
        score += mediaWords.Count(releaseWords.Contains) * 10;
        if (media.Year is int year && releaseWords.Contains(year.ToString())) score += 30;
        if (media.Episode.HasValue && MediaIdentifier.Parse(candidate.Release).Episode == media.Episode) score += 100;
        return score;
    }
}
