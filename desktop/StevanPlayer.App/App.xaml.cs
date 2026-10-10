using System.Windows;

namespace StevanPlayer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            if (e.Args.Length > 0 && e.Args[0] is "--smoke-test" or "--ui-smoke-test")
            {
                if (e.Args.Length != 4)
                {
                    Shutdown(2);
                    return;
                }
                if (e.Args[0] == "--smoke-test")
                {
                    int result = await SmokeTestRunner.RunAsync(e.Args[1], e.Args[2], e.Args[3]);
                    Shutdown(result);
                }
                else
                {
                    string testDirectory = Path.Combine(Path.GetTempPath(), "StevanPlayer-ui-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(testDirectory);
                    SettingsStore.TestDataDirectory = Path.Combine(testDirectory, "Settings");
                    try
                    {
                        var testWindow = new MainWindow(new StevanPlayer.Core.SubtitleCatalog([
                            new StevanPlayer.Core.Providers.LocalSubtitleProvider([testDirectory])
                        ]));
                        MainWindow = testWindow;
                        testWindow.Show();
                        int result = await testWindow.RunUiSmokeTestAsync(e.Args[1], e.Args[2], e.Args[3], testDirectory);
                        if (testWindow.IsVisible) testWindow.Close();
                        Shutdown(result);
                    }
                    finally
                    {
                        SettingsStore.TestDataDirectory = null;
                        try { Directory.Delete(testDirectory, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                return;
            }
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            if (e.Args.Length > 0)
                window.OpenInitialFiles(e.Args.Where(File.Exists).ToArray());
        }
        catch (Exception ex)
        {
            if (e.Args.Length == 4 && e.Args[0] is "--smoke-test" or "--ui-smoke-test")
            {
                try
                {
                    string path = Path.GetFullPath(e.Args[3]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Status = "FAIL", Error = ex.GetType().Name + ": " + ex.Message,
                        ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        Scope = e.Args[0] == "--ui-smoke-test" ? "windows_wpf_controls" : "native_windows_decode"
                    }));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Shutdown(1);
                return;
            }
            MessageBox.Show("Плејер не може да се покрене. Проверите да ли је цео инсталациони пакет присутан.\n\n" +
                ex.GetType().Name + ": " + ex.Message, "СТЕВАНplejer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
