using StevanPlayer.Core;
using StevanPlayer.Core.Generation;

namespace StevanPlayer.Tests.Generation;

/// <summary>Tests pipeline contracts with fakes; these tests do not perform model inference.</summary>
public sealed class LocalSubtitleGeneratorTests
{
    [Theory]
    [InlineData("sr", "Zdravo.")]
    [InlineData("hr", "Bok.")]
    [InlineData("bs", "Zdravo svima.")]
    public async Task KeylessPipelinePreservesTenMinuteOffsetsAndReleasesSpeechModelBeforeTranslation(string language, string text)
    {
        using var fixture = new Fixture(TimeSpan.Zero, TimeSpan.FromSeconds(600));
        fixture.Recognizer.Recognize = (segment, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>(
            [Cue(segment.Start.TotalSeconds + 0.5, segment.Start.TotalSeconds + 1.5, "Hello.")]);
        fixture.Translator.Translate = (cues, selectedLanguage, _) =>
        {
            Assert.Equal(language, selectedLanguage);
            Assert.True(fixture.Recognizer.Disposed);
            Assert.Equal(new[] { 0.5, 600.5 }, cues.Select(cue => cue.Start.TotalSeconds));
            return Task.FromResult<IReadOnlyList<SubtitleCue>>(cues.Select(cue => cue with { Text = text }).ToArray());
        };
        var settings = new PlayerSettings
        {
            UseLocalAi = true,
            AllowCloudTranscription = false,
            // These deliberately unusable legacy values must never be consumed by the local pipeline.
            AiApiKey = "ignored\r\nlegacy-value",
            OpenSubtitlesApiKey = "",
            SubDlApiKey = "",
            TranslationModel = ""
        };

        var result = await fixture.Generator.GenerateAsync(fixture.MediaPath, language, settings, null, default);

        Assert.Equal(new[] { Cue(0.5, 1.5, text), Cue(600.5, 601.5, text) }, result);
        Assert.Equal(new[] { "verify", "extract", "create-speech", "speech:0", "speech:600", "dispose-speech",
            "create-translation", "translate", "dispose-translation" }, fixture.Events);
        Assert.True(fixture.Translator.Disposed);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Theory]
    [InlineData(false, "sr")]
    [InlineData(true, "en")]
    public async Task DisabledAiOrUnsupportedLanguageFailsBeforeModelVerification(bool enabled, string language)
    {
        using var fixture = new Fixture();
        var settings = new PlayerSettings { UseLocalAi = enabled };
        if (enabled)
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Generator.GenerateAsync(fixture.MediaPath, language, settings, null, default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Generator.GenerateAsync(fixture.MediaPath, language, settings, null, default));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task MissingMediaFailsBeforeModelsAndExtraction()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Generator.GenerateAsync(
            Path.Combine(fixture.DirectoryPath, "missing.mkv"), "sr", new PlayerSettings(), null, default));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task ModelVerificationFailureNeverExtractsAudioOrCreatesNativeEngines()
    {
        using var fixture = new Fixture();
        fixture.Verify = _ => Task.FromException(new InvalidDataException("Model checksum mismatch"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.Equal(new[] { "verify" }, fixture.Events);
        Assert.Null(fixture.Extractor.OutputDirectory);
        Assert.True(File.Exists(fixture.MediaPath));
    }

    [Fact]
    public async Task CancellationDuringModelVerificationNeverBeginsAudioExtraction()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Verify = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.GenerateAsync(cancellation.Token));
        Assert.Equal(new[] { "verify" }, fixture.Events);
        Assert.Null(fixture.Extractor.OutputDirectory);
    }

    [Fact]
    public async Task CancelledRequestNeverVerifiesOrExtracts()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.GenerateAsync(cancellation.Token));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task ExtractionFailureDeletesOnlyOwnedTemporaryAudio()
    {
        using var fixture = new Fixture();
        fixture.Extractor.AfterWrite = _ => throw new IOException("Extraction failed");
        await Assert.ThrowsAsync<IOException>(() => fixture.GenerateAsync());
        Assert.Equal(new[] { "verify", "extract" }, fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task CancellationDuringRecognitionDisposesEngineAndRemovesTemporaryAudio()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Recognizer.Recognize = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new Xunit.Sdk.XunitException("Cancellation should have thrown");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.GenerateAsync(cancellation.Token));
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task CancelledTranslationCannotReturnPartialSubtitles()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Translator.Translate = (cues, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(cues);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.GenerateAsync(cancellation.Token));
        Assert.True(fixture.Recognizer.Disposed);
        Assert.True(fixture.Translator.Disposed);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task RecognitionFailureNeverStartsTranslator()
    {
        using var fixture = new Fixture();
        fixture.Recognizer.Recognize = (_, _) => Task.FromException<IReadOnlyList<SubtitleCue>>(new IOException("Native recognition failed"));
        await Assert.ThrowsAsync<IOException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task TranslationFailureDisposesBothModelsAndRemovesOwnedAudio()
    {
        using var fixture = new Fixture();
        fixture.Translator.Translate = (_, _, _) => Task.FromException<IReadOnlyList<SubtitleCue>>(new IOException("Native translation failed"));
        await Assert.ThrowsAsync<IOException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.True(fixture.Translator.Disposed);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task ExtractorCannotPassAPathOutsideTheOwnedDirectoryToSpeechEngine()
    {
        using var fixture = new Fixture();
        var externalPath = Path.Combine(fixture.DirectoryPath, "keep-this.wav");
        File.WriteAllText(externalPath, "external audio must remain untouched");
        fixture.Extractor.ReplaceSegments = _ => [new AudioSegment(externalPath, TimeSpan.Zero)];
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.Equal(new[] { "verify", "extract" }, fixture.Events);
        Assert.Equal("external audio must remain untouched", File.ReadAllText(externalPath));
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Theory]
    [InlineData("empty-file")]
    [InlineData("missing-file")]
    [InlineData("wrong-extension")]
    [InlineData("negative-offset")]
    [InlineData("duplicate-offset")]
    public async Task InvalidExtractedSegmentsFailBeforeNativeEngineCreation(string invalidKind)
    {
        using var fixture = new Fixture();
        fixture.Extractor.ReplaceSegments = segments =>
        {
            var first = segments[0];
            switch (invalidKind)
            {
                case "empty-file": File.WriteAllBytes(first.FilePath, []); return segments;
                case "missing-file": File.Delete(first.FilePath); return segments;
                case "wrong-extension":
                    var renamed = Path.ChangeExtension(first.FilePath, ".mp3");
                    File.Move(first.FilePath, renamed);
                    return [first with { FilePath = renamed }];
                case "negative-offset": return [first with { Start = TimeSpan.FromSeconds(-1) }];
                case "duplicate-offset": return [first, first];
                default: throw new Xunit.Sdk.XunitException("Unknown fixture");
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.Equal(new[] { "verify", "extract" }, fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Theory]
    [InlineData("negative-start")]
    [InlineData("zero-duration")]
    [InlineData("descending-order")]
    [InlineData("over-24-hours")]
    [InlineData("blank-text")]
    [InlineData("oversized-text")]
    public async Task InvalidSpeechCuesCannotReachTranslation(string invalidKind)
    {
        using var fixture = new Fixture();
        fixture.Recognizer.Recognize = (_, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>(invalidKind switch
        {
            "negative-start" => [Cue(-1, 1, "Hello")],
            "zero-duration" => [Cue(1, 1, "Hello")],
            "descending-order" => [Cue(2, 3, "One"), Cue(1, 2, "Two")],
            "over-24-hours" => [Cue(0, 86_401, "Hello")],
            "blank-text" => [Cue(0, 1, "  ")],
            "oversized-text" => [Cue(0, 1, new string('a', 8_001))],
            _ => throw new Xunit.Sdk.XunitException("Unknown fixture")
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task SpeechCuesCannotCrossTheNextTenMinuteAudioBoundary()
    {
        using var fixture = new Fixture(TimeSpan.Zero, TimeSpan.FromSeconds(600));
        fixture.Recognizer.Recognize = (_, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>([Cue(599, 602, "Wrong boundary")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task SegmentSpeechCannotStartBeforeItsAbsoluteOffset()
    {
        using var fixture = new Fixture(TimeSpan.FromSeconds(600));
        fixture.Recognizer.Recognize = (_, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>([Cue(0.5, 1.5, "Relative timestamps are wrong here")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Fact]
    public async Task EmptyTranscriptDoesNotCreateTranslationModel()
    {
        using var fixture = new Fixture();
        fixture.Recognizer.Recognize = (_, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.DoesNotContain("create-translation", fixture.Events);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    [Theory]
    [InlineData("changed-start")]
    [InlineData("changed-end")]
    [InlineData("omitted-cue")]
    [InlineData("extra-cue")]
    [InlineData("blank-text")]
    public async Task TranslationCannotChangeTimestampsOrLoseOrAddCues(string invalidKind)
    {
        using var fixture = new Fixture();
        fixture.Recognizer.Recognize = (_, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>([Cue(1, 2, "One"), Cue(3, 4, "Two")]);
        fixture.Translator.Translate = (cues, _, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>(invalidKind switch
        {
            "changed-start" => [cues[0] with { Start = TimeSpan.FromSeconds(1.1) }, cues[1]],
            "changed-end" => [cues[0] with { End = TimeSpan.FromSeconds(2.1) }, cues[1]],
            "omitted-cue" => [cues[0]],
            "extra-cue" => [cues[0], cues[1], Cue(5, 6, "Extra")],
            "blank-text" => [cues[0] with { Text = "  " }, cues[1]],
            _ => throw new Xunit.Sdk.XunitException("Unknown fixture")
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GenerateAsync());
        Assert.True(fixture.Recognizer.Disposed);
        Assert.True(fixture.Translator.Disposed);
        fixture.AssertOwnedAudioRemovedAndMediaPreserved();
    }

    private static SubtitleCue Cue(double start, double end, string text) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Stevan-local-contract-test-" + Guid.NewGuid().ToString("N"));
        public string MediaPath { get; }
        public List<string> Events { get; } = [];
        public FakeExtractor Extractor { get; }
        public FakeRecognizer Recognizer { get; }
        public FakeTranslator Translator { get; }
        public Func<CancellationToken, Task> Verify { get; set; } = _ => Task.CompletedTask;
        public LocalSubtitleGenerator Generator { get; }

        public Fixture(params TimeSpan[] starts)
        {
            Directory.CreateDirectory(DirectoryPath);
            MediaPath = Path.Combine(DirectoryPath, "keep-original.mkv");
            File.WriteAllText(MediaPath, "original media must not be modified");
            Extractor = new FakeExtractor(Events, starts.Length == 0 ? [TimeSpan.Zero] : starts);
            Recognizer = new FakeRecognizer(Events);
            Translator = new FakeTranslator(Events);
            Generator = new LocalSubtitleGenerator(Extractor,
                () => { Events.Add("create-speech"); return Recognizer; },
                () =>
                {
                    Events.Add("create-translation");
                    Assert.True(Recognizer.Disposed, "The speech model must be released before the translation model is allocated");
                    return Translator;
                },
                token => { Events.Add("verify"); return Verify(token); });
        }

        public Task<IReadOnlyList<SubtitleCue>> GenerateAsync(CancellationToken token = default) =>
            Generator.GenerateAsync(MediaPath, "sr", new PlayerSettings { UseLocalAi = true }, null, token);

        public void AssertOwnedAudioRemovedAndMediaPreserved()
        {
            Assert.NotNull(Extractor.OutputDirectory);
            Assert.False(Directory.Exists(Extractor.OutputDirectory));
            Assert.Equal("original media must not be modified", File.ReadAllText(MediaPath));
        }

        public void Dispose()
        {
            if (Extractor.OutputDirectory is { } directory && Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class FakeExtractor(List<string> events, TimeSpan[] starts) : IAudioExtractor
    {
        public string? OutputDirectory { get; private set; }
        public Action<IReadOnlyList<AudioSegment>>? AfterWrite { get; set; }
        public Func<IReadOnlyList<AudioSegment>, IReadOnlyList<AudioSegment>>? ReplaceSegments { get; set; }

        public Task<IReadOnlyList<AudioSegment>> ExtractAsync(string mediaPath, string outputDirectory,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            events.Add("extract");
            OutputDirectory = outputDirectory;
            var segments = starts.Select((start, index) => new AudioSegment(Path.Combine(outputDirectory, $"audio-{index}.wav"), start)).ToArray();
            // These tiny files exercise ownership validation only, not WAV decoding or ASR.
            foreach (var segment in segments) File.WriteAllBytes(segment.FilePath, [1, 2, 3, 4]);
            AfterWrite?.Invoke(segments);
            return Task.FromResult(ReplaceSegments?.Invoke(segments) ?? segments);
        }
    }

    private sealed class FakeRecognizer(List<string> events) : ILocalSpeechRecognizer
    {
        public bool Disposed { get; private set; }
        public Func<AudioSegment, CancellationToken, Task<IReadOnlyList<SubtitleCue>>> Recognize { get; set; } =
            (segment, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>([Cue(segment.Start.TotalSeconds + 0.5, segment.Start.TotalSeconds + 1.5, "Hello")]);

        public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(AudioSegment segment, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            events.Add("speech:" + segment.Start.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Recognize(segment, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            events.Add("dispose-speech");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTranslator(List<string> events) : ILocalSubtitleTranslator
    {
        public bool Disposed { get; private set; }
        public Func<IReadOnlyList<SubtitleCue>, string, CancellationToken, Task<IReadOnlyList<SubtitleCue>>> Translate { get; set; } =
            (cues, _, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>(cues.Select(cue => cue with { Text = "Zdravo" }).ToArray());

        public Task<IReadOnlyList<SubtitleCue>> TranslateAsync(IReadOnlyList<SubtitleCue> cues, string language,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            events.Add("translate");
            return Translate(cues, language, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            events.Add("dispose-translation");
            return ValueTask.CompletedTask;
        }
    }
}
