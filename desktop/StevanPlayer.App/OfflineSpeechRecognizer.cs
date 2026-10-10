using StevanPlayer.Core;
using StevanPlayer.Core.Generation;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace StevanPlayer.App;

/// <summary>Runs the bundled CPU Whisper runtime; audio never leaves this process.</summary>
public sealed class OfflineSpeechRecognizer(string modelPath) : ILocalSpeechRecognizer
{
    private static readonly Lazy<bool> CpuConfiguration = new(() =>
    {
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        return true;
    });
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private WhisperFactory? factory;
    private bool disposed;

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(AudioSegment segment,
        IProgress<string>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(segment);
        token.ThrowIfCancellationRequested();
        await operationLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Loading the model and starting native inference are synchronous native calls.
            // Keep them on a worker thread even when the caller is the WPF dispatcher.
            return await Task.Run(() => TranscribeCoreAsync(segment, progress, token), token).ConfigureAwait(false);
        }
        finally { operationLock.Release(); }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeCoreAsync(AudioSegment segment,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Nedostaje lokalni model za prepoznavanje govora.", modelPath);
        if (segment.Start < TimeSpan.Zero || !File.Exists(segment.FilePath))
            throw new InvalidOperationException("Zvučni segment za prepoznavanje govora nije ispravan.");
        token.ThrowIfCancellationRequested();
        _ = CpuConfiguration.Value;
        try
        {
            if (factory is null)
            {
                progress?.Report("Učitavanje lokalnog modela za prepoznavanje govora…");
                factory = WhisperFactory.FromPath(modelPath);
            }
            token.ThrowIfCancellationRequested();
            await using var processor = factory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                .WithLanguage("auto")
                // Whisper supports translation into English. The second local model
                // translates this timed intermediate transcript into sr/hr/bs.
                .WithTranslate()
                .Build();
            await using var audio = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var cues = new List<SubtitleCue>();
            var lastStart = TimeSpan.Zero;
            await foreach (var result in processor.ProcessAsync(audio, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                var text = result.Text.Trim();
                if (text.Length == 0) continue;
                if (result.Start < TimeSpan.Zero || result.End <= result.Start || result.Start < lastStart
                    || result.End > TimeSpan.FromMinutes(11) || text.Length > 1_200 || cues.Count >= 1_024)
                    throw new InvalidOperationException("Lokalno prepoznavanje vratilo je neispravne vremenske kodove ili predugačak titl.");
                lastStart = result.Start;
                cues.Add(new SubtitleCue(segment.Start + result.Start, segment.Start + result.End, text));
            }
            return cues;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException("Lokalni govorni mehanizam nije mogao da se učita. Na Windows-u je potreban Microsoft Visual C++ 2019–2022 Redistributable odgovarajuće arhitekture.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            var currentFactory = factory;
            factory = null;
            if (currentFactory is not null)
                await Task.Run(currentFactory.Dispose).ConfigureAwait(false);
        }
        finally { operationLock.Release(); }
    }
}
