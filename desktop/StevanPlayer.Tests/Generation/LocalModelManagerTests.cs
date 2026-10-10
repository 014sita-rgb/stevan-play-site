using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StevanPlayer.Core.Generation;

namespace StevanPlayer.Tests.Generation;

public sealed class LocalModelManagerTests
{
    private const string Revision = "1234567890abcdef1234567890abcdef12345678";
    private static readonly byte[] SpeechBytes = Encoding.UTF8.GetBytes("fixture speech model bytes");
    private static readonly byte[] TranslationBytes = Encoding.UTF8.GetBytes("fixture translation model bytes");

    [Fact]
    public async Task DownloadsBothModelsUsingImmutableRevisionAndVerifiesOffline()
    {
        using var directory = new TemporaryDirectory();
        var handler = StandardHandler();
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        Assert.False(manager.IsInstalled);
        await manager.DownloadAsync(null, default);
        Assert.True(manager.IsInstalled);
        Assert.Equal(SpeechBytes, await File.ReadAllBytesAsync(manager.SpeechModelPath));
        Assert.Equal(TranslationBytes, await File.ReadAllBytesAsync(manager.TranslationModelPath));
        Assert.Equal(4, handler.Calls);
        await manager.VerifyAsync(default);
        await manager.DownloadAsync(null, default);
        Assert.Equal(4, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        Assert.DoesNotContain("Authorization", await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "local-models.json")));
    }

    [Fact]
    public async Task AcceptsUniqueOfficialFilenameCasingAndSha256Oid()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal))
            {
                var speech = request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal);
                return Json(new
                {
                    sha = Revision,
                    siblings = new[]
                    {
                        new
                        {
                            rfilename = speech ? "ggml-small.bin" : "qwen3-4b-q4_k_m.gguf",
                            lfs = new { oid = "sha256:" + Hash(speech ? SpeechBytes : TranslationBytes), size = (speech ? SpeechBytes : TranslationBytes).Length }
                        }
                    }
                });
            }
            if (request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal))
                return Bytes(SpeechBytes);
            Assert.EndsWith("/qwen3-4b-q4_k_m.gguf", request.RequestUri.AbsolutePath);
            return Bytes(TranslationBytes);
        });
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        Assert.EndsWith("Qwen3-4B-Q4_K_M.gguf", manager.TranslationModelPath);
        await manager.VerifyAsync(default);
    }

    [Fact]
    public async Task BadChecksumDoesNotInstallOrRetainPartialFile()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
            ? Metadata(true)
            : Bytes(Enumerable.Repeat((byte)'x', SpeechBytes.Length).ToArray()));
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync(null, default));
        Assert.False(manager.IsInstalled);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task TruncatedOrOversizedStreamCannotInstall(int delta)
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(true);
            var response = Bytes(new byte[SpeechBytes.Length + delta]);
            response.Content.Headers.ContentLength = SpeechBytes.Length;
            return response;
        });
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadAsync(null, default));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task WrongContentLengthRejectedBeforeInstalling()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(true);
            var response = Bytes(SpeechBytes);
            response.Content.Headers.ContentLength = SpeechBytes.Length + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancellationDuringStreamingDeletesPartialFile()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(true);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellingStream(SpeechBytes, cancellation))
            };
        });
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DownloadAsync(null, cancellation.Token));
        Assert.False(manager.IsInstalled);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("https://example.com/model.bin")]
    [InlineData("http://cdn-lfs.hf.co/model.bin")]
    [InlineData("https://huggingface.co.attacker.example/model.bin")]
    [InlineData("https://user:pass@huggingface.co/model.bin")]
    [InlineData("https://huggingface.co:8443/model.bin")]
    public async Task ForeignOrUnsafeRedirectNotFollowed(string destination)
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(true);
            return Redirect(destination);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Equal(2, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task TrustedCdnRedirectWorksWithoutAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal));
            if (request.RequestUri.Host == "huggingface.co")
                return Redirect("https://cdn-lfs.hf.co/" + (request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal) ? "speech" : "translation"));
            Assert.Equal("cdn-lfs.hf.co", request.RequestUri.Host);
            return Bytes(request.RequestUri.AbsolutePath == "/speech" ? SpeechBytes : TranslationBytes);
        });
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        Assert.Equal(6, handler.Calls);
        await manager.VerifyAsync(default);
    }

    [Fact]
    public async Task RedirectLoopsAreBounded()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
            ? Metadata(true)
            : Redirect("https://cdn-lfs.hf.co/loop"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Equal(7, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("missing-lfs")]
    [InlineData("invalid-hash")]
    [InlineData("oversize")]
    [InlineData("invalid-size")]
    [InlineData("duplicate-filename")]
    [InlineData("floating-revision")]
    [InlineData("foreign-repository")]
    public async Task InvalidOfficialMetadataDoesNotDownloadModel(string kind)
    {
        using var directory = new TemporaryDirectory();
        var sibling = new Dictionary<string, object?>
        {
            ["rfilename"] = "ggml-small.bin"
        };
        if (kind != "missing-lfs")
            sibling["lfs"] = new
            {
                sha256 = kind == "invalid-hash" ? "not-a-checksum" : Hash(SpeechBytes),
                size = kind == "invalid-size" ? (object)"24" : kind == "oversize" ? 6L * 1024 * 1024 * 1024 + 1 : SpeechBytes.Length
            };
        var handler = new FakeHandler(_ => Json(new
        {
            id = kind == "foreign-repository" ? "someone/else" : "ggerganov/whisper.cpp",
            sha = kind == "floating-revision" ? "main" : Revision,
            siblings = kind == "duplicate-filename" ? new[] { sibling, sibling } : [sibling]
        }));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MetadataSizeIsBounded()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(_ => Bytes(new byte[2 * 1024 * 1024 + 1]));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task MetadataWithoutContentLengthIsStillBounded()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[2 * 1024 * 1024 + 1]))
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalModelManager(client, directory.Path).DownloadAsync(null, default));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RetryKeepsPreviouslyVerifiedModelAfterSecondDownloadFails()
    {
        using var directory = new TemporaryDirectory();
        var failTranslation = true;
        var speechRequests = 0;
        var handler = new FakeHandler(request =>
        {
            var speech = request.RequestUri!.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal);
            if (speech) speechRequests++;
            if (request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return Metadata(speech);
            return !speech && failTranslation
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Bytes(speech ? SpeechBytes : TranslationBytes);
        });
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.DownloadAsync(null, default));
        Assert.False(manager.IsInstalled);
        Assert.True(File.Exists(manager.SpeechModelPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
        failTranslation = false;
        await manager.DownloadAsync(null, default);
        Assert.Equal(2, speechRequests);
        Assert.Equal(6, handler.Calls);
        await manager.VerifyAsync(default);
    }

    [Fact]
    public async Task SameSizeTamperingFailsIndependentVerification()
    {
        using var directory = new TemporaryDirectory();
        var handler = StandardHandler();
        using var client = new HttpClient(handler);
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        await File.WriteAllBytesAsync(manager.TranslationModelPath, Enumerable.Repeat((byte)'x', TranslationBytes.Length).ToArray());
        Assert.True(manager.IsInstalled); // Availability does not substitute for the hash check.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAsync(default));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task IncorrectLengthAndMissingReceiptAreNotInstalled()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(StandardHandler());
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        await File.AppendAllTextAsync(manager.SpeechModelPath, "changed");
        Assert.False(manager.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAsync(default));
        File.Delete(System.IO.Path.Combine(directory.Path, "local-models.json"));
        Assert.False(manager.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAsync(default));
    }

    [Fact]
    public async Task CorruptReceiptCannotPassAvailabilityOrVerification()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(StandardHandler());
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "local-models.json"), "{broken");
        Assert.False(manager.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAsync(default));
    }

    [Fact]
    public async Task ReceiptCannotSelectForeignRepositoryOrPath()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(StandardHandler());
        var manager = new LocalModelManager(client, directory.Path);
        await manager.DownloadAsync(null, default);
        var receiptPath = System.IO.Path.Combine(directory.Path, "local-models.json");
        var receipt = await File.ReadAllTextAsync(receiptPath);
        await File.WriteAllTextAsync(receiptPath, receipt.Replace("ggml-small.bin", "../foreign.bin", StringComparison.Ordinal));
        Assert.False(manager.IsInstalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.VerifyAsync(default));
    }

    [Fact]
    public void RejectsCredentialBearingClient()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(StandardHandler());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-only");
        Assert.Throws<InvalidOperationException>(() => new LocalModelManager(client, directory.Path));
    }

    private static FakeHandler StandardHandler() => new(request =>
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("huggingface.co", request.RequestUri!.Host);
        Assert.Null(request.Headers.Authorization);
        if (request.RequestUri.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal))
        {
            Assert.Equal("?blobs=true", request.RequestUri.Query);
            return Metadata(request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal));
        }
        Assert.Contains("/resolve/" + Revision + "/", request.RequestUri.AbsolutePath);
        Assert.DoesNotContain("/resolve/main/", request.RequestUri.AbsolutePath);
        return Bytes(request.RequestUri.AbsolutePath.Contains("whisper.cpp", StringComparison.Ordinal) ? SpeechBytes : TranslationBytes);
    });

    private static HttpResponseMessage Metadata(bool speech) => Json(new
    {
        id = speech ? "ggerganov/whisper.cpp" : "Qwen/Qwen3-4B-GGUF",
        sha = Revision,
        siblings = new[]
        {
            new
            {
                rfilename = speech ? "ggml-small.bin" : "Qwen3-4B-Q4_K_M.gguf",
                lfs = new { sha256 = Hash(speech ? SpeechBytes : TranslationBytes), size = (speech ? SpeechBytes : TranslationBytes).Length }
            }
        }
    });

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string destination)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(destination);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(handle(request));
        }
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Read(buffer.Span);
            cancellation.Cancel();
            return ValueTask.FromResult(count);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StevanPlayer-model-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
