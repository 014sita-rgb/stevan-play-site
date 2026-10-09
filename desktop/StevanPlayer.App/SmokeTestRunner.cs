using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibVLCSharp.Shared;

namespace StevanPlayer.App;

/// <summary>
/// An actual native Windows decode check, not a release certification or a replacement for UI testing.
/// The report deliberately separates the assertions it executed from checks that need an interactive user.
/// </summary>
public static class SmokeTestRunner
{
    public static async Task<int> RunAsync(string mediaPath, string subtitlePath, string reportPath)
    {
        var report = new NativeDecodeReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            MediaFile = Path.GetFullPath(mediaPath),
            SubtitleFile = Path.GetFullPath(subtitlePath)
        };
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This native Windows test cannot run on a non-Windows machine.");
            if (!File.Exists(report.MediaFile)) throw new FileNotFoundException("The test media file does not exist.");
            if (!File.Exists(report.SubtitleFile)) throw new FileNotFoundException("The test subtitle file does not exist.");
            if (new FileInfo(report.SubtitleFile).Length == 0) throw new InvalidDataException("The test subtitle is empty.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await ExecuteNativeChecksAsync(report, timeout.Token);
            using var extractionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await ExecuteAudioExtractionChecksAsync(report, extractionTimeout.Token);
            report.Status = report.Checks.Count > 0 && report.Checks.Values.All(passed => passed) ? "PASS" : "FAIL";
        }
        catch (Exception exception)
        {
            report.Status = "FAIL";
            report.Error = exception.GetType().Name + ": " + exception.Message;
        }
        report.FinishedUtc = DateTimeOffset.UtcNow;
        if (!report.AudioSegmentationBoundaryExercised)
            report.NotTested.Add("audio segmentation boundary (use a test movie longer than 600 seconds)");
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Status == "PASS" ? 0 : 1;
    }

    private static async Task ExecuteNativeChecksAsync(NativeDecodeReport report, CancellationToken cancellationToken)
    {
        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--no-video-title-show", "--no-sub-autodetect-file", "--avcodec-hw=none");
        using var media = new Media(vlc, new Uri(report.MediaFile));
        var parsed = await media.Parse(MediaParseOptions.ParseLocal, 15_000, cancellationToken);
        report.Checks["media_parsed"] = parsed == MediaParsedStatus.Done;
        report.DurationMilliseconds = media.Duration;
        report.Checks["video_and_audio_tracks_present"] = media.Tracks.Any(track => track.TrackType == TrackType.Video)
            && media.Tracks.Any(track => track.TrackType == TrackType.Audio);
        report.Checks["test_media_at_least_15_seconds"] = media.Duration >= 15_000;
        if (report.Checks.Values.Any(passed => !passed))
            throw new InvalidDataException("Use a local test movie containing video and audio with duration at least 15 seconds.");

        using var player = new MediaPlayer(vlc);
        // Decode into aligned native memory to count real frames, without creating a hidden GUI window.
        // These callbacks intentionally do not test the application's WPF video host or a speaker device.
        const int width = 320;
        const int height = 180;
        var allocation = Marshal.AllocHGlobal(width * height * 4 + 31);
        var picture = new IntPtr((allocation.ToInt64() + 31) & ~31L);
        long decodedFrames = 0;
        long displayedFrames = 0;
        long audioSamples = 0;
        var nativeError = 0;
        var pictureLock = new object();
        MediaPlayer.LibVLCVideoLockCb lockCallback = (_, planes) =>
        {
            Monitor.Enter(pictureLock);
            Marshal.WriteIntPtr(planes, picture);
            return picture;
        };
        MediaPlayer.LibVLCVideoUnlockCb unlockCallback = (_, _, _) =>
        {
            Interlocked.Increment(ref decodedFrames);
            Monitor.Exit(pictureLock);
        };
        MediaPlayer.LibVLCVideoDisplayCb displayCallback = (_, _) => Interlocked.Increment(ref displayedFrames);
        MediaPlayer.LibVLCAudioPlayCb audioCallback = (_, _, count, _) => Interlocked.Add(ref audioSamples, count);
        try
        {
            player.SetVideoFormat("RV32", width, height, width * 4);
            player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
            player.SetAudioFormat("S16N", 16000, 1);
            player.SetAudioCallbacks(audioCallback, null, null, null, null);
            player.EncounteredError += (_, _) => Interlocked.Exchange(ref nativeError, 1);
            report.Checks["play_requested"] = player.Play(media);
            await WaitForAsync(() => player.State == VLCState.Playing && player.Time >= 1_000
                && Interlocked.Read(ref decodedFrames) >= 2 && Interlocked.Read(ref audioSamples) > 16_000,
                TimeSpan.FromSeconds(15), cancellationToken);
            report.Checks["playing_observed"] = player.State == VLCState.Playing;
            var initialStats = media.Statistics;
            report.Checks["native_video_decode_counter_positive"] = initialStats.DecodedVideo > 0;
            report.Checks["native_audio_decode_counter_positive"] = initialStats.DecodedAudio > 0;
            report.Checks["decoded_frame_callbacks_received"] = Interlocked.Read(ref decodedFrames) >= 2;
            report.Checks["audio_sample_callbacks_received"] = Interlocked.Read(ref audioSamples) > 16_000;

            var existingSubtitleIds = player.SpuDescription.Select(track => track.Id).ToHashSet();
            report.Checks["external_subtitle_add_accepted"] = player.AddSlave(MediaSlaveType.Subtitle,
                new Uri(report.SubtitleFile).AbsoluteUri, true);
            await WaitForAsync(() => player.Spu >= 0 && !existingSubtitleIds.Contains(player.Spu)
                && player.SpuDescription.Any(track => track.Id == player.Spu), TimeSpan.FromSeconds(8), cancellationToken);
            report.ActiveSubtitleTrackId = player.Spu;
            report.SubtitleTracks = player.SpuDescription.Where(track => track.Id >= 0)
                .Select(track => $"{track.Id}: {track.Name}").ToArray();
            report.Checks["new_external_subtitle_track_selected"] = player.Spu >= 0
                && !existingSubtitleIds.Contains(player.Spu)
                && player.SpuDescription.Any(track => track.Id == player.Spu);
            report.Checks["subtitle_delay_set"] = player.SetSpuDelay(250_000);
            report.Checks["subtitle_delay_readback_matches"] = player.SpuDelay == 250_000;
            player.SetSpuDelay(0);

            report.Checks["pause_supported"] = player.CanPause;
            player.SetPause(true);
            await WaitForAsync(() => player.State == VLCState.Paused, TimeSpan.FromSeconds(3), cancellationToken);
            report.Checks["paused_state_observed"] = player.State == VLCState.Paused;
            var pauseTime = player.Time;
            await Task.Delay(600, cancellationToken);
            report.Checks["paused_timeline_stable"] = Math.Abs(player.Time - pauseTime) <= 100;

            report.Checks["seek_supported"] = player.IsSeekable;
            var beforeSeekTime = player.Time;
            report.SeekTargetMilliseconds = beforeSeekTime < media.Duration / 2
                ? media.Duration * 3 / 4 : media.Duration / 4;
            player.Time = report.SeekTargetMilliseconds;
            player.SetPause(false);
            var framesBeforeResume = Interlocked.Read(ref decodedFrames);
            var samplesBeforeResume = Interlocked.Read(ref audioSamples);
            await WaitForAsync(() => player.State == VLCState.Playing
                && Math.Abs(player.Time - report.SeekTargetMilliseconds) < 1_500
                && Math.Abs(player.Time - beforeSeekTime) > 1_000,
                TimeSpan.FromSeconds(6), cancellationToken);
            report.SeekObservedMilliseconds = player.Time;
            report.Checks["seek_target_observed"] = Math.Abs(report.SeekObservedMilliseconds - report.SeekTargetMilliseconds) < 1_500
                && Math.Abs(report.SeekObservedMilliseconds - beforeSeekTime) > 1_000;
            await WaitForAsync(() => Interlocked.Read(ref decodedFrames) >= framesBeforeResume + 2
                && Interlocked.Read(ref audioSamples) > samplesBeforeResume + 8_000,
                TimeSpan.FromSeconds(5), cancellationToken);
            report.Checks["video_and_audio_decoding_resumed_after_seek"] =
                Interlocked.Read(ref decodedFrames) >= framesBeforeResume + 2
                && Interlocked.Read(ref audioSamples) > samplesBeforeResume + 8_000;
            report.Checks["subtitle_remains_selected_after_seek"] = player.Spu == report.ActiveSubtitleTrackId;
            report.Checks["no_native_playback_error"] = Volatile.Read(ref nativeError) == 0;
            var stats = media.Statistics;
            report.NativeDecodedVideo = stats.DecodedVideo;
            report.NativeDecodedAudio = stats.DecodedAudio;
            report.FrameCallbacks = Interlocked.Read(ref decodedFrames);
            report.DisplayCallbacks = Interlocked.Read(ref displayedFrames);
            report.AudioSampleCallbacks = Interlocked.Read(ref audioSamples);
        }
        finally
        {
            await Task.Run(player.Stop, CancellationToken.None);
            // Native Stop joins callback workers before their memory and delegates can be released.
            Marshal.FreeHGlobal(allocation);
            GC.KeepAlive(lockCallback);
            GC.KeepAlive(unlockCallback);
            GC.KeepAlive(displayCallback);
            GC.KeepAlive(audioCallback);
        }
    }

    private static async Task ExecuteAudioExtractionChecksAsync(NativeDecodeReport report,
        CancellationToken cancellationToken)
    {
        const double toleranceMilliseconds = 750;
        const double segmentMilliseconds = 600_000;
        // CreateTempSubdirectory returns a new, private directory; only these generated WAVs are removed.
        var directory = Directory.CreateTempSubdirectory("StevanPlayer-native-audio-");
        var elapsed = Stopwatch.StartNew();
        try
        {
            var segments = await new LibVlcAudioExtractor().ExtractAsync(report.MediaFile,
                directory.FullName, null, cancellationToken);
            report.Checks["audio_extraction_returned_segments"] = segments.Count > 0;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.AudioSegments.Add(ReadWaveSamples(segment.FilePath, segment.Start.TotalMilliseconds,
                    cancellationToken));
            }
            report.AudioSegmentationBoundaryExercised = report.AudioSegments.Count > 1;
            report.AudioDurationToleranceMilliseconds = toleranceMilliseconds;
            report.TotalExtractedAudioDurationMilliseconds = report.AudioSegments.Sum(segment => segment.DurationMilliseconds);
            report.AudioDurationDeltaMilliseconds = report.TotalExtractedAudioDurationMilliseconds - report.DurationMilliseconds;
            report.Checks["audio_wav_pcm_16khz_mono_16bit"] = report.AudioSegments.Count > 0
                && report.AudioSegments.All(segment => segment.FormatCode == 1 && segment.Channels == 1
                    && segment.SampleRate == 16_000 && segment.BitsPerSample == 16);
            report.Checks["audio_wav_nonempty_below_20mb"] = report.AudioSegments.Count > 0
                && report.AudioSegments.All(segment => segment.FileBytes is > 44 and <= 20_000_000
                    && segment.DataBytes > 0);
            report.Checks["audio_pcm_sample_payload_read"] = report.AudioSegments.Count > 0
                && report.AudioSegments.All(segment => segment.SamplesRead > 0
                    && segment.SamplesRead * 2 == segment.DataBytes);
            report.Checks["audio_extracted_waveform_contains_nonzero_samples"] =
                report.AudioSegments.Sum(segment => segment.NonzeroSamples) > 0;
            report.Checks["audio_extracted_duration_matches_media"] =
                Math.Abs(report.AudioDurationDeltaMilliseconds) <= toleranceMilliseconds;
            report.Checks["audio_segment_count_matches_600sec_boundaries"] = report.AudioSegments.Count
                == (int)Math.Ceiling(report.DurationMilliseconds / segmentMilliseconds);
            report.Checks["audio_segment_offsets_match_600sec_boundaries"] = report.AudioSegments.Count > 0
                && report.AudioSegments.Select((segment, index) =>
                    Math.Abs(segment.StartMilliseconds - index * segmentMilliseconds) <= 1).All(matches => matches);
            report.Checks["audio_segment_intervals_match_media"] = report.AudioSegments.Count > 0
                && report.AudioSegments.All(segment => Math.Abs(segment.DurationMilliseconds
                    - Math.Min(segmentMilliseconds, report.DurationMilliseconds - segment.StartMilliseconds))
                    <= toleranceMilliseconds);
        }
        finally
        {
            report.AudioExtractionElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            // The directory was created uniquely by this test and contains no user input files.
            try
            {
                directory.Delete(recursive: true);
                report.Checks["audio_test_temporary_files_removed"] = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report.Checks["audio_test_temporary_files_removed"] = false;
                report.AudioCleanupError = exception.GetType().Name + ": " + exception.Message;
            }
        }
    }

    private static WaveSegmentReport ReadWaveSamples(string path, double startMilliseconds,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var result = new WaveSegmentReport
        {
            FileName = Path.GetFileName(path),
            FileBytes = stream.Length,
            StartMilliseconds = startMilliseconds
        };
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Extracted audio has no RIFF header.");
        var riffBytes = reader.ReadUInt32();
        if (riffBytes + 8L != stream.Length) throw new InvalidDataException("The WAV RIFF length does not match the file length.");
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Extracted audio has no WAVE header.");
        long dataOffset = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            if (length > stream.Length - stream.Position) throw new InvalidDataException("An extracted WAV chunk is truncated.");
            var next = stream.Position + length + (length & 1);
            if (chunk == "fmt " && length >= 16)
            {
                result.FormatCode = reader.ReadUInt16();
                result.Channels = reader.ReadUInt16();
                result.SampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                result.BlockAlign = reader.ReadUInt16();
                result.BitsPerSample = reader.ReadUInt16();
            }
            else if (chunk == "data")
            {
                dataOffset = stream.Position;
                result.DataBytes = length;
            }
            stream.Position = Math.Min(next, stream.Length);
        }
        if (dataOffset < 0 || result.FormatCode != 1 || result.Channels != 1
            || result.SampleRate != 16_000 || result.BitsPerSample != 16 || result.BlockAlign != 2
            || result.DataBytes == 0 || result.DataBytes % 2 != 0)
            throw new InvalidDataException("The extracted audio is not nonempty mono PCM 16kHz / 16bit WAV.");
        stream.Position = dataOffset;
        var buffer = new byte[65_536];
        long remaining = result.DataBytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            stream.ReadExactly(buffer.AsSpan(0, requested));
            for (var offset = 0; offset < requested; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2));
                result.SamplesRead++;
                if (sample != 0) result.NonzeroSamples++;
                result.PeakAbsoluteSample = Math.Max(result.PeakAbsoluteSample, Math.Abs((int)sample));
            }
            remaining -= requested;
        }
        result.DurationMilliseconds = result.SamplesRead * 1000d / result.SampleRate;
        return result;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= timeout) throw new TimeoutException("A native playback assertion did not become true within " + timeout + ".");
            await Task.Delay(100, cancellationToken);
        }
    }

    private sealed class NativeDecodeReport
    {
        public string Scope { get; } = "native_windows_decode_and_audio_extraction";
        public string Status { get; set; } = "FAIL";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        public string OperatingSystem { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
        public string MediaFile { get; set; } = "";
        public string SubtitleFile { get; set; } = "";
        public long DurationMilliseconds { get; set; }
        public int NativeDecodedVideo { get; set; }
        public int NativeDecodedAudio { get; set; }
        public long FrameCallbacks { get; set; }
        public long DisplayCallbacks { get; set; }
        public long AudioSampleCallbacks { get; set; }
        public int ActiveSubtitleTrackId { get; set; } = -1;
        public string[] SubtitleTracks { get; set; } = [];
        public long SeekTargetMilliseconds { get; set; }
        public long SeekObservedMilliseconds { get; set; }
        public List<WaveSegmentReport> AudioSegments { get; } = [];
        public bool AudioSegmentationBoundaryExercised { get; set; }
        public double AudioExtractionElapsedMilliseconds { get; set; }
        public double TotalExtractedAudioDurationMilliseconds { get; set; }
        public double AudioDurationDeltaMilliseconds { get; set; }
        public double AudioDurationToleranceMilliseconds { get; set; }
        public string? AudioCleanupError { get; set; }
        public Dictionary<string, bool> Checks { get; } = [];
        public string? Error { get; set; }
        public List<string> NotTested { get; } = ["WPF GUI", "fullscreen", "speaker output", "installer",
            "automatic subtitle provider search/download", "subtitle translation accuracy",
            "subtitle text actually rendered", "subtitle visual/lip-sync accuracy"];
    }

    private sealed class WaveSegmentReport
    {
        public string FileName { get; set; } = "";
        public long FileBytes { get; set; }
        public uint DataBytes { get; set; }
        public ushort FormatCode { get; set; }
        public ushort Channels { get; set; }
        public uint SampleRate { get; set; }
        public ushort BlockAlign { get; set; }
        public ushort BitsPerSample { get; set; }
        public double StartMilliseconds { get; set; }
        public double DurationMilliseconds { get; set; }
        public long SamplesRead { get; set; }
        public long NonzeroSamples { get; set; }
        public int PeakAbsoluteSample { get; set; }
    }
}
