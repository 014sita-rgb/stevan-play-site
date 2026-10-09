using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core.Providers;

/// <summary>Normal authenticated Titlovi Kodi API access. It never scrapes pages or bypasses access controls.
/// Use ProviderHttpClientFactory so the API's credential-bearing query parameters cannot follow redirects.</summary>
public sealed partial class TitloviProvider(HttpClient httpClient, TimeProvider? timeProvider = null) : ISubtitleProvider
{
    private const string ApiBase = "https://kodi.titlovi.com/api/subtitles/";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly Dictionary<string, LoginToken> tokens = [];
    private static readonly TimeSpan ExpirationMargin = TimeSpan.FromMinutes(1);

    public string Id => "Titlovi.com";
    public bool IsConfigured(PlayerSettings settings) =>
        ProviderHttp.IsSafeCredential(settings.TitloviUsername) && ProviderHttp.IsSafeCredential(settings.TitloviPassword);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        var account = Credentials(settings);
        var token = await GetTokenAsync(account, cancellationToken).ConfigureAwait(false);
        var results = new List<SubtitleCandidate>();
        var totalPages = 1;
        for (var page = 1; page <= Math.Min(totalPages, 3); page++)
        {
            var query = new Dictionary<string, string?>
            {
                ["token"] = token.Value, ["userid"] = token.UserId, ["query"] = media.Title,
                ["lang"] = "Srpski|Cirilica|Hrvatski|Bosanski", ["json"] = "true",
                ["season"] = media.Season?.ToString(CultureInfo.InvariantCulture),
                ["episode"] = media.Episode?.ToString(CultureInfo.InvariantCulture),
                ["year"] = media.Year?.ToString(CultureInfo.InvariantCulture),
                ["pg"] = page == 1 ? null : page.ToString(CultureInfo.InvariantCulture)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "search?" + ProviderHttp.Query(query));
            request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
            System.Text.Json.JsonDocument document;
            try
            {
                document = await ProviderHttp.JsonAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.Failure == ProviderFailure.Authentication)
            {
                await InvalidateTokenAsync(account.Fingerprint, cancellationToken).ConfigureAwait(false);
                throw;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !root.TryGetProperty("SubtitleResults", out var subtitles) ||
                    subtitles.ValueKind is not (System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Null))
                    throw InvalidResponse();
                totalPages = ProviderHttp.Integer(root, "PagesAvailable") ?? 1;
                if (subtitles.ValueKind == System.Text.Json.JsonValueKind.Null) break;
                foreach (var row in subtitles.EnumerateArray())
                {
                    var language = ProviderHttp.Language(ProviderHttp.String(row, "Lang"));
                    var title = ProviderHttp.String(row, "Title");
                    var id = ProviderHttp.String(row, "Id");
                    var link = ProviderHttp.String(row, "Link");
                    if (language is null || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id) ||
                        string.IsNullOrWhiteSpace(link) ||
                        !TitleAliases().Split(title).Any(alias => NormalizeTitle(alias) == NormalizeTitle(media.Title))) continue;
                    var season = Positive(ProviderHttp.Integer(row, "Season"));
                    var episode = Positive(ProviderHttp.Integer(row, "Episode"));
                    if (media.Episode.HasValue)
                    {
                        if (season != media.Season || episode.HasValue && episode != media.Episode) continue;
                        // Episode=0 is the API's documented season-pack convention. Unknown/null is not a pack.
                        if (!episode.HasValue && ProviderHttp.Integer(row, "Episode") != 0) continue;
                        episode = media.Episode;
                    }
                    else
                    {
                        if (season.HasValue || episode.HasValue) continue;
                        var year = ProviderHttp.Integer(row, "Year");
                        if (media.Year.HasValue && year.HasValue && media.Year != year) continue;
                    }
                    var uri = ProviderHttp.TrustedDownloadUri(link, Id, "titlovi.com");
                    results.Add(new SubtitleCandidate(id, Id, language,
                        ProviderHttp.String(row, "Release") ?? title, uri.AbsoluteUri,
                        false, season, episode, ProviderHttp.Integer(row, "DownloadCount") ?? 0));
                }
                if (subtitles.GetArrayLength() == 0) break;
            }
        }
        return results.DistinctBy(candidate => candidate.Id).ToArray();
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        RequireConfiguration(settings);
        if (candidate.ProviderId != Id) throw InvalidResponse();
        var uri = ProviderHttp.TrustedDownloadUri(candidate.DownloadReference, Id, "titlovi.com");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
        var bytes = await ProviderHttp.BytesAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false);
        if (bytes.Length >= 4 && bytes[0] == 'R' && bytes[1] == 'a' && bytes[2] == 'r' && bytes[3] == '!')
            throw new ProviderException(Id, ProviderFailure.InvalidResponse,
                "Titlovi.com: ovaj titl je u RAR arhivi. Izaberite ZIP/SRT izdanje ili ručno raspakujte titl.");
        var isZip = bytes.Length >= 2 && bytes[0] == 'P' && bytes[1] == 'K';
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (isZip) extension = ".zip";
        else if (extension is not (".srt" or ".ass" or ".ssa" or ".vtt"))
        {
            var beginning = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart('\uFEFF', '\r', '\n');
            extension = beginning.StartsWith("WEBVTT", StringComparison.Ordinal) ? ".vtt" :
                beginning.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ? ".ass" : ".srt";
        }
        return new SubtitlePayload($"titlovi-{candidate.Id}.{candidate.Language}" + extension, bytes);
    }

    private async Task<LoginToken> GetTokenAsync(AccountCredentials account, CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (tokens.TryGetValue(account.Fingerprint, out var cached) && cached.Expires > now + ExpirationMargin)
                return cached;
            foreach (var expired in tokens.Where(item => item.Value.Expires <= now + ExpirationMargin).Select(item => item.Key).ToArray())
                tokens.Remove(expired);
            // The current authenticated Kodi API accepts POST credentials in query parameters.
            // Keep this contract explicit; no secrets are logged, persisted in token caches, or sent on redirects.
            var query = ProviderHttp.Query(new Dictionary<string, string?>
            {
                ["username"] = account.Username, ["password"] = account.Password, ["json"] = "true"
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "gettoken?" + query);
            request.Headers.UserAgent.ParseAdd("StevanPlayer/1.0");
            using var document = await ProviderHttp.JsonAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var value = ProviderHttp.String(root, "Token");
            var userId = ProviderHttp.String(root, "UserId");
            var expiration = ProviderHttp.String(root, "ExpirationDate");
            if (!ProviderHttp.IsSafeCredential(value) || !long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) ||
                numericId <= 0 || !DateTimeOffset.TryParse(expiration, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires))
                throw InvalidResponse();
            if (expires <= clock.GetUtcNow() + ExpirationMargin)
                throw new ProviderException(Id, ProviderFailure.Authentication,
                    "Titlovi.com: servis je vratio već istekao token. Proverite nalog i pokušajte kasnije.");
            var token = new LoginToken(value!, numericId.ToString(CultureInfo.InvariantCulture), expires);
            if (tokens.Count >= 8) tokens.Remove(tokens.OrderBy(item => item.Value.Expires).First().Key);
            tokens[account.Fingerprint] = token;
            return token;
        }
        finally { loginGate.Release(); }
    }

    private async Task InvalidateTokenAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { tokens.Remove(fingerprint); }
        finally { loginGate.Release(); }
    }

    private static AccountCredentials Credentials(PlayerSettings settings)
    {
        var username = settings.TitloviUsername.Trim();
        var password = settings.TitloviPassword;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username + '\0' + password)));
        return new AccountCredentials(username, password, fingerprint);
    }

    private static int? Positive(int? number) => number > 0 ? number : null;

    private static string NormalizeTitle(string value) =>
        new(value.Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void RequireConfiguration(PlayerSettings settings)
    {
        if (!IsConfigured(settings)) throw new ProviderException(Id, ProviderFailure.Configuration,
            "Titlovi.com: unesite svoj korisnički nalog u podešavanjima. Nalog mora imati odobren API pristup.");
    }

    private ProviderException InvalidResponse() => new(Id, ProviderFailure.InvalidResponse,
        "Titlovi.com: servis je vratio neispravan odgovor; proverite dostupnost API-ja.");

    private sealed record AccountCredentials(string Username, string Password, string Fingerprint);
    private sealed record LoginToken(string Value, string UserId, DateTimeOffset Expires);

    [GeneratedRegex(@"\s+aka\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleAliases();
}
