using System.ComponentModel;
using System.Windows;
using StevanPlayer.Core;
using StevanPlayer.Core.Generation;

namespace StevanPlayer.App;

public partial class SettingsWindow : Window
{
    private readonly PlayerSettings _initial;
    private readonly LocalModelManager _models;
    private CancellationTokenSource? _download;
    private bool _closing;
    public PlayerSettings? Result { get; private set; }

    public SettingsWindow(PlayerSettings settings, LocalModelManager models)
    {
        InitializeComponent();
        _initial = settings;
        _models = models;
        AutoSubtitlesBox.IsChecked = settings.AutoSubtitles;
        LocalAiBox.IsChecked = settings.UseLocalAi;
        AutoSynchronizeBox.IsChecked = settings.AutoSynchronize;
        DownloadModelsButton.IsEnabled = LocalAiBox.IsEnabled = AutoSynchronizeBox.IsEnabled = Environment.Is64BitProcess;
        ModelStatusText.Text = !Environment.Is64BitProcess ? "Локални превод захтева 64-bit издање плејера." :
            models.IsInstalled ? "Локални модели су преузети. При обради проверавам њихову исправност." : "Локални модели још нису преузети.";
    }

    private async void DownloadModels_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null) return;
        var cancellation = new CancellationTokenSource();
        _download = cancellation;
        DownloadModelsButton.IsEnabled = SaveButton.IsEnabled = false;
        CancelModelsButton.Visibility = Visibility.Visible;
        try
        {
            await _models.DownloadAsync(new Progress<string>(message => { if (!_closing) ModelStatusText.Text = message; }), cancellation.Token);
            if (!_closing) ModelStatusText.Text = "Локални модели су преузети и проверени.";
        }
        catch (OperationCanceledException) { if (!_closing) ModelStatusText.Text = "Преузимање је отказано."; }
        catch (Exception ex) { if (!_closing) ModelStatusText.Text = "Преузимање није успело: " + (ex.Message.Length > 500 ? ex.Message[..500] : ex.Message); }
        finally
        {
            _download = null;
            cancellation.Dispose();
            if (!_closing)
            {
                DownloadModelsButton.IsEnabled = SaveButton.IsEnabled = true;
                CancelModelsButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelModels_Click(object sender, RoutedEventArgs e) => _download?.Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AutoSynchronizeBox.IsChecked == true && LocalAiBox.IsChecked != true && Environment.Is64BitProcess)
        {
            MessageBox.Show(this, "За аутоматско усклађивање укључите локално препознавање и превод.",
                "Усклађивање", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = new PlayerSettings
        {
            PreferredLanguage = _initial.PreferredLanguage,
            AutoSubtitles = AutoSubtitlesBox.IsChecked == true,
            SubtitleOffsetMilliseconds = _initial.SubtitleOffsetMilliseconds,
            UseLocalAi = Environment.Is64BitProcess && LocalAiBox.IsChecked == true,
            AutoSynchronize = Environment.Is64BitProcess && AutoSynchronizeBox.IsChecked == true
        };
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _download?.Cancel();
        base.OnClosing(e);
    }
}
