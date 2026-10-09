using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StevanPlayer.Core;

namespace StevanPlayer.App;

public partial class SettingsWindow : Window
{
    private readonly PlayerSettings _initial;
    public PlayerSettings? Result { get; private set; }

    public SettingsWindow(PlayerSettings settings)
    {
        InitializeComponent();
        _initial = settings;
        AutoSubtitlesBox.IsChecked = settings.AutoSubtitles;
        CloudConsentBox.IsChecked = settings.AllowCloudTranscription;
        AutoSynchronizeBox.IsChecked = settings.AutoSynchronize;
        OpenSubtitlesKeyBox.Password = settings.OpenSubtitlesApiKey;
        OpenSubtitlesTokenBox.Password = settings.OpenSubtitlesToken;
        SubDlKeyBox.Password = settings.SubDlApiKey;
        AiKeyBox.Password = settings.AiApiKey;
        TitloviUsernameBox.Text = settings.TitloviUsername;
        TitloviPasswordBox.Password = settings.TitloviPassword;
        TranslationModelBox.Text = settings.TranslationModel;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AutoSynchronizeBox.IsChecked == true && CloudConsentBox.IsChecked != true)
        {
            MessageBox.Show(this, "Аутоматско усклађивање захтева дозволу за слање звука и текста OpenAI-ју.",
                "Усклађивање", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CloudConsentBox.IsChecked == true && string.IsNullOrWhiteSpace(AiKeyBox.Password))
        {
            MessageBox.Show(this, "За дозвољену AI обраду унесите OpenAI API кључ, или искључите ову опцију.",
                "AI обрада", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string model = TranslationModelBox.Text.Trim();
        if (model.Length == 0 || model.Length > 100 || model.Any(char.IsWhiteSpace))
        {
            MessageBox.Show(this, "Унесите назив доступног модела за превођење, на пример gpt-4.1-mini.",
                "Модел", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = new PlayerSettings
        {
            PreferredLanguage = _initial.PreferredLanguage,
            AutoSubtitles = AutoSubtitlesBox.IsChecked == true,
            SubtitleOffsetMilliseconds = _initial.SubtitleOffsetMilliseconds,
            OpenSubtitlesApiKey = OpenSubtitlesKeyBox.Password.Trim(),
            OpenSubtitlesToken = OpenSubtitlesTokenBox.Password.Trim(),
            SubDlApiKey = SubDlKeyBox.Password.Trim(),
            AiApiKey = AiKeyBox.Password.Trim(),
            TitloviUsername = TitloviUsernameBox.Text.Trim(),
            TitloviPassword = TitloviPasswordBox.Password,
            TranslationModel = model,
            AllowCloudTranscription = CloudConsentBox.IsChecked == true,
            AutoSynchronize = AutoSynchronizeBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address }) return;
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Прегледач није доступан. Адреса: " + address, "Веза", MessageBoxButton.OK); }
    }
}
