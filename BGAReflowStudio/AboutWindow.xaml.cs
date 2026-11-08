using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace BGAReflowStudio;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + App.AppVersion;

        // Build label: release-version_date_time, e.g. "Build: 0.1_110226_1646"
        // (version is fixed per release; the date/time stamp is baked in at each build).
        var stamp = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "";
        BuildText.Text = $"Build: {App.AppVersion}_{stamp}";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* ignore */ }
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
