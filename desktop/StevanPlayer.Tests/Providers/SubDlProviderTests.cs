using System.Net;
using System.Text;
using StevanPlayer.Core;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.Tests.Providers;

public sealed class SubDlProviderTests
{
    private static PlayerSettings Settings() => new() { SubDlApiKey = "subdl-dummy-key" };
    private static MediaIdentity Movie() => new("Movie.mkv", "Movie & Friends", 2020, null, null, 100_000);
    private static MediaIdentity Episode() => new("Show.S01E02.mkv", "Show", null, 1, 2, 100_000);

    [Fact]
    public async Task QueryIsEncodedAndLanguagesAreNormalized()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Equal("api.subdl.com", request.RequestUri!.Host);
            Assert.Contains("film_name=Movie%20%26%20Friends", request.RequestUri.AbsoluteUri);
            Assert.Contains("languages=SR%2CHR%2CBS", request.RequestUri.AbsoluteUri);
            Assert.Contains("api_key=subdl-dummy-key", request.RequestUri.AbsoluteUri);
            return Json("""
                {"status":true,"subtitles":[
                  {"name":"one.zip","language":"SR","url":"/subtitle/one.zip","releases":["Movie.2020.WEB-DL"]},
                  {"name":"two.zip","language":"Croatian","url":"/subtitle/two.zip"},
                  {"name":"three.zip","language":"BS","url":"/subtitle/three.zip"},
                  {"name":"four.zip","language":"English","url":"/subtitle/four.zip"}
                ]}
                """);
        });
        using var client = handler.CreateClient();
        var results = await new SubDlProvider(client).SearchAsync(Movie(), Settings(), default);
        Assert.Equal(new[] { "sr", "hr", "bs" }, results.Select(candidate => candidate.Language));
        Assert.Equal("https://dl.subdl.com/subtitle/one.zip", results[0].DownloadReference);
        Assert.Equal("Movie.2020.WEB-DL", results[0].Release);
    }

    [Fact]
    public async Task EpisodesRequireAnExactEpisodeOrAnExplicitContainingSeasonPack()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"status":true,"subtitles":[
              {"name":"wrong.zip","language":"SR","url":"/wrong.zip","season":1,"episode":3},
              {"name":"unknown.zip","language":"SR","url":"/unknown.zip"},
              {"name":"right.zip","language":"SR","url":"/right.zip","season":1,"episode":2},
              {"name":"pack.zip","language":"HR","url":"/pack.zip","season":1,"episode_from":1,"episode_end":8,"releases":["Show.S01E01-S01E08"]},
              {"name":"full.zip","language":"BS","url":"/full.zip","season":1,"episode":0,"full_season":true},
              {"name":"otherseason.zip","language":"SR","url":"/otherseason.zip","season":2,"episode":2},
              {"name":"outrange.zip","language":"SR","url":"/outrange.zip","season":1,"episode_from":3,"episode_end":8},
              {"name":"byrelease.zip","language":"SR","url":"/byrelease.zip","releases":["Show.1x02.WEB"]}
            ]}
            """);
        using var client = handler.CreateClient();
        var results = await new SubDlProvider(client).SearchAsync(Episode(), Settings(), default);
        Assert.Equal(new[] { "right.zip", "pack.zip", "full.zip", "byrelease.zip" }, results.Select(candidate => candidate.Id));
        Assert.All(results, candidate => Assert.Equal(1, candidate.Season));
        Assert.Equal(2, results[1].Episode);
        Assert.Equal(2, results[2].Episode);
        Assert.Contains("season_number=1", Assert.Single(handler.Requests).Query);
        Assert.Contains("episode_number=2", Assert.Single(handler.Requests).Query);
    }

    [Fact]
    public async Task WrongEpisodeMetadataIsNotOverriddenByReleaseText()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""
            {"status":true,"subtitles":[{"name":"wrong.zip","language":"SR","url":"/wrong.zip","season":1,"episode":3,"releases":["Show.S01E02"]}]}
            """);
        using var client = handler.CreateClient();
        Assert.Empty(await new SubDlProvider(client).SearchAsync(Episode(), Settings(), default));
    }

    [Fact]
    public async Task PaginationIsBoundedAndDuplicateArchivesAreRemoved()
    {
        using var handler = new ProviderTestHttpHandler();
        const string page = """{"status":true,"totalPages":200,"subtitles":[{"name":"one.zip","language":"SR","url":"/one.zip"}]}""";
        handler.Json(page);
        handler.Json(page);
        using var client = handler.CreateClient();
        Assert.Single(await new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=2", handler.Requests[1].Query);
    }

    [Theory]
    [InlineData("https://dl.subdl.com.evil.test/file.zip")]
    [InlineData("//evil.test/file.zip")]
    [InlineData("http://dl.subdl.com/file.zip")]
    [InlineData("file:///tmp/file.zip")]
    public async Task ReturnedUrlsCannotLeaveSubtitleCdn(string url)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = true, subtitles = new[] { new { name = "one.zip", language = "SR", url } }
        }));
        using var client = handler.CreateClient();
        var error = await Assert.ThrowsAsync<ProviderException>(() => new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ArchiveDownloadDoesNotForwardApiKeyAndPreservesZipBytes()
    {
        using var handler = new ProviderTestHttpHandler();
        var archiveBytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x01 };
        handler.Respond(request =>
        {
            Assert.Equal("dl.subdl.com", request.RequestUri!.Host);
            Assert.DoesNotContain("api_key", request.RequestUri.AbsoluteUri);
            Assert.False(request.Headers.Contains("Api-Key"));
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) };
        });
        using var client = handler.CreateClient();
        var payload = await new SubDlProvider(client).DownloadAsync(
            new("one.zip", "SubDL", "sr", "Movie", "https://dl.subdl.com/subtitle/one.zip"), Settings(), default);
        Assert.Equal("one.zip", payload.FileName);
        Assert.Equal(archiveBytes, payload.Content);
    }

    [Fact]
    public async Task TransportFailureNeverLeaksCredentialBearingUrl()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request => throw new HttpRequestException("Could not connect to " + request.RequestUri));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(ProviderFailure.Unavailable, exception.Failure);
        Assert.DoesNotContain("subdl-dummy-key", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("daily_limit", ProviderFailure.Quota)]
    [InlineData("rate_limit", ProviderFailure.Quota)]
    [InlineData("invalid_api_key", ProviderFailure.Authentication)]
    [InlineData("unrecognized_error", ProviderFailure.InvalidResponse)]
    public async Task JsonErrorsRemainDistinct(string error, ProviderFailure expected)
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json(System.Text.Json.JsonSerializer.Serialize(new { status = false, error }));
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
        Assert.Equal(expected, exception.Failure);
    }

    [Fact]
    public async Task NoResultsIsNotAnOutage()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Json("""{"status":false,"error":"can't find film"}""");
        using var client = handler.CreateClient();
        Assert.Empty(await new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
    }

    [Fact]
    public async Task OfficialResultMetadataPreventsDownloadingFuzzyWrongFilm()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(request =>
        {
            Assert.Contains("year=2020", request.RequestUri!.Query);
            Assert.Contains("client=custom_integration", request.RequestUri.Query);
            return Json("""
                {"status":true,"results":[{"name":"Another Movie","type":"movie","year":2020}],
                  "subtitles":[{"name":"wrong.zip","language":"SR","url":"/wrong.zip"}]}
                """);
        });
        using var client = handler.CreateClient();
        Assert.Empty(await new SubDlProvider(client).SearchAsync(Movie(), Settings(), default));
    }

    [Fact]
    public async Task OversizedDownloadIsRejectedBeforeReadingTheBody()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 17 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new SubDlProvider(client).DownloadAsync(
            new("one", "SubDL", "sr", "Movie", "https://dl.subdl.com/one.zip"), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
    }

    [Fact]
    public async Task MissingCredentialAndHeaderInjectionAreRejected()
    {
        using var handler = new ProviderTestHttpHandler();
        using var client = handler.CreateClient();
        var provider = new SubDlProvider(client);
        Assert.False(provider.IsConfigured(new PlayerSettings()));
        var settings = new PlayerSettings { SubDlApiKey = "key\r\nInjected: value" };
        Assert.False(provider.IsConfigured(settings));
        await Assert.ThrowsAsync<ProviderException>(() => provider.SearchAsync(Movie(), settings, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CdnRedirectIsRejectedWithoutFollowingAnUnverifiedUrl()
    {
        using var handler = new ProviderTestHttpHandler();
        handler.Respond(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.test/subtitle.zip");
            return response;
        });
        using var client = handler.CreateClient();
        var exception = await Assert.ThrowsAsync<ProviderException>(() => new SubDlProvider(client).DownloadAsync(
            new("one", "SubDL", "sr", "Movie", "https://dl.subdl.com/one.zip"), Settings(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, exception.Failure);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("evil.test", exception.ToString());
    }

    [Fact]
    public void ManualWebSourcesUseConfirmedAddressesAndExplicitAccessNotes()
    {
        Assert.Equal(new[] { "Titlovi.com", "Prijevodi-Titlovi.org", "Prijevodi Online" },
            ManualSubtitleSources.All.Select(source => source.Name));
        Assert.Equal(new Uri("https://www.prijevodi-online.org/"),
            ManualSubtitleSources.All.Single(source => source.Name == "Prijevodi Online").Website);
        Assert.All(ManualSubtitleSources.All, source => Assert.False(string.IsNullOrWhiteSpace(source.AccessNote)));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
}
