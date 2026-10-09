using System.Net;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class OpenSubtitlesProviderTests
{
    private static PlayerSettings Settings() => new() { OpenSubtitlesApiKey = "test-api-key", OpenSubtitlesToken = "test-token" };
    private static MediaIdentity Movie(string? hash = null) => new("Movie.2020.mkv", "Movie", 2020, null, null, 200_000, hash);
    private static MediaIdentity Episode() => new("Show.S01E02.mkv", "Show", null, 1, 2, 200_000);

    [Fact]
    public async Task MissingKeyDoesNotMakeANetworkRequest()
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        var provider = new OpenSubtitlesProvider(client);
        Assert.False(provider.IsConfigured(new PlayerSettings()));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.Configuration, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HashMatchWinsWithoutAFuzzyTitleRequest()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Contains("moviehash=0123456789abcdef", request.RequestUri!.Query);
            Assert.DoesNotContain("query=", request.RequestUri.Query);
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("Api-Key")));
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return Json("""
                {"total_pages":1,"data":[{"id":"12","attributes":{"language":"sr","moviehash_match":true,
                  "release":"Movie.2020.1080p","download_count":40,"files":[{"file_id":101,"file_name":"movie.srt"}]}}]}
                """);
        });
        using var client = handler.CreateClient();
        var results = await new OpenSubtitlesProvider(client).SearchAsync(Movie("0123456789abcdef"), Settings(), default);
        var candidate = Assert.Single(results);
        Assert.True(candidate.HashMatched);
        Assert.Equal("101", candidate.DownloadReference);
        Assert.Equal("sr", candidate.Language);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingHashMatchFallsBackToTitleAndYear()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"total_pages":1,"data":[]}""");
        handler.Respond(request =>
        {
            Assert.Contains("query=Movie", request.RequestUri!.Query);
            Assert.Contains("year=2020", request.RequestUri.Query);
            Assert.DoesNotContain("moviehash", request.RequestUri.Query);
            return Json("""{"data":[{"attributes":{"language":"hr","files":[{"file_id":3}],"feature_details":{"year":2020}}}]}""");
        });
        using var client = handler.CreateClient();
        var candidate = Assert.Single(await new OpenSubtitlesProvider(client).SearchAsync(Movie("0000000000000001"), Settings(), default));
        Assert.False(candidate.HashMatched);
        Assert.Equal("hr", candidate.Language);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WrongEpisodeAndUnknownEpisodeAreRejected()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"data":[
              {"attributes":{"language":"sr","feature_details":{"feature_type":"Episode","season_number":1,"episode_number":3},"files":[{"file_id":1}]}},
              {"attributes":{"language":"hr","feature_details":{"feature_type":"Episode","season_number":2,"episode_number":2},"files":[{"file_id":2}]}},
              {"attributes":{"language":"bs","files":[{"file_id":3}]}},
              {"attributes":{"language":"sr","feature_details":{"feature_type":"Episode","season_number":1,"episode_number":2},"files":[{"file_id":4}]}}
            ]}
            """);
        using var client = handler.CreateClient();
        var match = Assert.Single(await new OpenSubtitlesProvider(client).SearchAsync(Episode(), Settings(), default));
        Assert.Equal("4", match.Id);
        Assert.Equal(1, match.Season);
        Assert.Equal(2, match.Episode);
        Assert.Contains("season_number=1", Assert.Single(handler.Requests).Query);
        Assert.Contains("episode_number=2", Assert.Single(handler.Requests).Query);
    }

    [Fact]
    public async Task UnsupportedLanguagesForcedPartsAndWrongYearAreRejected()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"data":[
              {"attributes":{"language":"en","files":[{"file_id":1}]}},
              {"attributes":{"language":"sr","foreign_parts_only":true,"files":[{"file_id":2}]}},
              {"attributes":{"language":"hr","feature_details":{"year":1999},"files":[{"file_id":3}]}},
              {"attributes":{"language":"bs","feature_details":{"feature_type":"episode"},"files":[{"file_id":4}]}},
              {"attributes":{"language":"bs","feature_details":{"feature_type":"movie","year":2020},"files":[{"file_id":"5"}]}}
            ]}
            """);
        using var client = handler.CreateClient();
        Assert.Equal("5", Assert.Single(await new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default)).Id);
    }

    [Fact]
    public async Task FinalAllowedDownloadWithRemainingZeroIsUsableAndKeysStayOnApiHost()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.opensubtitles.com", request.RequestUri!.Host);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"file_id\":101", body);
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("Api-Key")));
            return Json("""{"link":"https://dl.opensubtitles.org/download/101","remaining":0,"file_name":"../../movie.srt"}""");
        });
        handler.Respond(request =>
        {
            Assert.Equal("dl.opensubtitles.org", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("Api-Key"));
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nZdravo\n", Encoding.UTF8)
            };
        });
        using var client = handler.CreateClient();
        var candidate = new SubtitleCandidate("101", "OpenSubtitles", "sr", "Movie", "101");
        var payload = await new OpenSubtitlesProvider(client).DownloadAsync(candidate, Settings(), default);
        Assert.Equal("movie.srt", payload.FileName);
        Assert.Contains("Zdravo", Encoding.UTF8.GetString(payload.Content));
    }

    [Theory]
    [InlineData("https://evilopensubtitles.org/file")]
    [InlineData("https://opensubtitles.org.evil.test/file")]
    [InlineData("http://dl.opensubtitles.org/file")]
    [InlineData("https://user@dl.opensubtitles.org/file")]
    [InlineData("https://dl.opensubtitles.org:8443/file")]
    public async Task UntrustedDownloadHostsAreRejected(string address)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(System.Text.Json.JsonSerializer.Serialize(new { link = address }));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client)
            .DownloadAsync(new("1", "OpenSubtitles", "sr", "Movie", "1"), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401, ProviderFailure.Authentication)]
    [InlineData(403, ProviderFailure.Authentication)]
    [InlineData(407, ProviderFailure.Quota)]
    [InlineData(429, ProviderFailure.Quota)]
    [InlineData(503, ProviderFailure.Unavailable)]
    public async Task ApiErrorsAreVisibleAndDoNotExposeCredentials(int status, ProviderFailure failure)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("test-api-key must never be shown", (HttpStatusCode)status);
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(failure, exception.Failure);
        Assert.DoesNotContain("test-api-key", exception.ToString());
        Assert.DoesNotContain("test-token", exception.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    public async Task MalformedApiResponsesAreReported(string response)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(response);
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnyRequest()
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    public async Task ApiRedirectIsRejectedWithoutExposingLocation(int status)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://evil.test/?secret=test-api-key");
            return response;
        });
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.DoesNotContain("evil.test", exception.ToString());
        Assert.DoesNotContain("test-api-key", exception.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OfficialSearchBestPracticesSortAndFormEncodeGetParameters()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("query=Some+Movie", query);
            Assert.DoesNotContain("%20", query);
            var names = query[1..].Split('&').Select(field => field.Split('=')[0]).ToArray();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
            return Json("""{"data":[]}""");
        });
        using var client = handler.CreateClient();
        await new OpenSubtitlesProvider(client).SearchAsync(Movie() with { Title = "Some Movie" }, Settings(), default);
    }

    [Fact]
    public async Task SameOriginApiRedirectIsFollowedAndKeepsAuthentication()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Redirect("/api/v1/subtitles/?languages=sr%2Chr%2Cbs"));
        handler.Respond(request =>
        {
            Assert.Equal("api.opensubtitles.com", request.RequestUri!.Host);
            Assert.Equal("/api/v1/subtitles/", request.RequestUri.AbsolutePath);
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("Api-Key")));
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return Json("""{"data":[]}""");
        });
        using var client = handler.CreateClient();
        Assert.Empty(await new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadApi307PreservesPostBodyOnTheSameOrigin()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Redirect("/api/v1/download/", HttpStatusCode.TemporaryRedirect));
        handler.Respond(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("\"file_id\":101", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("Api-Key")));
            return Json("""{"link":"https://dl.opensubtitles.org/file.srt"}""");
        });
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Zdravo") });
        using var client = handler.CreateClient();
        var payload = await new OpenSubtitlesProvider(client).DownloadAsync(
            new("101", "OpenSubtitles", "sr", "Movie", "101"), Settings(), default);
        Assert.Equal("Zdravo", Encoding.UTF8.GetString(payload.Content));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TrustedCdnRedirectCanChangeHostWithoutReceivingApiCredentials()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"link":"https://dl.opensubtitles.org/download/file.srt"}""");
        handler.Respond(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Api-Key"));
            return Redirect("https://www.opensubtitles.com/download/file.srt");
        });
        handler.Respond(request =>
        {
            Assert.Equal("www.opensubtitles.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Api-Key"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Zdravo") };
        });
        using var client = handler.CreateClient();
        var payload = await new OpenSubtitlesProvider(client).DownloadAsync(
            new("101", "OpenSubtitles", "sr", "Movie", "101"), Settings(), default);
        Assert.Equal("Zdravo", Encoding.UTF8.GetString(payload.Content));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://vip-api.opensubtitles.com/api/v1/subtitles")]
    [InlineData("http://api.opensubtitles.com/api/v1/subtitles")]
    [InlineData("https://api.opensubtitles.com/api/v1/subtitles?api_key=test-api-key")]
    [InlineData("https://api.opensubtitles.com/api/v1/subtitles?token=test-token")]
    public async Task ApiRedirectCannotChangeOriginDowngradeOrEchoOwnCredentials(string location)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => Redirect(location));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("test-api-key", exception.ToString());
        Assert.DoesNotContain("test-token", exception.ToString());
    }

    [Theory]
    [InlineData("https://evilopensubtitles.org/file.srt")]
    [InlineData("http://dl.opensubtitles.org/file.srt")]
    [InlineData("https://dl.opensubtitles.org/file.srt?api_key=test-api-key")]
    [InlineData("https://dl.opensubtitles.org/file.srt?token=test-token")]
    public async Task CdnRedirectCannotLeaveTrustedDomainsDowngradeOrExposeAccountCredentials(string location)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"link":"https://dl.opensubtitles.org/file.srt"}""");
        handler.Respond(_ => Redirect(location));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).DownloadAsync(
            new("101", "OpenSubtitles", "sr", "Movie", "101"), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("test-api-key", exception.ToString());
        Assert.DoesNotContain("test-token", exception.ToString());
    }

    [Fact]
    public async Task RedirectCyclesStopAfterThreeRedirects()
    {
        using var handler = new ProviderTestHttpHandler();
        for (var i = 0; i < 4; i++) handler.Respond(_ => Redirect("/api/v1/subtitles"));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new OpenSubtitlesProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Equal(4, handler.Requests.Count);
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Redirect)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
}
