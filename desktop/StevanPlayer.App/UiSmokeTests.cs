using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;

namespace StevanPlayer.App;

public partial class MainWindow
{
    /// <summary>Exercises the real window and event handlers on a Windows interactive desktop.</summary>
    public async Task<int> RunUiSmokeTestAsync(string mediaPath, string subtitlePath, string reportPath, string ownedDirectory)
    {
        var report = new UiTestReport();
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WPF тест захтева Windows радну површину.");
            if (!File.Exists(mediaPath) || !File.Exists(subtitlePath)) throw new FileNotFoundException("Медијски тест захтева видео и SRT титл.");
            _settings.AutoSubtitles = false; // This UI fixture does not depend on an external service.
            _settings.UseLocalAi = _settings.AutoSynchronize = false;
            _settings.PreferredLanguage = "sr";
            _settings.SubtitleOffsetMilliseconds = 0;
            // Preferences live in the isolated directory; no credentials are read or written.
            _settingsStore.Save(new StevanPlayer.Core.PlayerSettings { PreferredLanguage = "hr", UseLocalAi = false });
            string storedText = await File.ReadAllTextAsync(Path.Combine(SettingsStore.DataDirectory, "settings.json"));
            report.Checks["settings_do_not_store_api_credentials"] = !storedText.Contains("ApiKey", StringComparison.Ordinal) &&
                !storedText.Contains("ProtectedCredentials", StringComparison.Ordinal);
            report.Checks["local_preferences_roundtrip"] = _settingsStore.Load() is { PreferredLanguage: "hr", UseLocalAi: false };
            string fixture = Path.Combine(ownedDirectory, "Smoke.Movie.2026" + Path.GetExtension(mediaPath));
            string caption = Path.Combine(ownedDirectory, "Smoke.Movie.2026.sr.srt");
            string nextFixture = Path.Combine(ownedDirectory, "Next.Movie.2026" + Path.GetExtension(mediaPath));
            File.Copy(mediaPath, fixture);
            File.Copy(subtitlePath, caption);
            File.Copy(mediaPath, nextFixture);
            await File.WriteAllTextAsync(Path.ChangeExtension(nextFixture, ".sr.srt"), "<html>Invalid local subtitle</html>");
            WindowState = WindowState.Normal;
            Activate();
            OpenInitialFiles([fixture, nextFixture]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            await WaitForUiAsync(() => _player.State == VLCState.Playing && _player.Time >= 1_000 &&
                _media!.Statistics.DecodedVideo > 10 && _media.Statistics.DecodedAudio > 0, TimeSpan.FromSeconds(15), timeout.Token);
            report.Checks["wpf_window_visible"] = IsVisible && IsLoaded;
            report.Checks["native_video_hwnd_attached"] = Video.MediaPlayer == _player && _player.Hwnd != IntPtr.Zero && Video.ActualWidth > 0 && Video.ActualHeight > 0;
            report.Checks["video_and_audio_decoding_in_wpf_host"] = _media!.Statistics.DecodedVideo > 10 && _media.Statistics.DecodedAudio > 0;
            report.Checks["duration_at_least_15_seconds"] = _player.Length >= 15_000;
            await WaitForUiAsync(() => _player.Spu >= 0 && _currentSubtitleTrackId == _player.Spu &&
                _player.SpuDescription.Any(track => track.Id == _player.Spu), TimeSpan.FromSeconds(8), timeout.Token);
            report.Checks["adjacent_local_subtitle_automatically_supplied"] = _currentSubtitlePath == caption;
            report.Checks["actual_subtitle_track_selected"] = _player.Spu >= 0 && _player.SpuDescription.Any(track => track.Id == _player.Spu);
            await SearchSubtitlesAsync(automatic: false);
            var available = ResultsList.Items.OfType<StevanPlayer.Core.SubtitleCandidate>().ToArray();
            report.Checks["available_translations_are_listed_inside_player"] = available.Length == 1 && available[0].Language == "sr";
            if (available.Length != 1) throw new InvalidOperationException("Тест филм нема очекивани превод на списку.");
            ResultsList.SelectedItem = available[0];
            await WaitForUiAsync(() => _operation is null && _currentSubtitlePath is not null && _currentSubtitlePath != caption &&
                _player.Spu >= 0 && _player.Spu == _currentSubtitleTrackId, TimeSpan.FromSeconds(10), timeout.Token);
            report.Checks["single_selection_loads_chosen_translation_without_browser"] = File.Exists(_currentSubtitlePath) &&
                _player.Spu == _currentSubtitleTrackId && SubtitleLabel.Text.StartsWith("Укључен:", StringComparison.Ordinal);
            ResultsList.SelectedItem = null;
            await WaitForUiAsync(() => _player.Spu == -1 && _operation is null, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["unchecking_translation_disables_native_subtitle"] = _currentSubtitlePath is null &&
                _currentSubtitleTrackId == -1 && !SyncButton.IsEnabled;
            ResultsList.SelectedItem = available[0];
            ResultsList.SelectedItem = null;
            await Task.Delay(700, timeout.Token);
            report.Checks["unchecking_pending_choice_prevents_later_attachment"] = _player.Spu == -1 &&
                _currentSubtitlePath is null && _operation is null;
            ResultsList.SelectedItem = available[0];
            await WaitForUiAsync(() => _operation is null && _currentSubtitlePath is not null &&
                _player.Spu >= 0 && _player.Spu == _currentSubtitleTrackId, TimeSpan.FromSeconds(10), timeout.Token);
            report.Checks["translation_can_be_selected_again_after_cancel"] = File.Exists(_currentSubtitlePath);
            report.ActiveSubtitleTrack = _player.Spu;
            AudioTrackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var audioMenu = AudioTrackButton.ContextMenu ?? throw new InvalidOperationException("Звучни записи немају мени.");
            int audioTrack = _player.AudioTrackDescription.First(track => track.Id >= 0).Id;
            report.Checks["audio_menu_contains_actual_native_track"] = audioMenu.IsOpen &&
                audioMenu.Items.OfType<MenuItem>().Any(item => item.Tag is int id && id == audioTrack);
            var disableAudio = audioMenu.Items.OfType<MenuItem>().First(item => item.Tag is int id && id == -1);
            disableAudio.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForUiAsync(() => _player.AudioTrack == -1, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["audio_menu_disables_native_audio_track"] = _player.AudioTrack == -1;
            AudioTrackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            audioMenu = AudioTrackButton.ContextMenu ?? throw new InvalidOperationException("Мени звука није поново отворен.");
            var audioItem = audioMenu.Items.OfType<MenuItem>().First(item => item.Tag is int id && id == audioTrack);
            audioItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForUiAsync(() => _player.AudioTrack == audioTrack, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["audio_menu_restores_native_audio_track"] = _player.AudioTrack == audioTrack;

            SubtitleTrackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var subtitleMenu = SubtitleTrackButton.ContextMenu ?? throw new InvalidOperationException("Титл записи немају мени.");
            var disableSubtitle = subtitleMenu.Items.OfType<MenuItem>().First(item => item.Tag is int id && id == -1);
            disableSubtitle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForUiAsync(() => _player.Spu == -1 && !SyncButton.IsEnabled, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["subtitle_menu_disables_native_subtitle_track"] = _player.Spu == -1 && !SyncButton.IsEnabled;
            SubtitleTrackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            subtitleMenu = SubtitleTrackButton.ContextMenu ?? throw new InvalidOperationException("Мени титлова није поново отворен.");
            var subtitleItem = subtitleMenu.Items.OfType<MenuItem>().First(item => item.Tag is int id && id == report.ActiveSubtitleTrack);
            subtitleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitForUiAsync(() => _player.Spu == report.ActiveSubtitleTrack && SyncButton.IsEnabled, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["subtitle_menu_restores_native_subtitle_track"] = _player.Spu == report.ActiveSubtitleTrack && SyncButton.IsEnabled;
            await Task.Delay(300, timeout.Token);
            string windowScreenshot = reportPath + ".window.png";
            NativeWindowTools.CaptureWindow(this, windowScreenshot);
            report.Screenshots.Add(windowScreenshot);
            report.Checks["window_screen_pixels_captured"] = new FileInfo(windowScreenshot).Length > 0;

            PlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUiAsync(() => _player.State == VLCState.Paused, TimeSpan.FromSeconds(3), timeout.Token);
            long pauseTime = _player.Time;
            await Task.Delay(500, timeout.Token);
            report.Checks["play_button_pauses_native_playback"] = _player.State == VLCState.Paused && Math.Abs(_player.Time - pauseTime) < 100;
            RaiseTestKey(Key.Space);
            await WaitForUiAsync(() => _player.State == VLCState.Playing, TimeSpan.FromSeconds(3), timeout.Token);
            report.Checks["space_shortcut_resumes_native_playback"] = _player.IsPlaying;
            report.Checks["video_overlay_accepts_keyboard_focus"] = VideoOverlay.IsKeyboardFocusWithin;
            VolumeSlider.Value = 37;
            report.Checks["volume_slider_changes_native_volume"] = _player.Volume == 37;
            MuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            report.Checks["mute_button_changes_native_mute"] = _player.Mute;
            RaiseTestKey(Key.M);
            report.Checks["mute_shortcut_restores_native_sound"] = !_player.Mute;
            PlayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForUiAsync(() => _player.State == VLCState.Paused, TimeSpan.FromSeconds(3), timeout.Token);
            SeekSlider.Value = 400;
            RaiseTestKey(Key.Space);
            long seekTarget = _player.Length * 2 / 5;
            await WaitForUiAsync(() => _player.IsPlaying && Math.Abs(_player.Time - seekTarget) < 1500,
                TimeSpan.FromSeconds(5), timeout.Token);
            report.Checks["seek_slider_changes_native_timeline"] = Math.Abs(_player.Time - seekTarget) < 1500;
            RaiseTestKey(Key.H);
            report.Checks["subtitle_offset_shortcut_changes_native_delay"] = _player.SpuDelay == 100_000;
            RaiseTestKey(Key.G);
            report.Checks["subtitle_offset_restores_native_delay"] = _player.SpuDelay == 0;

            RaiseTestKey(Key.F11);
            await Task.Delay(400, timeout.Token);
            report.Checks["fullscreen_shortcut_changes_window_state"] = _fullscreen && WindowStyle == WindowStyle.None && Topmost;
            report.Checks["fullscreen_covers_whole_monitor"] = NativeWindowTools.CoversMonitor(this);
            report.Checks["fullscreen_hides_header_and_sidebar"] = Header.Visibility == Visibility.Collapsed && Sidebar.Visibility == Visibility.Collapsed;
            string fullscreenScreenshot = reportPath + ".fullscreen.png";
            NativeWindowTools.CaptureWindow(this, fullscreenScreenshot);
            report.Screenshots.Add(fullscreenScreenshot);
            report.Checks["fullscreen_screen_pixels_captured"] = new FileInfo(fullscreenScreenshot).Length > 0;
            RaiseTestKey(Key.Escape);
            await Task.Delay(200, timeout.Token);
            report.Checks["escape_exits_fullscreen"] = !_fullscreen && WindowStyle != WindowStyle.None && Header.Visibility == Visibility.Visible && Sidebar.Visibility == Visibility.Visible;

            var pendingOperation = BeginOperation();
            bool moved = NextMedia();
            report.Checks["playlist_advances_to_next_video"] = moved && _currentPath == nextFixture;
            report.Checks["media_switch_cancels_pending_operation"] = pendingOperation.Cancellation.IsCancellationRequested && !IsCurrent(pendingOperation);
            FinishOperation(pendingOperation);
            report.Checks["media_switch_clears_old_subtitle_and_results"] = _currentSubtitlePath is null && ResultsList.ItemsSource is null;
            await WaitForUiAsync(() => _player.IsPlaying && _media!.Statistics.DecodedVideo > 3, TimeSpan.FromSeconds(10), timeout.Token);
            report.Checks["next_video_really_decodes"] = _media!.Statistics.DecodedVideo > 3 && _media.Statistics.DecodedAudio > 0;
            await WaitForUiAsync(() => _operation is null, TimeSpan.FromSeconds(5), timeout.Token);
            report.Checks["malformed_adjacent_subtitle_is_rejected"] = _currentSubtitlePath is null &&
                _currentSubtitleTrackId == -1 && SubtitleLabel.Text == "Локални титл није исправан.";
            Close();
            report.Checks["window_closes_and_disposes_native_player"] = _closing && !IsVisible;
            report.Status = report.Checks.Count > 0 && report.Checks.Values.All(value => value) ? "PASS" : "FAIL";
        }
        catch (Exception ex)
        {
            report.Status = "FAIL";
            report.Error = ex.GetType().Name + ": " + SafeMessage(ex);
        }
        report.FinishedUtc = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Status == "PASS" ? 0 : 1;
    }

    private void RaiseTestKey(Key key)
    {
        UIElement target = VideoOverlay.IsVisible ? VideoOverlay : this;
        Keyboard.Focus(target);
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("WPF клавијатура нема активан прозор.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private static async Task WaitForUiAsync(Func<bool> condition, TimeSpan limit, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > limit) throw new TimeoutException("WPF/native услов није потврђен у дозвољеном времену.");
            await Task.Delay(50, cancellationToken);
        }
    }
    private sealed class UiTestReport
    {
        public string Status { get; set; } = "FAIL";
        public string Scope { get; } = "windows_wpf_controls";
        public string OperatingSystem { get; } = RuntimeInformation.OSDescription;
        public string ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset FinishedUtc { get; set; }
        public Dictionary<string, bool> Checks { get; } = new(StringComparer.Ordinal);
        public List<string> Screenshots { get; } = [];
        public int ActiveSubtitleTrack { get; set; }
        public string? Error { get; set; }
        public string[] NotTested { get; } =
        [
            "Human confirmation of audible output and screenshot appearance",
            "Hardware keyboard/mouse interaction, multi-monitor DPI combinations, codecs beyond the fixture",
            "Live subtitle provider searches/downloads and access quotas",
            "Local speech recognition/translation and subjective subtitle synchronization quality",
            "Installer, signature reputation, file associations and uninstall"
        ];
    }
}
