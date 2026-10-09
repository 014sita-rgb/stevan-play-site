using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StevanPlayer.Core.Providers;

/// <summary>OpenSubtitles REST v1; uses only a user-supplied API key and optional bearer token.
/// Inject a client from ProviderHttpClientFactory to prevent credentials following redirects.</summary>
public sealed class OpenSubtitlesProvider(HttpClient httpClient) : ISubtitleProvider
{
    private const string ApiBase = "https://api.opensubtitles.com/api/v1/";
    public string Id => "OpenSubtitles";
    public bool IsConfigured(PlayerSettings settings) => ProviderHttp.IsSafeCredential(settings.OpenSubtitlesApiKey);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        if (!string.IsNullOrWhiteSpace(media.OpenSubtitlesHash))
        {
            var byHash = await SearchPagesAsync(media, settings, true, cancellationToken).ConfigureAwait(false);
            if (byHash.Any(candidate => candidate.HashMatched)) return byHash;
        }
        return await SearchPagesAsync(media, settings, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> SearchPagesAsync(MediaIdentity media, PlayerSettings settings,
        bool hashOnly, CancellationToken cancellationToken)
    {
        var results = new List<SubtitleCandidate>();
        var totalPages = 1;
        for (var page = 1; page <= Math.Min(totalPages, 3); page++)
        {
            var query = new Dictionary<string, string?>
            {
                ["languages"] = "sr,hr,bs", ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["order_by"] = "download_count", ["order_direction"] = "desc"
            };
            if (hashOnly) query["moviehash"] = media.OpenSubtitlesHash;
            else
            {
                query["query"] = media.Title;
                query["year"] = media.Year?.ToString(CultureInfo.InvariantCulture);
                query["season_number"] = media.Season?.ToString(CultureInfo.InvariantCulture);
                query["episode_number"] = media.Episode?.ToString(CultureInfo.InvariantCulture);
                query["type"] = media.Season.HasValue ? "episode" : "movie";
            }
            using var request = ApiRequest(HttpMethod.Get, "subtitles?" + ProviderHttp.Query(query, sortedFormEncoding: true), settings);
            using var document = await ProviderHttp.JsonAsync(httpClient, request, Id, cancellationToken,
                (original, destination) => ApiRedirectAllowed(original, destination, settings)).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();
            totalPages = ProviderHttp.Integer(root, "total_pages") ?? 1;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("attributes", out var attributes) ||
                    attributes.ValueKind != JsonValueKind.Object) continue;
                var language = ProviderHttp.Language(ProviderHttp.String(attributes, "language"));
                if (language is null || ProviderHttp.Boolean(attributes, "foreign_parts_only")) continue;
                var matchedHash = ProviderHttp.Boolean(attributes, "moviehash_match");
                var season = (int?)null;
                var episode = (int?)null;
                if (attributes.TryGetProperty("feature_details", out var details))
                {
                    season = ProviderHttp.Integer(details, "season_number");
                    episode = ProviderHttp.Integer(details, "episode_number");
                    if (media.Season.HasValue && season.HasValue && season != media.Season ||
                        media.Episode.HasValue && episode.HasValue && episode != media.Episode) continue;
                    var year = ProviderHttp.Integer(details, "year");
                    if (!matchedHash && media.Year.HasValue && year.HasValue && year != media.Year) continue;
                    var type = ProviderHttp.String(details, "feature_type");
                    if (media.Season.HasValue && string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) ||
                        !media.Season.HasValue && string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (hashOnly && !matchedHash) continue;
                if (media.Episode.HasValue && !matchedHash &&
                    (season != media.Season || episode != media.Episode)) continue;
                if (!attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
                foreach (var file in files.EnumerateArray())
                {
                    var fileId = ProviderHttp.Integer(file, "file_id");
                    if (!fileId.HasValue || fileId <= 0) continue;
                    var release = ProviderHttp.String(attributes, "release") ?? ProviderHttp.String(file, "file_name") ?? media.Title;
                    var reference = fileId.Value.ToString(CultureInfo.InvariantCulture);
                    results.Add(new SubtitleCandidate(reference, Id, language, release, reference,
                        matchedHash, season, episode, ProviderHttp.Integer(attributes, "download_count") ?? 0));
                }
            }
            if (data.GetArrayLength() == 0) break;
        }
        return results.DistinctBy(candidate => candidate.Id).ToArray();
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        if (candidate.ProviderId != Id || !int.TryParse(candidate.DownloadReference, out var fileId) || fileId <= 0)
            throw InvalidResponse();
        using var request = ApiRequest(HttpMethod.Post, "download", settings);
        request.Content = JsonContent.Create(new { file_id = fileId, sub_format = "srt" });
        using var document = await ProviderHttp.JsonAsync(httpClient, request, Id, cancellationToken,
            (original, destination) => ApiRedirectAllowed(original, destination, settings)).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var uri = ProviderHttp.TrustedDownloadUri(ProviderHttp.String(root, "link"), Id,
            "opensubtitles.com", "opensubtitles.org");
        if (ContainsAccountCredential(uri, settings)) throw InvalidResponse();
        // The final permitted quota download can report remaining=0 and is still valid.
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        downloadRequest.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
        var bytes = await ProviderHttp.BytesAsync(httpClient, downloadRequest, Id, cancellationToken,
            (_, destination) => DownloadRedirectAllowed(destination, settings)).ConfigureAwait(false);
        return new SubtitlePayload(ProviderHttp.SafeFileName(ProviderHttp.String(root, "file_name"),
            $"{candidate.Id}.{candidate.Language}.srt"), bytes);
    }

    private HttpRequestMessage ApiRequest(HttpMethod method, string path, PlayerSettings settings)
    {
        var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Add("Api-Key", settings.OpenSubtitlesApiKey.Trim());
        request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (ProviderHttp.IsSafeCredential(settings.OpenSubtitlesToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenSubtitlesToken.Trim());
        return request;
    }

    private void RequireConfiguration(PlayerSettings settings)
    {
        if (!IsConfigured(settings)) throw new ProviderException(Id, ProviderFailure.Configuration,
            "OpenSubtitles: unesite svoj API ključ u podešavanjima. Preuzimanje može zahtevati i token naloga.");
    }

    private ProviderException InvalidResponse() => new(Id, ProviderFailure.InvalidResponse,
        "OpenSubtitles: neispravan rezultat pretrage ili preuzimanja.");

    private static bool ApiRedirectAllowed(Uri original, Uri destination, PlayerSettings settings) =>
        destination.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(destination.UserInfo) && destination.IsDefaultPort &&
        string.Equals(destination.IdnHost, original.IdnHost, StringComparison.OrdinalIgnoreCase) && destination.Port == original.Port &&
        !ContainsAccountCredential(destination, settings);

    private bool DownloadRedirectAllowed(Uri destination, PlayerSettings settings)
    {
        if (ContainsAccountCredential(destination, settings)) return false;
        try
        {
            ProviderHttp.TrustedDownloadUri(destination.AbsoluteUri, Id, "opensubtitles.com", "opensubtitles.org");
            return true;
        }
        catch (ProviderException) { return false; }
    }

    private static bool ContainsAccountCredential(Uri destination, PlayerSettings settings)
    {
        var decoded = Uri.UnescapeDataString(destination.PathAndQuery).Replace('+', ' ');
        return (!string.IsNullOrWhiteSpace(settings.OpenSubtitlesApiKey) && decoded.Contains(settings.OpenSubtitlesApiKey.Trim(), StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(settings.OpenSubtitlesToken) && decoded.Contains(settings.OpenSubtitlesToken.Trim(), StringComparison.Ordinal));
    }
}
