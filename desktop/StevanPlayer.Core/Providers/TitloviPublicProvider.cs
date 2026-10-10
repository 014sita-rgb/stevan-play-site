using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace StevanPlayer.Core.Providers;

/// <summary>
/// Uses the public Titlovi.com search, subtitle page and the download link that page publishes.
/// No login, API key, challenge solving, private endpoint or credential is used. A denied robots
/// route or access challenge stops the request. The authenticated Kodi API is a separate service.
/// </summary>
public sealed partial class TitloviPublicProvider(HttpClient httpClient, TimeProvider? timeProvider = null,
    TimeSpan? minimumRequestInterval = null) : ISubtitleProvider
{
    private static readonly Uri Site = new("https://titlovi.com/");
    private const string UserAgent = "STEVANplejer/1.0 (+https://github.com/014sita-rgb/stevan-play-site)";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan minimumInterval = minimumRequestInterval ?? TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private RobotsPolicy? robots;
    private DateTimeOffset robotsChecked;
    private DateTimeOffset lastRequest;

    public string Id => "Titlovi.com";
    public bool IsConfigured(PlayerSettings settings) => true;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(MediaIdentity media, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.Title) || media.Title.Length > 300) return [];
        var query = new Dictionary<string, string?>
        {
            ["prijevod"] = media.Title, ["sort"] = "4",
            ["g"] = media.Episode.HasValue ? null : media.Year?.ToString(CultureInfo.InvariantCulture),
            ["t"] = media.Episode.HasValue ? "2" : null,
            ["s"] = media.Season?.ToString(CultureInfo.InvariantCulture)
        };
        var candidates = new List<SubtitleCandidate>();
        for (var page = 1; page <= 3; page++)
        {
            query["pg"] = page == 1 ? null : page.ToString(CultureInfo.InvariantCulture);
            var uri = new Uri(Site, "titlovi/?" + ProviderHttp.Query(query));
            var html = await HtmlAsync(uri, cancellationToken).ConfigureAwait(false);
            var starts = SubtitleStart().Matches(html);
            if (starts.Count == 0 && !SearchSummary().IsMatch(html)) throw InvalidResponse();
            for (var i = 0; i < starts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = starts[i].Index;
                var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
                var row = html[start..end];
                var heading = Heading().Match(row);
                var link = Anchor().Match(heading.Groups["body"].Value);
                var id = Attribute(heading.Groups["attributes"].Value, "data-id");
                var title = Text(link.Groups["body"].Value);
                if (!NumericId(id) || !TitleAliases().Split(title).Any(alias => Normalize(alias) == Normalize(media.Title))) continue;
                var yearMatch = Year().Match(heading.Groups["body"].Value);
                int? year = yearMatch.Success ? int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture) : null;
                var episodeMatch = Episode().Match(Text(heading.Groups["body"].Value));
                int? season = episodeMatch.Success ? int.Parse(episodeMatch.Groups["season"].Value, CultureInfo.InvariantCulture) : null;
                int? episode = episodeMatch.Groups["episode"].Success ? int.Parse(episodeMatch.Groups["episode"].Value, CultureInfo.InvariantCulture) : null;
                if (media.Episode.HasValue)
                {
                    if (season != media.Season || episode.HasValue && episode != media.Episode) continue;
                    // The public site explicitly labels a whole-season archive with "Sezona N".
                    episode = media.Episode;
                }
                else if (season.HasValue || episode.HasValue || media.Year.HasValue && year != media.Year) continue;
                var language = Images().Matches(row).Cast<Match>()
                    .Where(image => HasClass(image.Groups["attributes"].Value, "lang"))
                    .Select(image => FlagLanguage(Attribute(image.Groups["attributes"].Value, "src")))
                    .FirstOrDefault(value => value is not null);
                if (language is null) continue;
                var detail = SiteUri(Attribute(link.Groups["attributes"].Value, "href"));
                if (!DetailPath().IsMatch(detail.AbsolutePath) || !detail.AbsolutePath.EndsWith("-" + id + "/", StringComparison.Ordinal) ||
                    detail.Query.Length != 0) continue;
                var release = Release().Match(row);
                var releaseText = Text(Span().Replace(release.Groups["body"].Value, ""));
                var count = DownloadCount().Match(row);
                candidates.Add(new SubtitleCandidate(id!, Id, language, releaseText.Length > 0 ? releaseText : title,
                    detail.AbsoluteUri, false, season, episode,
                    int.TryParse(Text(count.Groups["body"].Value).Replace(".", "").Replace(",", ""), out var downloads) ? downloads : 0));
            }
            // Follow only the next numbered public page, never an arbitrary page-provided URL.
            var paging = Paging().Match(html).Groups["body"].Value;
            if (!Anchor().Matches(paging).Cast<Match>().Any(anchor =>
                    PageNumber(Attribute(anchor.Groups["attributes"].Value, "href")) == page + 1)) break;
        }
        return candidates.DistinctBy(candidate => candidate.Id).ToArray();
    }

    public async Task<SubtitlePayload> DownloadAsync(SubtitleCandidate candidate, PlayerSettings settings,
        CancellationToken cancellationToken)
    {
        if (candidate.ProviderId != Id || !NumericId(candidate.Id) || candidate.Language is not ("sr" or "hr" or "bs"))
            throw InvalidResponse();
        var detail = SiteUri(candidate.DownloadReference);
        if (!DetailPath().IsMatch(detail.AbsolutePath) || !detail.AbsolutePath.EndsWith("-" + candidate.Id + "/", StringComparison.Ordinal) ||
            detail.Query.Length != 0) throw InvalidResponse();
        var html = await HtmlAsync(detail, cancellationToken).ConfigureAwait(false);
        Uri? download = null;
        foreach (Match anchor in Anchor().Matches(html))
        {
            var value = Attribute(anchor.Groups["attributes"].Value, "href");
            if (value is null || !value.Contains("/download/", StringComparison.OrdinalIgnoreCase)) continue;
            var uri = SiteUri(value);
            var fields = QueryFields(uri.Query);
            if (uri.AbsolutePath == "/download/" && fields.Count == 2 && fields.GetValueOrDefault("type") == "1" &&
                fields.GetValueOrDefault("mediaid") == candidate.Id) { download = uri; break; }
        }
        if (download is null) throw new ProviderException(Id, ProviderFailure.Unavailable,
            "Titlovi.com: ovaj prevod trenutno nema javno dostupno preuzimanje u plejeru.");
        var bytes = await BytesAsync(download, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith("Rar!"u8)) throw new ProviderException(Id, ProviderFailure.InvalidResponse,
            "Titlovi.com: ovaj prevod koristi RAR arhivu koja još nije podržana. Izaberite drugo izdanje iz spiska.");
        if (bytes.AsSpan().StartsWith("PK"u8)) return new SubtitlePayload($"titlovi-{candidate.Id}.{candidate.Language}.zip", bytes);
        var text = SubtitleFormat.Decode(bytes);
        EnsureNoChallenge(text);
        if (text.TrimStart().StartsWith('<')) throw InvalidResponse();
        var extension = text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal) ? ".vtt" :
            text.TrimStart().StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ? ".ass" : ".srt";
        // Fail here on an HTML error or malformed subtitle instead of pretending the download worked.
        SubtitleFormat.Parse(text, extension);
        return new SubtitlePayload($"titlovi-{candidate.Id}.{candidate.Language}" + extension, bytes);
    }

    private async Task<string> HtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        var bytes = await BytesAsync(uri, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 2 * 1024 * 1024) throw InvalidResponse();
        var html = Encoding.UTF8.GetString(bytes);
        EnsureNoChallenge(html);
        return html;
    }

    private async Task<byte[]> BytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CheckRobotsAsync(cancellationToken).ConfigureAwait(false);
            if (!robots!.Allows(uri.PathAndQuery)) throw new ProviderException(Id, ProviderFailure.Unavailable,
                "Titlovi.com: pravila sajta trenutno ne dozvoljavaju automatski pristup ovom prevodu.");
            await ThrottleAsync(cancellationToken).ConfigureAwait(false);
            using var request = Request(uri);
            try { return await ProviderHttp.BytesAsync(httpClient, request, Id, cancellationToken).ConfigureAwait(false); }
            catch (ProviderException exception) when (exception.Failure == ProviderFailure.Authentication)
            {
                throw new ProviderException(Id, ProviderFailure.Unavailable,
                    "Titlovi.com: sajt trenutno odbija automatski pristup. Plejer ne zaobilazi zaštitu ili prijavu.");
            }
        }
        finally { requestGate.Release(); }
    }

    private async Task CheckRobotsAsync(CancellationToken cancellationToken)
    {
        if (robots is not null && clock.GetUtcNow() - robotsChecked < TimeSpan.FromHours(6)) return;
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);
        using var request = Request(new Uri(Site, "robots.txt"));
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) robots = RobotsPolicy.Parse("");
            else
            {
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 512 * 1024)
                    throw RobotsUnavailable();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > 512 * 1024) throw RobotsUnavailable();
                    output.Write(buffer, 0, read);
                }
                var text = Encoding.UTF8.GetString(output.ToArray());
                EnsureNoChallenge(text);
                if (text.TrimStart().StartsWith('<')) throw RobotsUnavailable();
                robots = RobotsPolicy.Parse(text);
            }
            robotsChecked = clock.GetUtcNow();
        }
        catch (HttpRequestException) { throw RobotsUnavailable(); }
        catch (IOException) { throw RobotsUnavailable(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw RobotsUnavailable(); }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var interval = robots?.CrawlDelay > minimumInterval ? robots.CrawlDelay : minimumInterval;
        var remaining = interval - (clock.GetUtcNow() - lastRequest);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
        lastRequest = clock.GetUtcNow();
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private Uri SiteUri(string? value)
    {
        if (!Uri.TryCreate(Site, value, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IdnHost != Site.IdnHost ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw InvalidResponse();
        return uri;
    }

    private void EnsureNoChallenge(string text)
    {
        if (text.Contains("_cf_chl_opt", StringComparison.OrdinalIgnoreCase) || text.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase) || text.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(Id, ProviderFailure.Unavailable,
                "Titlovi.com: sajt je zatražio proveru pristupa. Plejer ne zaobilazi tu proveru.");
    }

    private ProviderException InvalidResponse() => new(Id, ProviderFailure.InvalidResponse,
        "Titlovi.com: javna stranica je promenjena ili je vraćen neispravan prevod.");
    private ProviderException RobotsUnavailable() => new(Id, ProviderFailure.Unavailable,
        "Titlovi.com: pravila automatskog pristupa trenutno nije moguće proveriti. Pokušajte kasnije.");

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Text(string html) => WebUtility.HtmlDecode(Tags().Replace(html, " ")).Trim();
    private static bool NumericId(string? value) => value is { Length: > 0 and <= 12 } && value.All(char.IsAsciiDigit);
    private static string? Attribute(string attributes, string name) => Attributes().Matches(attributes).Cast<Match>()
        .Where(match => match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(match => WebUtility.HtmlDecode(match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value)).FirstOrDefault();
    private static bool HasClass(string attributes, string value) => Attribute(attributes, "class")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value) == true;
    private static string? FlagLanguage(string? source) => source?.Split('/').Last().ToLowerInvariant() switch
    {
        "rs3.png" or "rs4.png" or "rs.png" or "sr3.png" or "cir3.png" => "sr",
        "hr3.png" or "hr.png" => "hr", "ba3.png" or "ba.png" or "bs3.png" => "bs", _ => null
    };
    private static int? PageNumber(string? value)
    {
        if (!Uri.TryCreate(Site, value, out var uri) || uri.AbsolutePath != "/titlovi/") return null;
        return int.TryParse(QueryFields(uri.Query).GetValueOrDefault("pg"), out var page) ? page : null;
    }
    private static Dictionary<string, string> QueryFields(string query)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair[1]))) return [];
        }
        return fields;
    }

    private sealed record RobotsPolicy(IReadOnlyList<RobotRule> Rules, TimeSpan CrawlDelay)
    {
        public bool Allows(string path)
        {
            var matching = Rules.Where(rule => Regex.IsMatch(path, "^" + Regex.Escape(rule.Path.EndsWith('$') ? rule.Path[..^1] : rule.Path).Replace("\\*", ".*") +
                (rule.Path.EndsWith('$') ? "$" : ""), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                .OrderByDescending(rule => rule.Path.Replace("*", "").TrimEnd('$').Length).ThenByDescending(rule => rule.Allow).FirstOrDefault();
            return matching?.Allow ?? true;
        }

        public static RobotsPolicy Parse(string text)
        {
            var groups = new List<(List<string> Agents, List<RobotRule> Rules, double Delay)>();
            List<string> agents = [];
            List<RobotRule> rules = [];
            double delay = 0;
            bool directives = false;
            void Save() { if (agents.Count > 0) groups.Add((agents, rules, delay)); agents = []; rules = []; delay = 0; directives = false; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Split('#', 2)[0].Trim();
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts[1].Trim();
                if (key == "user-agent") { if (directives) Save(); agents.Add(value.ToLowerInvariant()); }
                else if (agents.Count > 0)
                {
                    directives = true;
                    if (key is "allow" or "disallow" && value.Length > 0) rules.Add(new RobotRule(value, key == "allow"));
                    else if (key == "crawl-delay" && double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    {
                        // Never ignore a published delay that cannot be scheduled reasonably.
                        // Decline automated access rather than making requests sooner than requested.
                        if (!double.IsFinite(seconds) || seconds > 3600) return new RobotsPolicy([new RobotRule("/", false)], TimeSpan.Zero);
                        delay = seconds;
                    }
                }
            }
            Save();
            int Specificity(string agent) => agent == "*" ? 0 : "stevanplejer".StartsWith(agent, StringComparison.OrdinalIgnoreCase) ? agent.Length : -1;
            var specificity = groups.SelectMany(group => group.Agents).Select(Specificity).DefaultIfEmpty(-1).Max();
            if (specificity < 0) return new RobotsPolicy([], TimeSpan.Zero);
            var selected = groups.Where(group => group.Agents.Any(agent => Specificity(agent) == specificity)).ToArray();
            return new RobotsPolicy(selected.SelectMany(group => group.Rules).ToArray(),
                TimeSpan.FromSeconds(selected.Select(group => group.Delay).DefaultIfEmpty(0).Max()));
        }
    }
    private sealed record RobotRule(string Path, bool Allow);

    [GeneratedRegex("<li\\b[^>]*class\\s*=\\s*[\"'][^\"']*\\bsubtitleContainer\\b[^\"']*[\"'][^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SubtitleStart();
    [GeneratedRegex("<h3\\b(?<attributes>[^>]*)>(?<body>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Heading();
    [GeneratedRegex("<a\\b(?<attributes>[^>]*)>(?<body>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Anchor();
    [GeneratedRegex("<h4\\b[^>]*>(?<body>.*?)</h4>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Release();
    [GeneratedRegex("<img\\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Images();
    [GeneratedRegex("<span\\b[^>]*>(.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Span();
    [GeneratedRegex("<span\\b[^>]*class\\s*=\\s*[\"']downloads[\"'][^>]*>(?<body>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex DownloadCount();
    [GeneratedRegex("<div\\b[^>]*class\\s*=\\s*[\"']paging[\"'][^>]*>(?<body>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex Paging();
    [GeneratedRegex("class\\s*=\\s*[\"'](?:searchTerms|results_count)[\"']", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SearchSummary();
    [GeneratedRegex("\\((?<year>\\d{4})\\)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Year();
    [GeneratedRegex("Sezona\\s+(?<season>\\d{1,2})(?:\\s+Epizoda\\s+(?<episode>\\d{1,3}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Episode();
    [GeneratedRegex("(?<name>[A-Za-z0-9_-]+)\\s*=\\s*(?:(?<quote>[\"'])(?<quoted>.*?)\\k<quote>|(?<bare>[^\\s>]+))", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Attributes();
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Tags();
    [GeneratedRegex("^/titlovi/[a-z0-9-]+-\\d{1,12}/$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DetailPath();
    [GeneratedRegex("\\s+aka\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TitleAliases();
}
