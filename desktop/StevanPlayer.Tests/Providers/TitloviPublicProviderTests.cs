using System.IO.Compression;
using System.Net;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class TitloviPublicProviderTests
{
    private static MediaIdentity Movie() => new("Charade.1963.mkv", "Charade", 1963, null, null, 200_000);
    private static MediaIdentity Episode() => new("Breaking.Bad.S01E02.mkv", "Breaking Bad", 2008, 1, 2, 200_000);
    private static TitloviPublicProvider Provider(HttpClient client, TimeProvider? clock = null) => new(client, clock, TimeSpan.Zero);
    private static SubtitleCandidate Candidate(string reference = "https://titlovi.com/titlovi/charade-219219/") =>
        new("219219", "Titlovi.com", "sr", "720p.BRRip", reference);

    [Fact]
    public async Task AnonymousSearchReturnsTheThreeRequestedLanguagesWithoutSendingCredentials()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("titlovi.com", request.RequestUri!.Host);
            Assert.Contains("prijevod=Charade", request.RequestUri.Query);
            Assert.Contains("g=1963", request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Api-Key"));
            Assert.Contains("STEVANplejer", request.Headers.UserAgent.ToString());
            return Html(Search(Row("219219", flag: "rs3"), Row("111013", flag: "hr3"), Row("12345", flag: "ba3"),
                Row("396114", flag: "si3")));
        });
        using var client = handler.CreateClient();
        var provider = Provider(client);
        Assert.True(provider.IsConfigured(new PlayerSettings()));
        var result = await provider.SearchAsync(Movie(), new PlayerSettings(), default);
        Assert.Equal(new[] { "sr", "hr", "bs" }, result.Select(candidate => candidate.Language));
        Assert.Equal(2690, result[0].Downloads);
        Assert.Equal("720p.BRRip.H264.AC3.-.CODY", result[0].Release);
        Assert.Equal(Candidate().DownloadReference, result[0].DownloadReference);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExactTitleAndKnownYearExcludeRemakesAndSubstringMatches()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(_ => Html(Search(Row("1", "Charade", 2000), Row("2", "A Charade", 1963),
            Row("3", "Šarada aka Charade", 1963), Row("4", "Charade", 1963, episode: "Sezona 1 Epizoda 1"))));
        using var client = handler.CreateClient();
        var result = await Provider(client).SearchAsync(Movie(), new PlayerSettings(), default);
        Assert.Equal("3", Assert.Single(result).Id);
    }

    [Fact]
    public async Task EpisodeSearchIncludesOnlyItsEpisodeAndExplicitSameSeasonPacks()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(request =>
        {
            Assert.Contains("s=1", request.RequestUri!.Query);
            Assert.Contains("t=2", request.RequestUri.Query);
            Assert.DoesNotContain("g=", request.RequestUri.Query);
            // Search the season so whole-season archives are also available.
            Assert.DoesNotContain("e=", request.RequestUri.Query);
            return Html(Search(Row("1", "Breaking Bad", 2008, episode: "Sezona 1 Epizoda 2"),
                Row("2", "Breaking Bad", 2008, episode: "Sezona 1"),
                Row("3", "Breaking Bad", 2008, episode: "Sezona 1 Epizoda 3"),
                Row("4", "Breaking Bad", 2008, episode: "Sezona 2"),
                Row("5", "Breaking Bad", 2008)));
        });
        using var client = handler.CreateClient();
        var result = await Provider(client).SearchAsync(Episode(), new PlayerSettings(), default);
        Assert.Equal(new[] { "1", "2" }, result.Select(candidate => candidate.Id));
        Assert.All(result, candidate => { Assert.Equal(1, candidate.Season); Assert.Equal(2, candidate.Episode); });
    }

    [Fact]
    public async Task PublicPaginationIsBoundedAndDuplicateEntriesAreRemoved()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        for (int page = 1; page <= 3; page++)
        {
            var currentPage = page;
            handler.Respond(request =>
            {
                if (currentPage > 1) Assert.Contains("pg=" + currentPage, request.RequestUri!.Query);
                return Html(Search(Row("1"), Row((currentPage + 1).ToString()), nextPage: currentPage + 1));
            });
        }
        using var client = handler.CreateClient();
        var results = await Provider(client).SearchAsync(Movie(), new PlayerSettings(), default);
        Assert.Equal(4, results.Count);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task PublicDownloadUsesOnlyTheSelectedPagesPublishedMatchingLink()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(request =>
        {
            Assert.Equal("/titlovi/charade-219219/", request.RequestUri!.AbsolutePath);
            return Html("<a href='/download/?type=1&amp;mediaid=999'>another translation</a>" +
                "<a href='/download/?type=1&amp;mediaid=219219'>Skini titl</a>");
        });
        var bytes = ZipSubtitle("Charade.1963.srt", "1\n00:00:01,000 --> 00:00:03,000\nŠarada, čćžđ!\n\n");
        handler.Respond(request =>
        {
            Assert.Equal("/download/", request.RequestUri!.AbsolutePath);
            Assert.Equal("?type=1&mediaid=219219", request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var client = handler.CreateClient();
        var payload = await Provider(client).DownloadAsync(Candidate(), new PlayerSettings(), default);
        Assert.EndsWith(".zip", payload.FileName);
        var normalized = SubtitleArchive.Normalize(payload, Movie(), "sr");
        var cue = Assert.Single(SubtitleFormat.Parse(normalized));
        Assert.Equal("Šarada, čćžđ!", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
    }

    [Theory]
    [InlineData("https://other.example/titlovi/charade-219219/")]
    [InlineData("http://titlovi.com/titlovi/charade-219219/")]
    [InlineData("https://titlovi.com:444/titlovi/charade-219219/")]
    [InlineData("https://user@titlovi.com/titlovi/charade-219219/")]
    [InlineData("https://titlovi.com/titlovi/charade-219218/")]
    [InlineData("https://titlovi.com/download/?type=1&mediaid=219219")]
    public async Task UntrustedOrMismatchedCandidateDoesNotMakeAnyRequest(string reference)
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).DownloadAsync(Candidate(reference), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadCannotFollowAnOffSitePublishedLink()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(_ => Html("<a href='https://other.example/download/?type=1&amp;mediaid=219219'>download</a>"));
        using var client = handler.CreateClient();
        await Assert.ThrowsAsync<ProviderException>(() => Provider(client).DownloadAsync(Candidate(), new PlayerSettings(), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RobotsDownloadDisallowIsRespectedAfterThePermittedDetailPage()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Plain("User-agent: *\nDisallow: /download/\n"));
        handler.Respond(_ => Html("<a href='/download/?type=1&amp;mediaid=219219'>Skini titl</a>"));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).DownloadAsync(Candidate(), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.Unavailable, exception.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("User-agent: *\nDisallow: /titlovi/\n")]
    [InlineData("User-agent: STEVANplejer\nDisallow: /\nUser-agent: *\nAllow: /\n")]
    [InlineData("User-agent: *\nDisallow: /titlovi/*Charade*\n")]
    [InlineData("User-agent: *\nDisallow: /titlovi/?prijevod=Charade&sort=4&g=1963$\n")]
    [InlineData("User-agent: *\nCrawl-delay: 7200\n")]
    public async Task RobotsRulesStopSearchBeforeTheDeniedRequest(string rules)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Plain(rules));
        using var client = handler.CreateClient();
        await Assert.ThrowsAsync<ProviderException>(() => Provider(client).SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("User-agent: OtherBot\nDisallow: /\n")]
    [InlineData("User-agent: *\nDisallow: /\nAllow: /titlovi/\n")]
    [InlineData("User-agent: STEVANplejer\nAllow: /titlovi/\nUser-agent: *\nDisallow: /\n")]
    [InlineData("User-agent: *\nDisallow: /titlovi/\nAllow: /titlovi/\n")]
    public async Task RobotsAllowsUseSpecificGroupsAndLongestPathWithAllowWinningTies(string rules)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Plain(rules));
        handler.Respond(_ => Html(Search()));
        using var client = handler.CreateClient();
        Assert.Empty(await Provider(client).SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RobotsFailureAndChallengesDoNotBecomeFalseEmptyResults()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.Unavailable, exception.Failure);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("<html><script>window._cf_chl_opt={};</script></html>")]
    [InlineData("<html><div class='g-recaptcha'>Verification</div></html>")]
    [InlineData("<html><div class='cf-turnstile'>Verification</div></html>")]
    public async Task PublicChallengeStopsWithoutSolvingOrDownloading(string html)
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(_ => Html(html));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => Provider(client).SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.Unavailable, exception.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RobotsCacheExpiresAndNewRulesTakeEffect()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(_ => Html(Search()));
        handler.Respond(_ => Html(Search()));
        handler.Respond(_ => Plain("User-agent: *\nDisallow: /\n"));
        using var client = handler.CreateClient();
        var clock = new TestClock();
        var provider = Provider(client, clock);
        await provider.SearchAsync(Movie(), new PlayerSettings(), default);
        await provider.SearchAsync(Movie(), new PlayerSettings(), default);
        clock.Advance(TimeSpan.FromHours(7));
        await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath == "/robots.txt"));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task CancellationInterruptsTheSiteThrottleBeforeSearchIsSent()
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        using var client = handler.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var pending = new TitloviPublicProvider(client, minimumRequestInterval: TimeSpan.FromHours(1))
            .SearchAsync(Movie(), new PlayerSettings(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("<html>Access denied</html>")]
    [InlineData("this is not a subtitle")]
    public async Task AnErrorPageOrInvalidSubtitleIsNeverReturnedAsSuccessfulDownload(string content)
    {
        using var handler = new ProviderTestHttpHandler();
        RobotsMissing(handler);
        handler.Respond(_ => Html("<a href='/download/?type=1&amp;mediaid=219219'>Skini titl</a>"));
        handler.Respond(_ => Html(content));
        using var client = handler.CreateClient();
        await Assert.ThrowsAnyAsync<Exception>(() => Provider(client).DownloadAsync(Candidate(), new PlayerSettings(), default));
        Assert.Equal(3, handler.Requests.Count);
    }

    private static void RobotsMissing(ProviderTestHttpHandler handler) => handler.Respond(request =>
    {
        Assert.Equal("/robots.txt", request.RequestUri!.AbsolutePath);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Plain(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    private static string Search(params string[] rows) => Search(rows, null);
    private static string Search(string row1, string row2, int nextPage) => Search([row1, row2], nextPage);
    private static string Search(string[] rows, int? nextPage) =>
        "<div class='searchTerms'>Našli smo rezultata.</div><ul class='titlovi'>" + string.Join("", rows) + "</ul><div class='paging'>" +
        (nextPage.HasValue ? $"<a href='/titlovi/?prijevod=Charade&amp;pg={nextPage}'>Sljedeća</a>" : "") + "</div>";
    private static string Row(string id, string title = "Charade", int year = 1963, string flag = "rs3", string? episode = null) =>
        $"<li class='subtitleContainer canEdit'><h3 data-id='{id}'><a href='/titlovi/charade-{id}/'>{WebUtility.HtmlEncode(title)}</a> <i>({year})</i>" +
        (episode is null ? "" : "<span class='s0xe0y'>" + episode + "</span>") +
        "<div class='moviePopup'></div></h3><h4>720p.BRRip.H264.AC3.-.CODY<span class='fps'>fps: 23.976</span></h4>" +
        $"<div class='icons'><img class='lang' src='https://static.titlovi.com/images/flags/{flag}.png' alt='7' /></div>" +
        "<ul class='rating'><li class='empty'></li></ul><h5>Dodao: example</h5><div class='download'><span class='downloads'>2690</span></div></li>";
    private static byte[] ZipSubtitle(string name, string subtitle)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(subtitle);
        return memory.ToArray();
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
}
