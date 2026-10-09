using System.Net;
using System.Text;
using System.Text.Json;
using StevanPlayer.Core;
using StevanPlayer.Core.Generation;

namespace StevanPlayer.Tests.Generation;

public sealed class OpenAiSubtitleGeneratorTests
{
    [Fact]
    public async Task ConsentIsRequiredBeforeExtractionOrAnyRequest()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP call"));
        using var client = new HttpClient(handler);
        var settings = ConfiguredSettings();
        settings.AllowCloudTranscription = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAiSubtitleGenerator(client, extractor).GenerateAsync(media.Path, "sr", settings, null, default));
        Assert.Equal(0, extractor.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("key\r\nInjected: value")]
    public async Task InvalidKeyDoesNotExtractOrSend(string key)
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP call"));
        using var client = new HttpClient(handler);
        var settings = ConfiguredSettings();
        settings.AiApiKey = key;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenAiSubtitleGenerator(client, extractor).GenerateAsync(media.Path, "sr", settings, null, default));
        Assert.Equal(0, extractor.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MultipartTranscriptionAndStructuredTranslationPreserveSegmentOffsets()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor(TimeSpan.Zero, TimeSpan.FromMinutes(10));
        var transcriptionCalls = 0;
        var translationCalls = 0;
        var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("user-owned-test-key", request.Headers.Authorization.Parameter);
            if (request.RequestUri.AbsolutePath == "/v1/audio/transcriptions")
            {
                transcriptionCalls++;
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                var fields = multipart.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal("whisper-1", await fields["model"].ReadAsStringAsync(token));
                Assert.Equal("verbose_json", await fields["response_format"].ReadAsStringAsync(token));
                Assert.Equal("segment", await fields["timestamp_granularities[]"].ReadAsStringAsync(token));
                Assert.Equal("audio.wav", fields["file"].Headers.ContentDisposition!.FileName!.Trim('"'));
                Assert.Equal("audio/wav", fields["file"].Headers.ContentType!.MediaType);
                Assert.NotEmpty(await fields["file"].ReadAsByteArrayAsync(token));
                return Transcription((0.5, 1.5, "Hello."));
            }
            translationCalls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("gpt-4.1-mini", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            return Translation((0, "Zdravo."), (1, "Zdravo ponovo."));
        });
        using var client = new HttpClient(handler);
        var cues = await new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default);
        Assert.Equal(2, transcriptionCalls);
        Assert.Equal(1, translationCalls);
        Assert.Collection(cues,
            cue => Assert.Equal(new SubtitleCue(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), "Zdravo."), cue),
            cue => Assert.Equal(new SubtitleCue(TimeSpan.FromSeconds(600.5), TimeSpan.FromSeconds(601.5), "Zdravo ponovo."), cue));
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Theory]
    [InlineData("sr", "Serbian")]
    [InlineData("hr", "Croatian")]
    [InlineData("bs", "Bosnian")]
    public async Task SelectsRequestedTargetLanguage(string language, string expected)
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("transcriptions", StringComparison.Ordinal))
                return Transcription((0, 1, "Hello"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Contains(expected, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Translation((0, "Zdravo"));
        });
        using var client = new HttpClient(handler);
        Assert.Single(await new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, language, ConfiguredSettings(), null, default));
    }

    [Fact]
    public async Task BatchesLargeTranscriptWithoutOmissionsOrTimestampChanges()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var batches = new List<int>();
        var handler = new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("transcriptions", StringComparison.Ordinal))
                return Transcription(Enumerable.Range(0, 123).Select(index => ((double)index, index + 0.9, "Cue " + index)).ToArray());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var items = input.RootElement.EnumerateArray().ToArray();
            batches.Add(items.Length);
            return Translation(items.Select(item =>
                (item.GetProperty("index").GetInt32(), "Prevod " + item.GetProperty("text").GetString())).ToArray());
        });
        using var client = new HttpClient(handler);
        var cues = await new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default);
        Assert.Equal(new[] { 50, 50, 23 }, batches);
        Assert.Equal(123, cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(i), cues[i].Start);
            Assert.Equal(TimeSpan.FromSeconds(i + 0.9), cues[i].End);
            Assert.Equal("Prevod Cue " + i, cues[i].Text);
        }
    }

    [Fact]
    public async Task AlsoBatchesByCharacters()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var batchSizes = new List<int>();
        var handler = new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("transcriptions", StringComparison.Ordinal))
                return Transcription((0, 1, new string('a', 6000)), (1, 2, new string('b', 6000)), (2, 3, new string('c', 6000)));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var count = input.RootElement.GetArrayLength();
            batchSizes.Add(count);
            return Translation(Enumerable.Range(0, count).Select(index => (index, "Prevod")).ToArray());
        });
        using var client = new HttpClient(handler);
        Assert.Equal(3, (await new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "hr", ConfiguredSettings(), null, default)).Count);
        Assert.Equal(new[] { 2, 1 }, batchSizes);
    }

    [Theory]
    [InlineData("{\"cues\":[]}")]
    [InlineData("{\"cues\":[{\"index\":5,\"text\":\"Zdravo\"}]}")]
    [InlineData("{\"cues\":[{\"index\":0,\"text\":\" \"}]}")]
    [InlineData("not JSON")]
    public async Task RejectsMalformedTranslationAndCleansTemporaryAudio(string response)
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("transcriptions", StringComparison.Ordinal)
                ? Transcription((0, 1, "Hello")) : TranslationContent(response)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default));
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Theory]
    [InlineData("{\"text\":\"missing timestamps\"}")]
    [InlineData("{\"segments\":[{\"start\":-1,\"end\":1,\"text\":\"bad start\"}]}")]
    [InlineData("{\"segments\":[{\"start\":1,\"end\":1,\"text\":\"zero length\"}]}")]
    [InlineData("not JSON")]
    public async Task RejectsMalformedTranscriptionBeforeTranslation(string response)
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(response)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default));
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Fact]
    public async Task QuotaErrorDoesNotRetryOrExposeResponse()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("SECRET_ACCOUNT_DETAIL user-owned-test-key")
        }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default));
        Assert.Contains("kvota", exception.Message);
        Assert.DoesNotContain("SECRET_ACCOUNT_DETAIL", exception.ToString());
        Assert.DoesNotContain("user-owned-test-key", exception.ToString());
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new string('x', 4 * 1024 * 1024 + 1))));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, default));
        Assert.Contains("veličine", exception.Message);
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Fact]
    public async Task CancellationPropagatesAndRemovesExtractedAudio()
    {
        using var media = new TemporaryMedia();
        using var cancellation = new CancellationTokenSource();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, cancellation.Token));
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotExtractOrSend()
    {
        using var media = new TemporaryMedia();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP call"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "sr", ConfiguredSettings(), null, cancellation.Token));
        Assert.Equal(0, extractor.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EmptySpeechDoesNotProduceFakeSubtitles()
    {
        using var media = new TemporaryMedia();
        var extractor = new FakeExtractor();
        var handler = new FakeHandler((_, _) => Task.FromResult(Transcription()));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OpenAiSubtitleGenerator(client, extractor)
            .GenerateAsync(media.Path, "bs", ConfiguredSettings(), null, default));
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(extractor.OutputDirectory));
    }

    private static PlayerSettings ConfiguredSettings() => new()
    {
        AllowCloudTranscription = true,
        AiApiKey = "user-owned-test-key",
        TranslationModel = "gpt-4.1-mini"
    };

    private static HttpResponseMessage Transcription(params (double Start, double End, string Text)[] cues) =>
        JsonResponse(JsonSerializer.Serialize(new { segments = cues.Select(cue => new { start = cue.Start, end = cue.End, text = cue.Text }) }));

    private static HttpResponseMessage Translation(params (int Index, string Text)[] cues) =>
        TranslationContent(JsonSerializer.Serialize(new { cues = cues.Select(cue => new { index = cue.Index, text = cue.Text }) }));

    private static HttpResponseMessage TranslationContent(string content) =>
        JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class FakeExtractor(params TimeSpan[] starts) : IAudioExtractor
    {
        public int Calls { get; private set; }
        public string? OutputDirectory { get; private set; }
        public async Task<IReadOnlyList<AudioSegment>> ExtractAsync(string mediaPath, string outputDirectory,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            OutputDirectory = outputDirectory;
            var offsets = starts.Length == 0 ? new[] { TimeSpan.Zero } : starts;
            var result = new List<AudioSegment>();
            foreach (var start in offsets)
            {
                var path = System.IO.Path.Combine(outputDirectory, result.Count + ".wav");
                await File.WriteAllBytesAsync(path, "RIFF-test-fixture"u8.ToArray(), cancellationToken);
                result.Add(new AudioSegment(path, start));
            }
            return result;
        }
    }

    private sealed class TemporaryMedia : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();
        public void Dispose() => File.Delete(Path);
    }
}
