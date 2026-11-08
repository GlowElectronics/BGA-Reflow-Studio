using System.Windows;
using System.Windows.Navigation;

namespace BGAReflowStudio;

public partial class SupportWindow : Window
{
    private const string KofiUrl = "https://ko-fi.com/glowelectronics";

    public SupportWindow() => InitializeComponent();

    private void Kofi_Click(object sender, RoutedEventArgs e) => Open(KofiUrl);

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
