using System.Globalization;
using LibVLCSharp.Shared;
using StevanPlayer.Core;

namespace StevanPlayer.App;

/// <summary>Extracts audio locally. This class never sends a media file or audio over the network.</summary>
public sealed class LibVlcAudioExtractor : IAudioExtractor
{
    private const int SegmentSeconds = 600;
    private const long MaximumFileBytes = 20_000_000;

    public async Task<IReadOnlyList<AudioSegment>> ExtractAsync(string mediaPath, string outputDirectory,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Издвајање звука захтева Windows и испоручени LibVLC.");
        mediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Медијска датотека није пронађена.", mediaPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--no-video", "--no-video-title-show", "--no-sout-all",
            "--no-sout-video", "--no-sout-spu");
        using var probe = new Media(vlc, new Uri(mediaPath));
        var parseStatus = await probe.Parse(MediaParseOptions.ParseLocal, 15_000, cancellationToken);
        if (parseStatus != MediaParsedStatus.Done || probe.Duration <= 0)
            throw new InvalidOperationException("Није могуће утврдити трајање медија; издвајање звука је заустављено.");
        if (!probe.Tracks.Any(track => track.TrackType == TrackType.Audio))
            throw new InvalidOperationException("Медијска датотека нема звучни запис.");

        var totalSeconds = probe.Duration / 1000d;
        var result = new List<AudioSegment>();
        var createdFiles = new List<string>();
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            for (var startSeconds = 0d; startSeconds < totalSeconds; startSeconds += SegmentSeconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopSeconds = Math.Min(startSeconds + SegmentSeconds, totalSeconds);
                var segmentPath = Path.Combine(outputDirectory, $"audio-{operationId}-{result.Count:D4}.wav");
                createdFiles.Add(segmentPath);
                progress?.Report($"Издвајање звука: {TimeSpan.FromSeconds(startSeconds):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(totalSeconds):hh\\:mm\\:ss}");
                await ExtractSegmentAsync(vlc, mediaPath, segmentPath, startSeconds, stopSeconds, cancellationToken);
                ValidateWaveFile(segmentPath);
                result.Add(new AudioSegment(segmentPath, TimeSpan.FromSeconds(startSeconds)));
            }
            return result;
        }
        catch
        {
            // Remove only the files this invocation created, never an existing directory or user's media.
            foreach (var file in createdFiles)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static async Task ExtractSegmentAsync(LibVLC vlc, string mediaPath, string destination,
        double startSeconds, double stopSeconds, CancellationToken cancellationToken)
    {
        using var media = new Media(vlc, new Uri(mediaPath));
        // The file sink does not wait for an audio device's clock and can transcode faster than playback.
        // Every new media/player has its own start/stop interval; interactive playback is unaffected.
        var vlcPath = destination.Replace('\\', '/').Replace("'", "\\'");
        media.AddOption(":sout=#transcode{acodec=s16l,channels=1,samplerate=16000}:std{access=file,mux=wav,dst='" + vlcPath + "'}");
        media.AddOption(":start-time=" + startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        media.AddOption(":stop-time=" + stopSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        using var player = new MediaPlayer(vlc);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => finished.TrySetResult(true);
        player.EncounteredError += (_, _) => finished.TrySetException(
            new InvalidOperationException("LibVLC није успео да издвоји звук из медијске датотеке."));
        try
        {
            if (!player.Play(media)) throw new InvalidOperationException("LibVLC није покренуо издвајање звука.");
            // A bounded timeout also handles a demuxer that never raises EndReached.
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(stopSeconds - startSeconds + 120), cancellationToken);
        }
        finally
        {
            // Stop joins the native workers and finalizes the RIFF header before validation/cleanup.
            // Never invoke Stop in a LibVLC callback: it can deadlock the callback's own worker.
            await Task.Run(player.Stop, CancellationToken.None);
        }
    }

    private static void ValidateWaveFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 44 or > MaximumFileBytes)
            throw new InvalidDataException("Издвојени WAV је празан или прелази ограничење од 20 MB.");
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Недостаје WAV RIFF заглавље.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Недостаје WAV WAVE заглавље.");
        var validFormat = false;
        var validData = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var name = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            if (length > stream.Length - stream.Position) throw new InvalidDataException("Оштећен WAV блок.");
            var nextPosition = stream.Position + length + (length & 1);
            if (name == "fmt " && length >= 16)
            {
                var format = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                var blockAlign = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                validFormat = format == 1 && channels == 1 && sampleRate == 16000 && bits == 16 && blockAlign == 2;
            }
            else if (name == "data") validData = length > 0 && length % 2 == 0;
            stream.Position = Math.Min(nextPosition, stream.Length);
        }
        if (!validFormat || !validData)
            throw new InvalidDataException("Издвојени звук није исправан моно PCM WAV, 16 kHz / 16 bit.");
    }
}
