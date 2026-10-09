using System.Net;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class TitloviProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static PlayerSettings Settings() => new() { TitloviUsername = "dummy-user", TitloviPassword = "dummy-password" };
    private static MediaIdentity Movie() => new("Movie.2020.mkv", "Movie", 2020, null, null, 200_000);
    private static MediaIdentity Episode() => new("Show.S01E02.mkv", "Show", null, 1, 2, 200_000);
    private static string Token(string value = "dummy-token", int userId = 10, string expiration = "2026-10-08T14:00:00Z") =>
        System.Text.Json.JsonSerializer.Serialize(new { Token = value, UserId = userId, ExpirationDate = expiration });

    [Fact]
    public async Task MissingOrInvalidCredentialsDoNotContactTheService()
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        var provider = new TitloviProvider(client, new TestClock(Now));
        Assert.False(provider.IsConfigured(new PlayerSettings()));
        Assert.False(provider.IsConfigured(new PlayerSettings { TitloviUsername = "user", TitloviPassword = "pass\r\nheader" }));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), new PlayerSettings(), default));
        Assert.Equal(ProviderFailure.Configuration, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NormalApiLoginUsesObservedPostContractAndReturnsThreeLanguages()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("kodi.titlovi.com", request.RequestUri!.Host);
            Assert.Equal("/api/subtitles/gettoken", request.RequestUri.AbsolutePath);
            Assert.Contains("username=dummy-user", request.RequestUri.Query);
            Assert.Contains("password=dummy-password", request.RequestUri.Query);
            Assert.Contains("json=true", request.RequestUri.Query);
            return Json(Token());
        });
        handler.Respond(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/subtitles/search", request.RequestUri!.AbsolutePath);
            Assert.Contains("token=dummy-token", request.RequestUri.Query);
            Assert.Contains("userid=10", request.RequestUri.Query);
            Assert.Contains("lang=Srpski%7CCirilica%7CHrvatski%7CBosanski", request.RequestUri.AbsoluteUri);
            Assert.DoesNotContain("password", request.RequestUri.Query);
            return Json("""
                {"PagesAvailable":1,"SubtitleResults":[
                  {"Id":1,"Title":"Movie","Year":2020,"Lang":"Srpski","Link":"https://titlovi.com/download/1","Release":"Movie.2020.WEB","DownloadCount":20},
                  {"Id":2,"Title":"Movie","Year":2020,"Lang":"Cirilica","Link":"https://titlovi.com/download/2"},
                  {"Id":3,"Title":"Film aka Movie","Year":2020,"Lang":"Hrvatski","Link":"https://titlovi.com/download/3"},
                  {"Id":4,"Title":"Movie","Year":2020,"Lang":"Bosanski","Link":"https://titlovi.com/download/4"}
                ]}
                """);
        });
        using var client = handler.CreateClient();
        var results = await new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default);
        Assert.Equal(new[] { "sr", "sr", "hr", "bs" }, results.Select(candidate => candidate.Language));
        Assert.Equal(20, results[0].Downloads);
    }

    [Fact]
    public async Task CredentialQueryIsEncodedAndPasswordWhitespaceIsPreserved()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Contains("username=user%20%26%20name", request.RequestUri!.AbsoluteUri);
            Assert.Contains("password=%20pass%26%3F%20", request.RequestUri.AbsoluteUri);
            return Json(Token());
        });
        handler.Json("""{"PagesAvailable":0,"SubtitleResults":null}""");
        using var client = handler.CreateClient();
        var settings = new PlayerSettings { TitloviUsername = " user & name ", TitloviPassword = " pass&? " };
        Assert.Empty(await new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), settings, default));
    }

    [Fact]
    public async Task TokenIsCachedForTheSameAccountUntilExpiry()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        handler.Json("""{"SubtitleResults":[]}""");
        handler.Json("""{"SubtitleResults":[]}""");
        var clock = new TestClock(Now);
        using var client = handler.CreateClient();
        var provider = new TitloviProvider(client, clock);
        await provider.SearchAsync(Movie(), Settings(), default);
        clock.Advance(TimeSpan.FromMinutes(30));
        await provider.SearchAsync(Movie(), Settings(), default);
        Assert.Single(handler.Requests, uri => uri.AbsolutePath.EndsWith("gettoken", StringComparison.Ordinal));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task TokenNearExpiryIsRefreshedBeforeSearching()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        handler.Json("""{"SubtitleResults":[]}""");
        handler.Json(Token("renewed-token", expiration: "2026-10-08T18:00:00Z"));
        handler.Respond(request =>
        {
            Assert.Contains("token=renewed-token", request.RequestUri!.Query);
            return Json("""{"SubtitleResults":[]}""");
        });
        var clock = new TestClock(Now);
        using var client = handler.CreateClient();
        var provider = new TitloviProvider(client, clock);
        await provider.SearchAsync(Movie(), Settings(), default);
        clock.Advance(TimeSpan.FromMinutes(119) + TimeSpan.FromSeconds(10));
        await provider.SearchAsync(Movie(), Settings(), default);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath.EndsWith("gettoken", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ChangedPasswordCannotReuseAnotherAccountsToken()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token("first-token"));
        handler.Json("""{"SubtitleResults":[]}""");
        handler.Json(Token("second-token"));
        handler.Respond(request =>
        {
            Assert.Contains("token=second-token", request.RequestUri!.Query);
            return Json("""{"SubtitleResults":[]}""");
        });
        using var client = handler.CreateClient();
        var provider = new TitloviProvider(client, new TestClock(Now));
        await provider.SearchAsync(Movie(), Settings(), default);
        var changed = Settings();
        changed.TitloviPassword = "another-password";
        await provider.SearchAsync(Movie(), changed, default);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath.EndsWith("gettoken", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SearchAuthRejectionInvalidatesTheCachedSession()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        handler.Json("unauthorized", HttpStatusCode.Unauthorized);
        handler.Json(Token("fresh-token"));
        handler.Respond(request =>
        {
            Assert.Contains("token=fresh-token", request.RequestUri!.Query);
            return Json("""{"SubtitleResults":[]}""");
        });
        using var client = handler.CreateClient();
        var provider = new TitloviProvider(client, new TestClock(Now));
        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.Authentication, exception.Failure);
        await provider.SearchAsync(Movie(), Settings(), default);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath.EndsWith("gettoken", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task LoginDenialDoesNotSearchOrExposeTheAccount(int status)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("dummy-password", (HttpStatusCode)status);
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.Authentication, exception.Failure);
        Assert.DoesNotContain("dummy-password", exception.ToString());
        Assert.DoesNotContain("dummy-user", exception.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Token\":\"dummy-token\",\"UserId\":0,\"ExpirationDate\":\"2026-10-08T14:00:00Z\"}")]
    [InlineData("{\"Token\":\"dummy-token\",\"UserId\":10,\"ExpirationDate\":\"unknown\"}")]
    [InlineData("{\"Token\":null,\"UserId\":10,\"ExpirationDate\":\"2026-10-08T14:00:00Z\"}")]
    public async Task UnrecognizedLoginFieldsAreNotInvented(string response)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(response);
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AlreadyExpiredTokenIsNeverUsed()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token(expiration: "2026-10-08T11:00:00Z"));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.Authentication, exception.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EpisodeAndPackRulesRejectWrongOrUnknownEpisodes()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        handler.Respond(request =>
        {
            Assert.Contains("season=1", request.RequestUri!.Query);
            Assert.Contains("episode=2", request.RequestUri.Query);
            return Json("""
                {"SubtitleResults":[
                  {"Id":1,"Title":"Show","Lang":"Srpski","Season":1,"Episode":3,"Link":"https://titlovi.com/1"},
                  {"Id":2,"Title":"Show","Lang":"Srpski","Season":2,"Episode":2,"Link":"https://titlovi.com/2"},
                  {"Id":3,"Title":"Show","Lang":"Srpski","Link":"https://titlovi.com/3"},
                  {"Id":4,"Title":"Show","Lang":"Hrvatski","Season":1,"Episode":2,"Link":"https://titlovi.com/4"},
                  {"Id":5,"Title":"Show","Lang":"Bosanski","Season":1,"Episode":0,"Link":"https://titlovi.com/5"},
                  {"Id":6,"Title":"Show","Lang":"Srpski","Season":1,"Episode":null,"Link":"https://titlovi.com/6"}
                ]}
                """);
        });
        using var client = handler.CreateClient();
        var results = await new TitloviProvider(client, new TestClock(Now)).SearchAsync(Episode(), Settings(), default);
        Assert.Equal(new[] { "4", "5" }, results.Select(candidate => candidate.Id));
        Assert.All(results, candidate => Assert.Equal(2, candidate.Episode));
    }

    [Fact]
    public async Task MovieTitleYearAndMediaTypeAreChecked()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        handler.Json("""
            {"SubtitleResults":[
              {"Id":1,"Title":"Other Movie","Year":2020,"Lang":"Srpski","Link":"https://titlovi.com/1"},
              {"Id":2,"Title":"Movie","Year":1999,"Lang":"Srpski","Link":"https://titlovi.com/2"},
              {"Id":3,"Title":"Movie","Year":2020,"Lang":"English","Link":"https://titlovi.com/3"},
              {"Id":4,"Title":"Movie","Year":2020,"Season":1,"Episode":2,"Lang":"Srpski","Link":"https://titlovi.com/4"},
              {"Id":5,"Title":"Movie","Year":2020,"Lang":"Srpski","Link":"https://titlovi.com/5"}
            ]}
            """);
        using var client = handler.CreateClient();
        Assert.Equal("5", Assert.Single(await new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default)).Id);
    }

    [Fact]
    public async Task PaginationStopsAtThreePagesAndDeduplicatesIds()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(Token());
        const string page = """{"PagesAvailable":100,"SubtitleResults":[{"Id":1,"Title":"Movie","Year":2020,"Lang":"Srpski","Link":"https://titlovi.com/1"}]}""";
        handler.Json(page);
        handler.Json(page);
        handler.Json(page);
        using var client = handler.CreateClient();
        Assert.Single(await new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("pg=3", handler.Requests[^1].Query);
    }

    [Theory]
    [InlineData("https://eviltitlovi.com/download")]
    [InlineData("https://titlovi.com.evil.test/download")]
    [InlineData("http://titlovi.com/download")]
    [InlineData("https://username@titlovi.com/download")]
    public async Task DownloadHostMustBelongToTheProvider(string address)
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).DownloadAsync(
            new("1", "Titlovi.com", "sr", "Movie", address), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ArchiveDownloadAddsNoLoginCredentialsAndReturnsZipPayload()
    {
        using var handler = new ProviderTestHttpHandler();
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x01 };
        handler.Respond(request =>
        {
            Assert.Equal("titlovi.com", request.RequestUri!.Host);
            Assert.DoesNotContain("username", request.RequestUri.Query);
            Assert.DoesNotContain("password", request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var client = handler.CreateClient();
        var result = await new TitloviProvider(client, new TestClock(Now)).DownloadAsync(
            new("1", "Titlovi.com", "sr", "Movie", "https://titlovi.com/download/1"), Settings(), default);
        Assert.Equal("titlovi-1.sr.zip", result.FileName);
        Assert.Equal(bytes, result.Content);
    }

    [Theory]
    [InlineData("WEBVTT\n\n00:00.000 --> 00:01.000\nZdravo", ".vtt")]
    [InlineData("[Script Info]\n[Events]", ".ass")]
    [InlineData("1\n00:00:00,000 --> 00:00:01,000\nZdravo", ".srt")]
    public async Task RawTextFormatCanBeDetectedWithoutAnExtension(string text, string extension)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) });
        using var client = handler.CreateClient();
        var result = await new TitloviProvider(client, new TestClock(Now)).DownloadAsync(
            new("1", "Titlovi.com", "sr", "Movie", "https://titlovi.com/download/1"), Settings(), default);
        Assert.EndsWith(extension, result.FileName);
    }

    [Fact]
    public async Task UnsupportedRarIsReportedWithoutPretendingItIsASubtitle()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("Rar!"u8.ToArray()) });
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).DownloadAsync(
            new("1", "Titlovi.com", "sr", "Movie", "https://titlovi.com/download/1"), Settings(), default));
        Assert.Contains("RAR", exception.Message);
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
    }

    [Fact]
    public async Task TransportErrorsDoNotExposePasswordOrUsername()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request => throw new HttpRequestException("Cannot connect " + request.RequestUri));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.DoesNotContain("dummy-password", exception.ToString());
        Assert.DoesNotContain("dummy-user", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task LoginRedirectDoesNotFollowCredentialBearingLocation()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://evil.test/?password=dummy-password");
            return response;
        });
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new TitloviProvider(client, new TestClock(Now)).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.DoesNotContain("dummy-password", exception.ToString());
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan interval) => current += interval;
    }
}
