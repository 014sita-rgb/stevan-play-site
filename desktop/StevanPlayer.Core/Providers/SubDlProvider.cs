using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core.Providers;

/// <summary>SubDL public API. API keys are user-owned; archives are returned for safe core extraction.
/// Inject a client from ProviderHttpClientFactory to prevent credentials following redirects.</summary>
public sealed partial class SubDlProvider(HttpClient httpClient) : ISubtitleProvider
{
    private const string ApiBase = "https://api.subdl.com/api/v1/subtitles";
    public string Id => "SubDL";
    public bool IsConfigured(PlayerSettings settings) => ProviderHttp.IsSafeCredential(settings.SubDlApiKey);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        var results = new List<SubtitleCandidate>();
        var totalPages = 1;
        for (var page = 1; page <= Math.Min(totalPages, 2); page++)
        {
            var parameters = new Dictionary<string, string?>
            {
                ["api_key"] = settings.SubDlApiKey.Trim(), ["film_name"] = media.Title,
                ["type"] = media.Season.HasValue ? "tv" : "movie", ["languages"] = "SR,HR,BS",
                ["season_number"] = media.Season?.ToString(CultureInfo.InvariantCulture),
                ["episode_number"] = media.Episode?.ToString(CultureInfo.InvariantCulture),
                ["year"] = media.Year?.ToString(CultureInfo.InvariantCulture), ["client"] = "custom_integration",
                ["releases"] = "1", ["subs_per_page"] = "30", ["page"] = page.ToString(CultureInfo.InvariantCulture)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + '?' + ProviderHttp.Query(parameters));
            request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var document = await ProviderHttp.JsonAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            var error = ProviderHttp.String(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (error.Contains("can't find", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("not_found", StringComparison.OrdinalIgnoreCase)) return results;
                if (error is "daily_limit" or "api_download_limit_exceeded" or "rate_limit")
                    throw new ProviderException(Id, ProviderFailure.Quota, "SubDL: dostignuto je ograničenje naloga ili zahteva. Pokušajte kasnije.");
                if (error.Contains("key", StringComparison.OrdinalIgnoreCase) || error.Contains("auth", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException(Id, ProviderFailure.Authentication, "SubDL: API ključ nije prihvaćen.");
                throw InvalidResponse();
            }
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False ||
                root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw InvalidResponse();
            // The documented response states that all subtitle rows belong to results[0].
            // Reject a known mismatched film returned by the service's broader fallback search.
            if (root.TryGetProperty("results", out var films) && films.ValueKind == JsonValueKind.Array && films.GetArrayLength() > 0)
            {
                var firstFilm = films[0];
                var filmName = ProviderHttp.String(firstFilm, "name");
                var filmYear = ProviderHttp.Integer(firstFilm, "year");
                var filmType = ProviderHttp.String(firstFilm, "type");
                if (!string.IsNullOrWhiteSpace(filmName) && NormalizeTitle(filmName) != NormalizeTitle(media.Title) ||
                    media.Year.HasValue && filmYear.HasValue && media.Year != filmYear ||
                    !string.IsNullOrWhiteSpace(filmType) && filmType != (media.Season.HasValue ? "tv" : "movie")) return results;
            }
            if (!root.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();
            totalPages = ProviderHttp.Integer(root, "totalPages") ?? ProviderHttp.Integer(root, "total_pages") ?? 1;
            foreach (var row in subtitles.EnumerateArray())
            {
                var language = ProviderHttp.Language(ProviderHttp.String(row, "language") ?? ProviderHttp.String(row, "lang"));
                if (language is null || ProviderHttp.Boolean(row, "forced")) continue;
                var rawUrl = ProviderHttp.String(row, "url");
                if (string.IsNullOrWhiteSpace(rawUrl)) continue;
                var uri = ResolveDownloadUri(rawUrl);
                var id = ProviderHttp.String(row, "name") ?? uri.AbsolutePath.Split('/').Last();
                var release = Releases(row) ?? ProviderHttp.String(row, "release_name") ?? id;
                var season = ProviderHttp.Integer(row, "season");
                var episode = ProviderHttp.Integer(row, "episode");
                if (media.Episode.HasValue)
                {
                    if (season == 0) season = null;
                    if (episode == 0) episode = null;
                    var first = ProviderHttp.Integer(row, "episode_from");
                    var last = ProviderHttp.Integer(row, "episode_end");
                    var inPack = first > 0 && last >= first && media.Episode >= first && media.Episode <= last;
                    var fullSeason = ProviderHttp.Boolean(row, "full_season");
                    var match = EpisodePattern().Match(release);
                    if (match.Success)
                    {
                        season ??= int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
                        if (!inPack && !fullSeason)
                            episode ??= int.Parse(match.Groups["episode"].Value, CultureInfo.InvariantCulture);
                    }
                    if (season.HasValue && season != media.Season ||
                        episode.HasValue && episode != media.Episode && !inPack && !fullSeason) continue;
                    if (season != media.Season || episode != media.Episode && !inPack && !fullSeason) continue;
                    // A season pack is allowed only with explicit season metadata. The core chooses its exact member.
                    if (inPack || fullSeason) episode = media.Episode;
                }
                else if (season > 0 || episode > 0) continue;
                results.Add(new SubtitleCandidate(id, Id, language, release, uri.AbsoluteUri,
                    false, season, episode, ProviderHttp.Integer(row, "downloads") ?? 0));
            }
            if (subtitles.GetArrayLength() == 0) break;
        }
        return results.DistinctBy(candidate => candidate.DownloadReference).ToArray();
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        if (candidate.ProviderId != Id) throw InvalidResponse();
        var uri = ProviderHttp.TrustedDownloadUri(candidate.DownloadReference, Id, "dl.subdl.com");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
        var bytes = await ProviderHttp.BytesAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false);
        return new SubtitlePayload(ProviderHttp.SafeFileName(Uri.UnescapeDataString(uri.AbsolutePath),
            candidate.Id + ".zip"), bytes);
    }

    private Uri ResolveDownloadUri(string reference)
    {
        // Documented SubDL relative links are relative to its download CDN, never the API host.
        if (!Uri.TryCreate(new Uri("https://dl.subdl.com/"), reference, out var resolved)) throw InvalidResponse();
        return ProviderHttp.TrustedDownloadUri(resolved.AbsoluteUri, Id, "dl.subdl.com");
    }

    private static string? Releases(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("releases", out var releases)) return null;
        if (releases.ValueKind == JsonValueKind.String) return releases.GetString();
        if (releases.ValueKind != JsonValueKind.Array) return null;
        var names = releases.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)).Take(20).ToArray();
        return names.Length > 0 ? string.Join(" / ", names) : null;
    }

    private void RequireConfiguration(PlayerSettings settings)
    {
        if (!IsConfigured(settings)) throw new ProviderException(Id, ProviderFailure.Configuration,
            "SubDL: unesite svoj API ključ u podešavanjima.");
    }

    private ProviderException InvalidResponse() => new(Id, ProviderFailure.InvalidResponse,
        "SubDL: neispravan rezultat pretrage ili preuzimanja.");

    private static string NormalizeTitle(string value) =>
        new(value.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"(?i)(?:\bs|[._ -]s)(?<season>\d{1,2})[ ._-]*e(?<episode>\d{1,3})(?!\d)|(?:^|[._ -])(?<season>\d{1,2})x(?<episode>\d{1,3})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePattern();
}
