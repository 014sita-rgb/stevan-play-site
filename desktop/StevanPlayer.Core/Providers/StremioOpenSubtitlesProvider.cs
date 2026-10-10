using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core.Providers;

/// <summary>
/// The publicly advertised OpenSubtitles v3 addon, using Stremio's documented universal
/// addon protocol. The service holds its own integration credentials; none are requested
/// from the player user. Inject a ProviderHttpClientFactory client to disable redirects.
/// </summary>
public sealed class StremioOpenSubtitlesProvider : ISubtitleProvider
{
    private const string CinemetaBase = "https://v3-cinemeta.strem.io/";
    private const string SubtitlesBase = "https://opensubtitles-v3.strem.io/";
    private readonly HttpClient httpClient;
    private readonly TimeSpan requestInterval;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextRequestAt;

    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "opensubtitles-v3.strem.io", "subs.strem.io", "dl.opensubtitles.org",
        "dl.opensubtitles.com", "www.opensubtitles.com", "api.opensubtitles.com"
    };

    public StremioOpenSubtitlesProvider(HttpClient httpClient, TimeSpan? minimumRequestInterval = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        requestInterval = minimumRequestInterval ?? TimeSpan.FromSeconds(1);
        if (requestInterval < TimeSpan.Zero || requestInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
    }

    public string Id => "OpenSubtitles public";
    public bool IsConfigured(PlayerSettings settings) => true;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(media.Title) || media.Title.Length > 200 ||
            media.Title.Any(char.IsControl) || media.Season.HasValue != media.Episode.HasValue ||
            media.Season is < 0 or > 1000 || media.Episode is < 1 or > 1000)
            throw InvalidResponse("Naslov ili broj epizode nije dovoljno određen za pretragu.");

        var type = media.Episode.HasValue ? "series" : "movie";
        var catalogUri = new Uri(CinemetaBase + $"catalog/{type}/top/search={Uri.EscapeDataString(media.Title)}.json");
        using var catalog = await JsonAsync(catalogUri, cancellationToken).ConfigureAwait(false);
        if (catalog.RootElement.ValueKind != JsonValueKind.Object ||
            !catalog.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();

        var matches = new HashSet<string>(StringComparer.Ordinal);
        var metadataChecks = 0;
        foreach (var row in metas.EnumerateArray().Take(100))
        {
            if (!MatchesTitleAndType(row, media.Title, type)) continue;
            var imdbId = ProviderHttp.String(row, "id");
            if (imdbId is null || !Regex.IsMatch(imdbId, @"^tt[0-9]{5,12}$", RegexOptions.CultureInvariant)) continue;
            var year = ReadYear(row);
            if (media.Year.HasValue && year is null)
            {
                if (metadataChecks++ >= 3) continue;
                using var details = await JsonAsync(new Uri(CinemetaBase + $"meta/{type}/{imdbId}.json"),
                    cancellationToken).ConfigureAwait(false);
                if (details.RootElement.ValueKind != JsonValueKind.Object ||
                    !details.RootElement.TryGetProperty("meta", out var metadata) ||
                    !MatchesTitleAndType(metadata, media.Title, type) || ProviderHttp.String(metadata, "id") != imdbId) continue;
                year = ReadYear(metadata);
            }
            if (media.Year.HasValue && year != media.Year) continue;
            matches.Add(imdbId);
        }
        if (matches.Count == 0) return [];
        if (matches.Count > 1)
            throw InvalidResponse("Više filmova ima taj naslov. Dodajte godinu u naziv video datoteke da plejer izabere pravi film.");

        var videoId = matches.Single();
        if (media.Season.HasValue)
            videoId += ":" + media.Season.Value.ToString(CultureInfo.InvariantCulture) + ":" +
                media.Episode!.Value.ToString(CultureInfo.InvariantCulture);
        var extra = new Dictionary<string, string?> { ["filename"] = VideoFileName(media.FilePath) };
        if (media.FileSize > 0 && media.OpenSubtitlesHash is string hash &&
            Regex.IsMatch(hash, @"^[0-9a-fA-F]{16}$", RegexOptions.CultureInvariant))
        {
            extra["videoHash"] = hash.ToLowerInvariant();
            extra["videoSize"] = media.FileSize.ToString(CultureInfo.InvariantCulture);
        }
        var subtitleUri = new Uri(SubtitlesBase + $"subtitles/{type}/{Uri.EscapeDataString(videoId)}/" +
            ProviderHttp.Query(extra) + ".json");
        using var subtitles = await JsonAsync(subtitleUri, cancellationToken).ConfigureAwait(false);
        if (subtitles.RootElement.ValueKind != JsonValueKind.Object ||
            !subtitles.RootElement.TryGetProperty("subtitles", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        var results = new List<SubtitleCandidate>();
        foreach (var row in rows.EnumerateArray().Take(1000))
        {
            var language = ProviderHttp.Language(ProviderHttp.String(row, "lang"));
            var id = ProviderHttp.String(row, "id");
            if (language is null || string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)) continue;
            var uri = TrustedDownloadUri(ProviderHttp.String(row, "url"));
            var release = ProviderHttp.String(row, "label") ?? ProviderHttp.String(row, "release") ??
                Path.GetFileNameWithoutExtension(VideoFileName(media.FilePath));
            var episode = MediaIdentifier.Parse(release);
            if (media.Episode.HasValue &&
                (episode.Season.HasValue && episode.Season != media.Season ||
                 episode.Episode.HasValue && episode.Episode != media.Episode)) continue;
            // A supplied hash improves the server's ranking. It is not proof that each returned file matched.
            results.Add(new SubtitleCandidate(id, Id, language, release, uri.AbsoluteUri,
                Season: media.Season, Episode: media.Episode));
        }
        return results.DistinctBy(candidate => (candidate.Id, candidate.Language)).ToArray();
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ProviderId != Id || candidate.Language is not ("sr" or "hr" or "bs")) throw InvalidResponse();
        var uri = TrustedDownloadUri(candidate.DownloadReference);
        var bytes = await BytesAsync(uri, cancellationToken, (_, destination) => IsTrustedDownloadUri(destination))
            .ConfigureAwait(false);
        var extension = uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" :
            uri.AbsolutePath.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) ? ".ass" :
            uri.AbsolutePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ? ".vtt" : ".srt";
        var safeId = Regex.Replace(candidate.Id, @"[^A-Za-z0-9_-]", "_");
        if (safeId.Length > 120) safeId = safeId[..120];
        return new SubtitlePayload($"{safeId}.{candidate.Language}{extension}", bytes);
    }

    private async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        var bytes = await BytesAsync(uri, cancellationToken, (original, destination) =>
            destination.Scheme == Uri.UriSchemeHttps && destination.IsDefaultPort &&
            string.IsNullOrEmpty(destination.UserInfo) && string.IsNullOrEmpty(destination.Fragment) &&
            string.Equals(original.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private async Task<byte[]> BytesAsync(Uri uri, CancellationToken cancellationToken,
        Func<Uri, Uri, bool> redirectPolicy)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            nextRequestAt = DateTimeOffset.UtcNow + requestInterval;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", .5));
            return await ProviderHttp.BytesAsync(httpClient, request, Id, cancellationToken, redirectPolicy).ConfigureAwait(false);
        }
        catch (ProviderException exception) when (exception.Failure == ProviderFailure.Quota)
        {
            nextRequestAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
            throw;
        }
        catch (ProviderException exception) when (exception.Failure == ProviderFailure.Authentication)
        {
            throw new ProviderException(Id, ProviderFailure.Authentication,
                "OpenSubtitles: javni servis trenutno ne dozvoljava zahtev. Pokušajte ponovo kasnije.");
        }
        finally { requestGate.Release(); }
    }

    private Uri TrustedDownloadUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsTrustedDownloadUri(uri)) throw InvalidResponse();
        return uri;
    }

    private static bool IsTrustedDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && DownloadHosts.Contains(uri.IdnHost);

    private static bool MatchesTitleAndType(JsonElement row, string title, string type) =>
        row.ValueKind == JsonValueKind.Object && ProviderHttp.String(row, "type") == type &&
        NormalizeTitle(ProviderHttp.String(row, "name") ?? "") == NormalizeTitle(title);

    private static string NormalizeTitle(string title) => string.Concat(title.Normalize(NormalizationForm.FormD)
        .Where(character => char.IsLetterOrDigit(character)).Select(char.ToLowerInvariant));

    private static int? ReadYear(JsonElement row)
    {
        var year = ProviderHttp.Integer(row, "year");
        if (year is >= 1800 and <= 2200) return year;
        var release = ProviderHttp.String(row, "releaseInfo") ?? ProviderHttp.String(row, "released");
        return release is { Length: >= 4 } && int.TryParse(release.AsSpan(0, 4), out var value) && value is >= 1800 and <= 2200
            ? value : null;
    }

    private static string VideoFileName(string path)
    {
        var name = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) : path.Replace('\\', '/').Split('/').Last();
        return name.Length <= 300 && !name.Any(char.IsControl) ? name : "video";
    }

    private ProviderException InvalidResponse(string? detail = null) => new(Id, ProviderFailure.InvalidResponse,
        "OpenSubtitles: " + (detail ?? "Javni servis je vratio neispravan ili nepodržan odgovor."));
}
