using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using StevanPlayer.Core;
using StevanPlayer.Core.Generation;
using StevanPlayer.Core.Providers;

namespace StevanPlayer.App;

public partial class MainWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly HttpClient _providerHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _aiHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SubtitleCatalog _catalog;
    private readonly ISubtitleGenerator _generator;
    private readonly SettingsStore _settingsStore = new();
    private PlayerSettings _settings;
    private readonly ObservableCollection<PlaylistEntry> _playlist = [];
    private readonly DispatcherTimer _ticker;
    private CancellationTokenSource? _operation;
    private Media? _media;
    private MediaIdentity? _identity;
    private string? _currentPath;
    private string? _currentSubtitlePath;
    private int _currentSubtitleTrackId = -1, _trackSelectionSequence;
    private IReadOnlyList<SubtitleCue>? _transcript;
    private string? _transcriptLanguage;
    private int _generation;
    private bool _closing, _initializing = true, _seeking, _updatingSeek, _fullscreen;
    private WindowStyle _previousStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousState;
    private Rect _previousBounds;
    private bool _previousTopmost;
    private DateTime _lastMouseActivity = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _libVlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-sub-autodetect-file", "--quiet");
        _player = new MediaPlayer(_libVlc) { Volume = 75, EnableKeyInput = false, EnableMouseInput = false };
        Video.MediaPlayer = _player;
        _catalog = new SubtitleCatalog([new OpenSubtitlesProvider(_providerHttp), new SubDlProvider(_providerHttp), new TitloviProvider(_providerHttp)]);
        _generator = new OpenAiSubtitleGenerator(_aiHttp, new LibVlcAudioExtractor());
        PlaylistList.ItemsSource = _playlist;
        LanguageBox.SelectedIndex = _settings.PreferredLanguage switch { "hr" => 1, "bs" => 2, _ => 0 };
        UpdateOffsetLabel();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (_, _) => UpdatePlaybackUi();
        _ticker.Start();
        _player.Playing += (_, _) => Dispatch(() =>
        {
            _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
            Welcome.Visibility = Visibility.Collapsed;
            PlayButton.Content = "Ⅱ Пауза";
        });
        _player.EndReached += (_, _) => Dispatch(() =>
        {
            if (_player.State != VLCState.Ended) return;
            PlayButton.Content = "▶ Понови";
            Footer.Visibility = Visibility.Visible;
            if (!NextMedia()) SetStatus("Репродукција је завршена.");
        });
        _player.EncounteredError += (_, _) => Dispatch(() =>
        {
            if (_player.State != VLCState.Error) return;
            CancelOperation();
            ShowVideoBanner("Видео не може да се репродукује. Проверите датотеку, кодек или директну адресу.");
            SetStatus("Грешка репродукције. Отворите другу датотеку или проверите извор.");
        });
        _initializing = false;
        if (_settingsStore.Warning is not null) SetStatus(_settingsStore.Warning);
    }

    public void OpenInitialFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        SetPlaylist(paths);
        OpenMedia(paths[0]);
    }

    private void SetPlaylist(IEnumerable<string> paths)
    {
        _playlist.Clear();
        foreach (string path in paths) _playlist.Add(new PlaylistEntry(path, DisplayName(path)));
    }

    private void OpenMedia(string path)
    {
        if (_closing) return;
        CancelOperation();
        _generation++;
        _identity = null;
        _transcript = null;
        _transcriptLanguage = null;
        _currentSubtitlePath = null;
        _currentSubtitleTrackId = -1;
        CloseTrackMenus();
        _currentPath = path;
        _player.Stop();
        _media?.Dispose();
        bool remote = IsRemote(path);
        _media = new Media(_libVlc, path, remote ? FromType.FromLocation : FromType.FromPath);
        _media.AddOption(":no-sub-autodetect-file");
        string? local = remote ? null : MediaIdentifier.FindLocalSubtitle(path, _settings.PreferredLanguage);
        if (local is not null)
        {
            if (_media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(Path.GetFullPath(local)).AbsoluteUri))
                _currentSubtitlePath = local;
            else local = null;
        }
        ResultsList.ItemsSource = null;
        DownloadButton.IsEnabled = false;
        SyncButton.IsEnabled = local is not null && !remote;
        SearchButton.IsEnabled = true;
        GenerateButton.IsEnabled = !remote;
        LocalSubtitleButton.IsEnabled = true;
        PlayButton.IsEnabled = StopButton.IsEnabled = true;
        SubtitleLabel.Text = local is null ? "Титл још није учитан." : "Додат локални титл: " + Path.GetFileName(local);
        MediaName.Text = DisplayName(path);
        Title = DisplayName(path) + " · СТЕВАНplejer";
        Welcome.Visibility = Visibility.Collapsed;
        VideoBanner.Visibility = Visibility.Collapsed;
        PlaylistList.SelectedItem = _playlist.FirstOrDefault(item => item.Path == path);
        SeekSlider.Value = 0;
        if (!_player.Play(_media))
        {
            ShowVideoBanner("Видео не може да се покрене.");
            SetStatus("Видео не може да се покрене. Проверите датотеку или адресу.");
            return;
        }
        SetStatus(local is null ? "Репродукција: " + DisplayName(path) : "Додат локални титл: " + Path.GetFileName(local));
        if (local is not null) _ = ConfirmInitialSubtitleAsync(local, _generation);
        if (local is null && _settings.AutoSubtitles) _ = SearchSubtitlesAsync(automatic: true);
    }

    private async Task SearchSubtitlesAsync(bool automatic)
    {
        if (_currentPath is null || _closing) return;
        var context = BeginOperation();
        string path = _currentPath;
        try
        {
            SetStatus("Препознајем филм и претражујем титлове…");
            MediaIdentity identity = await MediaIdentifier.IdentifyAsync(path, context.Cancellation.Token);
            if (!IsCurrent(context)) return;
            _identity = identity;
            SubtitleSearchResult results = await _catalog.SearchAsync(identity, _settings, context.Cancellation.Token);
            if (!IsCurrent(context)) return;
            ResultsList.ItemsSource = results.Candidates;
            string notices = string.Join(" · ", results.Notices.Select(item => item.Message));
            if (results.Candidates.Count == 0)
            {
                SetStatus("Титл није пронађен." + (notices.Length == 0 ? "" : " " + notices));
                if (automatic && !IsRemote(path) && AiIsEnabled()) await GenerateWithinOperationAsync(context);
                return;
            }
            SetStatus($"Пронађено титлова: {results.Candidates.Count}." + (notices.Length == 0 ? "" : " " + notices));
            if (!automatic) return;
            SubtitleCandidate[] automaticCandidates = results.Candidates.Where(candidate => ConfidentMatch(candidate, identity)).Take(6).ToArray();
            if (automaticCandidates.Length == 0)
            {
                SetStatus("Пронађени титлови немају довољно поуздану подударност са видео датотеком. Изаберите одговарајуће издање у резултатима.");
                return;
            }
            foreach (SubtitleCandidate candidate in automaticCandidates)
            {
                context.Cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    string subtitle = await _catalog.DownloadAsync(candidate, _settings, SettingsStore.CacheDirectory,
                        context.Cancellation.Token, identity);
                    if (!IsCurrent(context)) return;
                    if (!await AttachSubtitleAsync(subtitle, context)) continue;
                    ResultsList.SelectedItem = candidate;
                    SetStatus($"Титл је укључен: {candidate.ProviderId} · {candidate.Language} · {candidate.Release}");
                    if (_settings.AutoSynchronize && AiIsEnabled() && !IsRemote(path))
                        await TrySynchronizeWithinOperationAsync(context);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (IsCurrent(context)) SetStatus("Изабрани титл није доступан: " + SafeMessage(ex)); }
            }
            if (IsCurrent(context)) SetStatus("Титлови су пронађени, али нису учитани. Изаберите други резултат или додајте локални титл.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(context)) SetStatus("Претрага није успела: " + SafeMessage(ex)); }
        finally { FinishOperation(context); }
    }

    private async Task ConfirmInitialSubtitleAsync(string path, int generation)
    {
        try
        {
            for (int i = 0; i < 100 && !_closing && generation == _generation; i++)
            {
                if (_player.Spu >= 0 && _player.SpuDescription.Any(track => track.Id == _player.Spu))
                {
                    if (_currentSubtitlePath == path)
                    {
                        _currentSubtitleTrackId = _player.Spu;
                        _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
                        SubtitleLabel.Text = "Локални титл: " + Path.GetFileName(path);
                        SetStatus("Изабран је титл за репродукцију: " + Path.GetFileName(path));
                    }
                    return;
                }
                if (_player.State is VLCState.Error or VLCState.Ended) break;
                await Task.Delay(100);
            }
            if (!_closing && generation == _generation && _currentSubtitlePath == path)
                SetStatus("Локални титл је додат, али активна стаза титла није потврђена. Покушајте дугме Локални титл.");
        }
        catch (ObjectDisposedException) when (_closing) { }
    }

    private async Task DownloadSelectedAsync()
    {
        if (ResultsList.SelectedItem is not SubtitleCandidate candidate || _currentPath is null) return;
        var context = BeginOperation();
        try
        {
            SetStatus("Преузимам титл…");
            MediaIdentity identity = _identity ?? await MediaIdentifier.IdentifyAsync(_currentPath, context.Cancellation.Token);
            string subtitle = await _catalog.DownloadAsync(candidate, _settings, SettingsStore.CacheDirectory,
                context.Cancellation.Token, identity);
            if (!IsCurrent(context)) return;
            if (!await AttachSubtitleAsync(subtitle, context)) throw new InvalidOperationException("Плејер није прихватио титл.");
            SetStatus($"Укључен титл: {candidate.Release} · {candidate.ProviderId}");
            if (_settings.AutoSynchronize && AiIsEnabled() && !IsRemote(_currentPath!))
                await TrySynchronizeWithinOperationAsync(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(context)) SetStatus("Преузимање није успело: " + SafeMessage(ex)); }
        finally { FinishOperation(context); }
    }

    private async Task<bool> AttachSubtitleAsync(string path, OperationContext context)
    {
        for (int i = 0; i < 120 && IsCurrent(context); i++)
        {
            if (_player.State is VLCState.Playing or VLCState.Paused) break;
            if (_player.State is VLCState.Error or VLCState.Ended or VLCState.Stopped) return false;
            await Task.Delay(100, context.Cancellation.Token);
        }
        if (!IsCurrent(context) || _player.State is not (VLCState.Playing or VLCState.Paused)) return false;
        if (_currentSubtitlePath == path && _currentSubtitleTrackId >= 0 && _player.Spu == _currentSubtitleTrackId)
        {
            _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
            return true;
        }
        var previousTracks = _player.SpuDescription.Select(track => track.Id).ToHashSet();
        bool wasPaused = _player.State == VLCState.Paused;
        if (!_player.AddSlave(MediaSlaveType.Subtitle, new Uri(Path.GetFullPath(path)).AbsoluteUri, true)) return false;
        try
        {
            if (wasPaused) _player.SetPause(false);
            // AddSlave acceptance is asynchronous. Success requires the new external track to be selected.
            for (int i = 0; i < 60 && IsCurrent(context); i++)
            {
                int selected = _player.Spu;
                if (selected >= 0 && !previousTracks.Contains(selected) && _player.SpuDescription.Any(track => track.Id == selected))
                {
                    _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
                    _currentSubtitlePath = path;
                    _currentSubtitleTrackId = selected;
                    SubtitleLabel.Text = "Укључен: " + Path.GetFileName(path);
                    SyncButton.IsEnabled = _currentPath is not null && !IsRemote(_currentPath);
                    return true;
                }
                if (_player.State is VLCState.Error or VLCState.Ended or VLCState.Stopped) return false;
                await Task.Delay(100, context.Cancellation.Token);
            }
            return false;
        }
        finally { if (wasPaused && !_closing && context.Generation == _generation) _player.SetPause(true); }
    }

    private async Task<IReadOnlyList<SubtitleCue>> GetTranscriptAsync(OperationContext context)
    {
        if (_transcript is not null && _transcriptLanguage == _settings.PreferredLanguage) return _transcript;
        if (_currentPath is null || IsRemote(_currentPath)) throw new InvalidOperationException("AI обрада захтева локалну видео датотеку.");
        IReadOnlyList<SubtitleCue> cues = await _generator.GenerateAsync(_currentPath, _settings.PreferredLanguage, _settings,
            new Progress<string>(message => { if (IsCurrent(context)) SetStatus(message); }), context.Cancellation.Token);
        if (!IsCurrent(context)) throw new OperationCanceledException(context.Cancellation.Token);
        _transcript = cues;
        _transcriptLanguage = _settings.PreferredLanguage;
        return cues;
    }

    private async Task GenerateWithinOperationAsync(OperationContext context)
    {
        IReadOnlyList<SubtitleCue> cues = await GetTranscriptAsync(context);
        if (!IsCurrent(context)) return;
        string path = await SaveGeneratedAsync(cues, "generated", context.Cancellation.Token);
        if (!IsCurrent(context)) return;
        _settings.SubtitleOffsetMilliseconds = 0;
        UpdateOffsetLabel();
        if (!await AttachSubtitleAsync(path, context)) throw new InvalidOperationException("Генерисани титл није учитан; поновите репродукцију и додајте га из кеша.");
        SaveSettings();
        SetStatus("AI титл је укључен. Проверите превод и временску усклађеност; препознавање може садржати грешке.");
    }

    private async Task SynchronizeWithinOperationAsync(OperationContext context)
    {
        if (_currentSubtitlePath is null || _player.Spu != _currentSubtitleTrackId || _currentSubtitleTrackId < 0)
            throw new InvalidOperationException("Прво укључите спољни титл који желите да ускладите.");
        string subtitle = _currentSubtitlePath;
        var original = SubtitleFormat.Parse(SubtitleFormat.Decode(await File.ReadAllBytesAsync(subtitle, context.Cancellation.Token)), Path.GetExtension(subtitle));
        var transcript = await GetTranscriptAsync(context);
        if (!IsCurrent(context) || _currentSubtitlePath != subtitle) return;
        var corrected = SubtitleSynchronizer.Synchronize(original, transcript);
        string path = await SaveGeneratedAsync(corrected.Cues, "synchronized", context.Cancellation.Token);
        if (!IsCurrent(context)) return;
        _settings.SubtitleOffsetMilliseconds = 0;
        UpdateOffsetLabel();
        if (!await AttachSubtitleAsync(path, context)) throw new InvalidOperationException("Усклађени титл није учитан.");
        SaveSettings();
        SetStatus($"Титл је усклађен: помак {corrected.OffsetSeconds:+0.00;-0.00;0.00} s, брзина {corrected.TimeScale:0.0000}, подударања {corrected.AnchorCount}.");
    }

    private async Task TrySynchronizeWithinOperationAsync(OperationContext context)
    {
        try { await SynchronizeWithinOperationAsync(context); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (IsCurrent(context)) SetStatus("Титл је укључен, али аутоматско усклађивање није успело: " + SafeMessage(ex));
        }
    }

    private static async Task<string> SaveGeneratedAsync(IReadOnlyList<SubtitleCue> cues, string purpose, CancellationToken cancellation)
    {
        Directory.CreateDirectory(SettingsStore.CacheDirectory);
        string path = Path.Combine(SettingsStore.CacheDirectory, purpose + "-" + Guid.NewGuid().ToString("N") + ".srt");
        await File.WriteAllTextAsync(path, SubtitleFormat.ToSrt(cues), new UTF8Encoding(false), cancellation);
        return path;
    }

    private async Task RunAiOperationAsync(bool synchronize)
    {
        if (_currentPath is null || IsRemote(_currentPath)) return;
        if (!AiIsEnabled())
        {
            MessageBox.Show(this, "У Подешавањима унесите OpenAI API кључ и дозволите слање звука и текста. API обрада може бити наплаћена.",
                "AI титлови", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var context = BeginOperation();
        try
        {
            if (synchronize) await SynchronizeWithinOperationAsync(context);
            else await GenerateWithinOperationAsync(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(context)) SetStatus("AI обрада није успела: " + SafeMessage(ex)); }
        finally { FinishOperation(context); }
    }

    private bool AiIsEnabled() => _settings.AllowCloudTranscription && !string.IsNullOrWhiteSpace(_settings.AiApiKey);

    private static bool ConfidentMatch(SubtitleCandidate candidate, MediaIdentity media)
    {
        if (candidate.HashMatched) return true;
        MediaIdentity release = MediaIdentifier.Parse(candidate.Release);
        if (media.Year.HasValue && release.Year.HasValue && media.Year != release.Year) return false;
        if (media.Episode.HasValue && (candidate.Episode ?? release.Episode) != media.Episode) return false;
        if (media.Season.HasValue && (candidate.Season ?? release.Season) != media.Season) return false;
        string[] title = Regex.Split(media.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).ToArray();
        var releaseWords = Regex.Split(release.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).ToHashSet();
        return title.Length > 0 && title.All(releaseWords.Contains);
    }

    private OperationContext BeginOperation()
    {
        CancelOperation();
        var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        Busy.Visibility = CancelButton.Visibility = Visibility.Visible;
        return new OperationContext(_generation, cancellation);
    }
    private bool IsCurrent(OperationContext context) => !_closing && context.Generation == _generation &&
        context.Cancellation == _operation && !context.Cancellation.IsCancellationRequested;
    private void FinishOperation(OperationContext context)
    {
        if (!_closing && context.Cancellation == _operation)
        {
            _operation = null;
            Busy.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        }
        context.Cancellation.Dispose();
    }
    private void CancelOperation()
    {
        _operation?.Cancel();
        _operation = null;
        Busy.Visibility = CancelButton.Visibility = Visibility.Collapsed;
    }

    private void UpdatePlaybackUi()
    {
        if (_closing) return;
        long duration = Math.Max(0, _player.Length), position = Math.Max(0, _player.Time);
        if (!_seeking)
        {
            _updatingSeek = true;
            SeekSlider.Value = duration > 0 ? Math.Clamp(position * 1000d / duration, 0, 1000) : 0;
            _updatingSeek = false;
        }
        SeekSlider.IsEnabled = _player.IsSeekable && duration > 0;
        AudioTrackButton.IsEnabled = SubtitleTrackButton.IsEnabled = _player.State is VLCState.Playing or VLCState.Paused;
        PositionLabel.Text = FormatTime(position);
        DurationLabel.Text = FormatTime(duration);
        PlayButton.Content = _player.IsPlaying ? "Ⅱ Пауза" : "▶ Пусти";
        MuteButton.Content = _player.Mute ? "Без звука" : "Звук";
        if (_currentSubtitlePath is not null && _player.Spu >= 0 && _player.SpuDelay != (long)_settings.SubtitleOffsetMilliseconds * 1000)
            _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
        if (_fullscreen)
        {
            bool visible = !_player.IsPlaying || _operation is not null || AudioTrackButton.ContextMenu?.IsOpen == true ||
                SubtitleTrackButton.ContextMenu?.IsOpen == true || DateTime.UtcNow - _lastMouseActivity < TimeSpan.FromSeconds(3);
            Footer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ExitFullscreenButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Mouse.OverrideCursor = visible ? null : Cursors.None;
        }
    }

    private void TogglePlayback()
    {
        if (_currentPath is null) { OpenFile_Click(this, new RoutedEventArgs()); return; }
        if (_player.IsPlaying) _player.Pause();
        else if (_player.State == VLCState.Paused) _player.Play();
        else if (_media is not null) _player.Play(_media);
    }
    private void SeekBy(long milliseconds)
    {
        if (!_player.IsSeekable) return;
        _player.Time = Math.Clamp(_player.Time + milliseconds, 0, Math.Max(0, _player.Length));
    }
    private bool NextMedia()
    {
        int index = _playlist.ToList().FindIndex(entry => entry.Path == _currentPath);
        if (index < 0 || index + 1 >= _playlist.Count) return false;
        OpenMedia(_playlist[index + 1].Path);
        return true;
    }

    private void SetOffset(int adjustment)
    {
        _settings.SubtitleOffsetMilliseconds = Math.Clamp(_settings.SubtitleOffsetMilliseconds + adjustment, -600_000, 600_000);
        _player.SetSpuDelay((long)_settings.SubtitleOffsetMilliseconds * 1000);
        UpdateOffsetLabel();
        SaveSettings();
        SetStatus("Кашњење титла: " + OffsetLabel.Text + ". Позитивна вредност приказује титл касније.");
    }
    private void UpdateOffsetLabel() => OffsetLabel.Text = (_settings.SubtitleOffsetMilliseconds / 1000d).ToString("+0.0;-0.0;0.0") + " s";
    private void SaveSettings()
    {
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { SetStatus("Подешавања нису сачувана. Проверите приступ корисничкој фасцикли."); }
    }
    private string SafeMessage(Exception exception)
    {
        string message = exception.Message;
        foreach (string secret in new[] { _settings.OpenSubtitlesApiKey, _settings.OpenSubtitlesToken, _settings.SubDlApiKey, _settings.AiApiKey, _settings.TitloviPassword })
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[заштићено]", StringComparison.Ordinal);
        return message.Length > 700 ? message[..700] : message;
    }
    private void SetStatus(string message) { if (!_closing) StatusText.Text = message; }
    private void ShowVideoBanner(string message) { VideoBannerText.Text = message; VideoBanner.Visibility = Visibility.Visible; }
    private void Dispatch(Action action)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        int generation = Volatile.Read(ref _generation);
        _ = Dispatcher.BeginInvoke(() => { if (!_closing && generation == _generation) action(); });
    }
    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
    }
    private static bool IsRemote(string path) => Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";
    private static string DisplayName(string path)
    {
        if (IsRemote(path) && Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
            return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) is { Length: > 0 } name ? name : uri.Host;
        return Path.GetFileName(path);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Отвори филм или епизоде", Multiselect = true,
            Filter = "Видео датотеке|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.ts;*.wmv;*.mpg;*.mpeg;*.flv;*.m2ts;*.vob|Све датотеке|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            SetPlaylist(dialog.FileNames.Order(StringComparer.OrdinalIgnoreCase));
            OpenMedia(_playlist[0].Path);
        }
    }
    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UrlWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Address is not null)
        { SetPlaylist([dialog.Address]); OpenMedia(dialog.Address); }
    }
    private async void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null) return;
        var dialog = new OpenFileDialog { Title = "Додај локални титл", Filter = "Титлови|*.srt;*.vtt;*.ass;*.ssa;*.sub|Све датотеке|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var context = BeginOperation();
        try { SetStatus(await AttachSubtitleAsync(dialog.FileName, context) ? "Укључен локални титл: " + dialog.SafeFileName : "Титл није учитан. Покрените репродукцију па покушајте поново."); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(context)) SetStatus("Титл није учитан: " + SafeMessage(ex)); }
        finally { FinishOperation(context); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CancelOperation();
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        { _settings = dialog.Result; SaveSettings(); SetStatus("Подешавања су сачувана за овог Windows корисника."); }
    }
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address }) return;
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch { SetStatus("Прегледач није доступан. Извор: " + address); }
    }
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        CancelOperation();
        _settings.PreferredLanguage = language;
        _transcript = null;
        SaveSettings();
        if (_currentPath is not null) _ = SearchSubtitlesAsync(automatic: true);
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchSubtitlesAsync(automatic: false);
    private async void Download_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();
    private async void Result_DoubleClick(object sender, MouseButtonEventArgs e) => await DownloadSelectedAsync();
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) => DownloadButton.IsEnabled = ResultsList.SelectedItem is SubtitleCandidate;
    private async void Generate_Click(object sender, RoutedEventArgs e) => await RunAiOperationAsync(synchronize: false);
    private async void Synchronize_Click(object sender, RoutedEventArgs e) => await RunAiOperationAsync(synchronize: true);
    private void Playlist_DoubleClick(object sender, MouseButtonEventArgs e) { if (PlaylistList.SelectedItem is PlaylistEntry entry) OpenMedia(entry.Path); }
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void Stop_Click(object sender, RoutedEventArgs e) { CancelOperation(); _player.Stop(); SetStatus("Репродукција је заустављена."); Footer.Visibility = Visibility.Visible; }
    private void Back_Click(object sender, RoutedEventArgs e) => SeekBy(-10_000);
    private void Forward_Click(object sender, RoutedEventArgs e) => SeekBy(10_000);
    private void Next_Click(object sender, RoutedEventArgs e) { if (!NextMedia()) SetStatus("Нема следећег видеа у листи."); }
    private void Mute_Click(object sender, RoutedEventArgs e) => _player.Mute = !_player.Mute;
    private void AudioTrack_Click(object sender, RoutedEventArgs e) => ShowTrackMenu(AudioTrackButton, audio: true);
    private void SubtitleTrack_Click(object sender, RoutedEventArgs e) => ShowTrackMenu(SubtitleTrackButton, audio: false);
    private void ShowTrackMenu(Button button, bool audio)
    {
        if (_closing || _media is null || _player.State is not (VLCState.Playing or VLCState.Paused)) return;
        CloseTrackMenus();
        int generation = _generation;
        Media media = _media;
        int selected = audio ? _player.AudioTrack : _player.Spu;
        var descriptions = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        var tracks = new List<(int Id, string Name)> { (-1, audio ? "Искључи звучне записе" : "Искључи титлове") };
        tracks.AddRange(descriptions.Where(track => track.Id >= 0).Select(track =>
            (track.Id, string.IsNullOrWhiteSpace(track.Name) ? (audio ? "Звучни запис " : "Титл запис ") + track.Id : track.Name)));
        var menu = new ContextMenu
        {
            PlacementTarget = button, Placement = PlacementMode.Top,
            Style = (Style)FindResource("TrackContextMenuStyle")
        };
        foreach (var track in tracks)
        {
            var item = new MenuItem
            {
                Header = track.Name, Tag = track.Id, IsCheckable = true, IsChecked = selected == track.Id,
                Style = (Style)FindResource("TrackMenuItemStyle")
            };
            item.Click += async (_, _) => await SelectTrackAsync(audio, track.Id, track.Name, generation, media, menu);
            menu.Items.Add(item);
        }
        if (tracks.Count == 1) menu.Items.Add(new MenuItem
        {
            Header = audio ? "Нема доступних звучних записа" : "Нема учитаних титлова", IsEnabled = false,
            Style = (Style)FindResource("TrackMenuItemStyle")
        });
        button.ContextMenu = menu;
        _lastMouseActivity = DateTime.UtcNow;
        menu.IsOpen = true;
    }
    private async Task SelectTrackAsync(bool audio, int trackId, string name, int generation, Media media, ContextMenu menu)
    {
        if (_closing || generation != _generation || !ReferenceEquals(media, _media)) return;
        if (!audio) CancelOperation(); // An explicit subtitle choice must not be overwritten by an in-flight automatic download/AI operation.
        int sequence = ++_trackSelectionSequence;
        menu.IsOpen = false;
        bool accepted = audio ? _player.SetAudioTrack(trackId) : _player.SetSpu(trackId);
        for (int attempt = 0; accepted && attempt < 20; attempt++)
        {
            if (_closing || generation != _generation || !ReferenceEquals(media, _media) || sequence != _trackSelectionSequence) return;
            if ((audio ? _player.AudioTrack : _player.Spu) == trackId)
            {
                if (!audio)
                {
                    bool externalSelected = trackId >= 0 && trackId == _currentSubtitleTrackId && _currentSubtitlePath is not null;
                    SyncButton.IsEnabled = externalSelected && _currentPath is not null && !IsRemote(_currentPath);
                    SubtitleLabel.Text = trackId < 0 ? "Титлови су искључени." :
                        externalSelected ? "Укључен: " + Path.GetFileName(_currentSubtitlePath) : "Укључен титл запис: " + name;
                }
                SetStatus((audio ? "Звучни запис: " : "Титл запис: ") + name);
                return;
            }
            await Task.Delay(75);
        }
        if (!_closing && generation == _generation && ReferenceEquals(media, _media) && sequence == _trackSelectionSequence)
            SetStatus("Плејер није потврдио избор " + (audio ? "звучног записа" : "титла") + ". Покушајте поново.");
    }
    private void CloseTrackMenus()
    {
        _trackSelectionSequence++;
        if (AudioTrackButton.ContextMenu is { } audio) audio.IsOpen = false;
        if (SubtitleTrackButton.ContextMenu is { } subtitle) subtitle.IsOpen = false;
        AudioTrackButton.ContextMenu = SubtitleTrackButton.ContextMenu = null;
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_player is not null) _player.Volume = (int)e.NewValue; }
    private void OffsetMinus_Click(object sender, RoutedEventArgs e) => SetOffset(-100);
    private void OffsetPlus_Click(object sender, RoutedEventArgs e) => SetOffset(100);
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelOperation(); SetStatus("Обрада титла је отказана."); }
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) { ApplySeek(); _seeking = false; }
    private void Seek_LostCapture(object sender, MouseEventArgs e) { if (_seeking) ApplySeek(); _seeking = false; }
    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updatingSeek && !_seeking && _player is not null) ApplySeek(); }
    private void ApplySeek() { if (_player.IsSeekable && _player.Length > 0) _player.Time = (long)(_player.Length * SeekSlider.Value / 1000); }
    private void VideoOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? origin = e.OriginalSource as DependencyObject;
        while (origin is not null)
        {
            if (origin is ButtonBase) return;
            origin = origin is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(origin) : LogicalTreeHelper.GetParent(origin);
        }
        Keyboard.Focus(VideoOverlay);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        { ToggleFullscreen(); e.Handled = true; }
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousStyle = WindowStyle; _previousState = WindowState; _previousResizeMode = ResizeMode;
            _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _previousTopmost = Topmost;
            Rect monitor = NativeWindowTools.MonitorBounds(this);
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Left = monitor.Left; Top = monitor.Top; Width = monitor.Width; Height = monitor.Height; Topmost = true;
            Header.Visibility = Sidebar.Visibility = Visibility.Collapsed; SidebarColumn.Width = new GridLength(0);
            _fullscreen = true; _lastMouseActivity = DateTime.UtcNow;
        }
        else
        {
            _fullscreen = false; Mouse.OverrideCursor = null;
            WindowState = WindowState.Normal; WindowStyle = _previousStyle; ResizeMode = _previousResizeMode;
            Left = _previousBounds.Left; Top = _previousBounds.Top; Width = _previousBounds.Width; Height = _previousBounds.Height;
            Topmost = _previousTopmost; WindowState = _previousState;
            Header.Visibility = Sidebar.Visibility = Footer.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(318); ExitFullscreenButton.Visibility = Visibility.Collapsed;
        }
    }
    private void Window_MouseMove(object sender, MouseEventArgs e) => _lastMouseActivity = DateTime.UtcNow;
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or PasswordBox or ComboBox) return;
        _lastMouseActivity = DateTime.UtcNow;
        if (e.Key == Key.F11 || e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
        else if (e.Key == Key.Space) TogglePlayback();
        else if (e.Key == Key.Left) SeekBy(-10_000);
        else if (e.Key == Key.Right) SeekBy(10_000);
        else if (e.Key == Key.Up) VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
        else if (e.Key == Key.Down) VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
        else if (e.Key == Key.M) _player.Mute = !_player.Mute;
        else if (e.Key == Key.G) SetOffset(-100);
        else if (e.Key == Key.H) SetOffset(100);
        else if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OpenFile_Click(this, new RoutedEventArgs());
        else return;
        e.Handled = true;
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        string[] files = paths.Where(File.Exists).ToArray();
        if (files.Length == 1 && Path.GetExtension(files[0]).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa" or ".sub")
            _ = AttachDroppedSubtitleAsync(files[0]);
        else if (files.Length > 0) OpenInitialFiles(files);
        e.Handled = true;
    }
    private async Task AttachDroppedSubtitleAsync(string path)
    {
        if (_currentPath is null) { SetStatus("Прво отворите видео, па превуците титл."); return; }
        var context = BeginOperation();
        try { SetStatus(await AttachSubtitleAsync(path, context) ? "Укључен: " + Path.GetFileName(path) : "Титл није учитан. Покрените репродукцију."); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(context)) SetStatus("Титл није учитан: " + SafeMessage(ex)); }
        finally { FinishOperation(context); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        CloseTrackMenus();
        CancelOperation();
        _ticker.Stop();
        Mouse.OverrideCursor = null;
        _player.Stop();
        Video.MediaPlayer = null;
        _player.Dispose();
        _media?.Dispose();
        _libVlc.Dispose();
        _providerHttp.Dispose();
        _aiHttp.Dispose();
    }

    private sealed record OperationContext(int Generation, CancellationTokenSource Cancellation);
    private sealed record PlaylistEntry(string Path, string Name);
}
