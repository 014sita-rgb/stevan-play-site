namespace StevanPlayer.Core.Generation;

public interface ILocalSpeechRecognizer : IAsyncDisposable
{
    Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(AudioSegment segment, IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public interface ILocalSubtitleTranslator : IAsyncDisposable
{
    Task<IReadOnlyList<SubtitleCue>> TranslateAsync(IReadOnlyList<SubtitleCue> cues, string language,
        IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Runs speech recognition and translation on the user's computer, without service credentials.</summary>
public sealed class LocalSubtitleGenerator(IAudioExtractor audioExtractor,
    Func<ILocalSpeechRecognizer> createRecognizer, Func<ILocalSubtitleTranslator> createTranslator,
    Func<CancellationToken, Task> verifyModels) : ISubtitleGenerator
{
    public async Task<IReadOnlyList<SubtitleCue>> GenerateAsync(string mediaPath, string language,
        PlayerSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.UseLocalAi) throw new InvalidOperationException("Укључите локално препознавање и превод у Подешавањима.");
        if (language is not ("sr" or "hr" or "bs")) throw new ArgumentException("Подржани су српски, хрватски и босански.", nameof(language));
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Видео датотека није пронађена.", mediaPath);
        await verifyModels(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "StevanPlayer-local-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            progress?.Report("Издвајам звук за локално препознавање…");
            var segments = await audioExtractor.ExtractAsync(mediaPath, temporaryDirectory, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSegments(segments, temporaryDirectory);
            var transcript = new List<SubtitleCue>();
            await using (var recognizer = createRecognizer())
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"Локално препознавање: део {i + 1}/{segments.Count}…");
                    var cues = await recognizer.TranscribeAsync(segments[i], progress, cancellationToken).ConfigureAwait(false);
                    ValidateCues(cues, segments[i].Start);
                    if (i + 1 < segments.Count && cues.Any(cue => cue.End > segments[i + 1].Start + TimeSpan.FromSeconds(1)))
                        throw new InvalidDataException("Препознати текст прелази границу звучног сегмента.");
                    transcript.AddRange(cues);
                    if (transcript.Count > 40_000) throw new InvalidDataException("Препознати титл је превелик.");
                }
            }
            if (transcript.Count == 0) throw new InvalidDataException("У звуку није препознат говор за титлове.");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Преводим титл локално на рачунару…");
            await using var translator = createTranslator();
            var translated = await translator.TranslateAsync(transcript, language, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCues(translated, TimeSpan.Zero);
            if (translated.Count != transcript.Count || translated.Where((cue, i) =>
                    cue.Start != transcript[i].Start || cue.End != transcript[i].End).Any())
                throw new InvalidDataException("Локални превод није сачувао све временске кодове титла.");
            progress?.Report($"Локално је припремљено {translated.Count} титлова.");
            return translated;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateSegments(IReadOnlyList<AudioSegment> segments, string directory)
    {
        if (segments.Count is 0 or > 512) throw new InvalidDataException("Нема подржаних звучних сегмената.");
        string prefix = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        TimeSpan? previous = null;
        foreach (var segment in segments)
        {
            var file = new FileInfo(segment.FilePath);
            if (!file.FullName.StartsWith(prefix, comparison) || !file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                !file.Exists || file.Length is <= 0 or > 24 * 1024 * 1024 || file.LinkTarget is not null ||
                segment.Start < TimeSpan.Zero || previous.HasValue && segment.Start <= previous.Value)
                throw new InvalidDataException("Неисправан локални звучни сегмент.");
            previous = segment.Start;
        }
    }

    private static void ValidateCues(IReadOnlyList<SubtitleCue> cues, TimeSpan minimum)
    {
        TimeSpan previous = minimum;
        foreach (var cue in cues)
        {
            if (cue.Start < previous || cue.End <= cue.Start || cue.End > TimeSpan.FromHours(24) ||
                string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 8_000)
                throw new InvalidDataException("Локални модел је вратио неисправан текст или време титла.");
            previous = cue.Start;
        }
    }
}
