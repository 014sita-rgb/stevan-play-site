using System.Net;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class StremioOpenSubtitlesProviderTests
{
    private static MediaIdentity Movie() => new("C:\\Films\\Charade.1963.BluRay.mkv", "Charade", 1963,
        null, null, 123456789, "0123456789abcdef");
    private static StremioOpenSubtitlesProvider Create(HttpClient client) => new(client, TimeSpan.Zero);
    private const string Catalog = """{"metas":[{"id":"tt0056923","type":"movie","name":"Charade","releaseInfo":"1963"}]}""";
    private const string Subtitle = """{"subtitles":[{"id":"sr-one","lang":"srp","url":"https://subs.strem.io/one.srt","label":"Charade.1963.BluRay"}]}""";

    [Fact]
    public async Task FindsAnExactMovieAndPassesOnlyItsFilenameHashAndSizeWithoutCredentials()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Equal("https://v3-cinemeta.strem.io/catalog/movie/top/search=Charade.json", request.RequestUri!.AbsoluteUri);
            AssertCredentialsAbsent(request);
            return Json(Catalog);
        });
        handler.Respond(request =>
        {
            Assert.Equal("opensubtitles-v3.strem.io", request.RequestUri!.Host);
            Assert.Contains("/subtitles/movie/tt0056923/", request.RequestUri.AbsoluteUri);
            Assert.Contains("filename=Charade.1963.BluRay.mkv", request.RequestUri.AbsoluteUri);
            Assert.Contains("videoHash=0123456789abcdef", request.RequestUri.AbsoluteUri);
            Assert.Contains("videoSize=123456789", request.RequestUri.AbsoluteUri);
            Assert.DoesNotContain("Films", request.RequestUri.AbsoluteUri);
            AssertCredentialsAbsent(request);
            return Json(Subtitle);
        });
        using var client = handler.CreateClient();
        var settings = new PlayerSettings { OpenSubtitlesApiKey = "unused-personal-key", OpenSubtitlesToken = "unused-token" };
        Assert.True(Create(client).IsConfigured(new PlayerSettings()));
        var result = Assert.Single(await Create(client).SearchAsync(Movie(), settings, default));
        Assert.Equal("sr", result.Language);
        Assert.Equal("OpenSubtitles public", result.ProviderId);
        Assert.False(result.HashMatched);
    }

    [Fact]
    public async Task WrongTitlesYearsTypesAndUnsafeImdbIdsAreIgnored()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"metas":[
              {"id":"tt0011111","type":"movie","name":"The Christmas Charade","year":1963},
              {"id":"tt0022222","type":"movie","name":"Charade","year":2000},
              {"id":"tt0033333","type":"series","name":"Charade","year":1963},
              {"id":"tt0056923/evil","type":"movie","name":"Charade","year":1963},
              {"id":"tt0056923","type":"movie","name":"Charade","releaseInfo":"1963"}
            ]}
            """);
        handler.Json(Subtitle);
        using var client = handler.CreateClient();
        Assert.Single(await Create(client).SearchAsync(Movie(), new(), default));
        Assert.Contains("tt0056923", handler.Requests.Last().AbsolutePath);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownCatalogYearIsCheckedAgainstDetailedMetadata()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"metas":[{"id":"tt0056923","type":"movie","name":"Charade"}]}""");
        handler.Json("""{"meta":{"id":"tt0056923","type":"movie","name":"Charade","released":"1963-12-01T00:00:00Z"}}""");
        handler.Json(Subtitle);
        using var client = handler.CreateClient();
        Assert.Single(await Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal("/meta/movie/tt0056923.json", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task DetailedMetadataWithWrongYearNeverRequestsSubtitles()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"metas":[{"id":"tt0056923","type":"movie","name":"Charade"}]}""");
        handler.Json("""{"meta":{"id":"tt0056923","type":"movie","name":"Charade","year":2000}}""");
        using var client = handler.CreateClient();
        Assert.Empty(await Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MetadataRequestsAreBoundedToThree()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"metas":[
             {"id":"tt0000001","type":"movie","name":"Charade"},
             {"id":"tt0000002","type":"movie","name":"Charade"},
             {"id":"tt0000003","type":"movie","name":"Charade"},
             {"id":"tt0000004","type":"movie","name":"Charade"}
            ]}
            """);
        for (var id = 1; id <= 3; id++)
            handler.Json(System.Text.Json.JsonSerializer.Serialize(new
            { meta = new { id = "tt000000" + id, type = "movie", name = "Charade", year = 2000 } }));
        using var client = handler.CreateClient();
        Assert.Empty(await Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task AmbiguousTitlesWithoutYearAreReportedRatherThanPickingAnUnrelatedFilm()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"metas":[{"id":"tt0000001","type":"movie","name":"Charade","year":1963},
              {"id":"tt0000002","type":"movie","name":"Charade","year":2000}]}
            """);
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).SearchAsync(Movie() with { Year = null }, new(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
        Assert.Contains("godinu", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpisodeIdentityIsExactAndWrongEpisodeReleaseIsDiscarded()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"metas":[{"id":"tt0108778","type":"series","name":"Friends","releaseInfo":"1994-2004"}]}""");
        handler.Respond(request =>
        {
            Assert.Contains("/subtitles/series/tt0108778:2:3/", Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            Assert.DoesNotContain("videoHash", request.RequestUri.AbsoluteUri);
            return Json("""
                {"subtitles":[
                 {"id":"wrong","lang":"srp","url":"https://subs.strem.io/wrong.srt","label":"Friends.S02E04"},
                 {"id":"right","lang":"hrv","url":"https://subs.strem.io/right.srt","label":"Friends.S02E03"},
                 {"id":"generic","lang":"bos","url":"https://subs.strem.io/generic.srt"}
                ]}
                """);
        });
        using var client = handler.CreateClient();
        var media = new MediaIdentity("Friends.S02E03.mkv", "Friends", 1994, 2, 3, 100000);
        var results = await Create(client).SearchAsync(media, new(), default);
        Assert.Equal(new[] { "right", "generic" }, results.Select(candidate => candidate.Id));
        Assert.All(results, candidate => { Assert.Equal(2, candidate.Season); Assert.Equal(3, candidate.Episode); });
    }

    [Fact]
    public async Task SerbianCroatianAndBosnianAreNormalizedAndDuplicatesRemoved()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Catalog);
        handler.Json("""
            {"subtitles":[
              {"id":"one","lang":"srp","url":"https://subs.strem.io/one.srt"},
              {"id":"one","lang":"srp","url":"https://subs.strem.io/one.srt"},
              {"id":"two","lang":"hrv","url":"https://subs.strem.io/two.srt"},
              {"id":"three","lang":"bos","url":"https://subs.strem.io/three.srt"},
              {"id":"four","lang":"eng","url":"https://example.test/english.srt"}
            ]}
            """);
        using var client = handler.CreateClient();
        var results = await Create(client).SearchAsync(Movie(), new(), default);
        Assert.Equal(new[] { "sr", "hr", "bs" }, results.Select(candidate => candidate.Language));
    }

    [Theory]
    [InlineData("http://subs.strem.io/one.srt")]
    [InlineData("https://subs.strem.io.evil.test/one.srt")]
    [InlineData("https://evil.strem.io/one.srt")]
    [InlineData("https://user:pass@subs.strem.io/one.srt")]
    [InlineData("https://subs.strem.io:444/one.srt")]
    [InlineData("file:///tmp/one.srt")]
    public async Task UntrustedReturnedSubtitleUrlIsRejected(string url)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Catalog);
        handler.Json(System.Text.Json.JsonSerializer.Serialize(new { subtitles = new[] { new { id = "one", lang = "srp", url } } }));
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SelectedSubtitleDownloadsWithoutKeysAndPreservesUnicodeText()
    {
        using var handler = new ProviderTestHttpHandler();
        const string subtitle = "1\n00:00:01,000 --> 00:00:03,000\nČujem te. Шта кажеш?\n";
        handler.Respond(request =>
        {
            AssertCredentialsAbsent(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(subtitle, Encoding.UTF8) };
        });
        using var client = handler.CreateClient();
        var payload = await Create(client).DownloadAsync(new("../one", "OpenSubtitles public", "sr", "Charade",
            "https://subs.strem.io/one.srt"), new(), default);
        Assert.Equal("___one.sr.srt", payload.FileName);
        Assert.Equal(subtitle, Encoding.UTF8.GetString(payload.Content));
    }

    [Fact]
    public async Task DownloadRedirectCannotLeaveExactHttpsWhitelist()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.test/one.srt") }
        });
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).DownloadAsync(
            new("one", "OpenSubtitles public", "sr", "Charade", "https://subs.strem.io/one.srt"), new(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"metas\":{}}")]
    [InlineData("<html>challenge</html>")]
    public async Task UnsupportedCatalogResponsesAreSafeFailures(string response)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(response);
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(429, ProviderFailure.Quota)]
    [InlineData(503, ProviderFailure.Unavailable)]
    public async Task QuotaAndOutageRemainDistinct(int status, ProviderFailure failure)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("{}", (HttpStatusCode)status);
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(failure, error.Failure);
    }

    [Fact]
    public async Task NetworkFailureIsReportedWithoutRawServerDetails()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => throw new HttpRequestException("private network diagnostic"));
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => Create(client).SearchAsync(Movie(), new(), default));
        Assert.Equal(ProviderFailure.Unavailable, error.Failure);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task CancellationBeforeSearchMakesNoNetworkRequest()
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).SearchAsync(Movie(), new(), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task QuotaBackoffCanBeCancelledWithoutAnotherRequest()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("{}", (HttpStatusCode)429);
        using var client = handler.CreateClient();
        var provider = Create(client);
        await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), new(), default));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(Movie(), new(), cancellation.Token));
        Assert.Single(handler.Requests);
    }

    private static void AssertCredentialsAbsent(HttpRequestMessage request)
    {
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Api-Key"));
        Assert.Equal("StevanPlayer/1.0", request.Headers.UserAgent.ToString());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
