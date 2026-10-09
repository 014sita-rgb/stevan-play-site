using System.Windows;

namespace StevanPlayer.App;

public partial class UrlWindow : Window
{
    public string? Address { get; private set; }
    public UrlWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { UrlBox.Focus(); UrlBox.CaretIndex = UrlBox.Text.Length; };
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        string address = UrlBox.Text.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            MessageBox.Show(this, "Унесите исправну HTTP или HTTPS адресу без корисничког имена и лозинке.", "Адреса", MessageBoxButton.OK);
            return;
        }
        Address = address;
        DialogResult = true;
    }
}
