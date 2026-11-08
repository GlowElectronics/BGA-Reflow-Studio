using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BGAReflowStudio;

public partial class App : Application
{
    /// <summary>Displayed product version. Bump only on a requested release.</summary>
    internal const string AppVersion = "1.0";

    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Numbers are formatted with a comma decimal separator on disk and on the wire.
        Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture("pl-PL");

        DispatcherUnhandledException += (_, ex) => { LogFatal(ex.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { if (ex.ExceptionObject is Exception x) LogFatal(x); };

        base.OnStartup(e);

        // Hidden developer self-test: "--selftest screenshot|serialdialog <outPath>" renders
        // the relevant window, saves a PNG with no synthetic input, then exits.
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            RunSelfTest(e.Args[1], e.Args[2]);
            return;
        }

        // Allow only one running instance.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\BGAReflowStudio.SingleInstance", out bool isNew);
        if (!isNew)
        {
            new ConfirmWindow("Already Running", "BGA Reflow Studio is already running.", "OK", showCancel: false)
                { WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private void RunSelfTest(string mode, string outPath)
    {
        if (mode == "manuals")
        {
            var mw = new MainWindow { SkipExitConfirm = true };
            mw.Loaded += (_, _) =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                t.Tick += (_, _) => { t.Stop(); mw.OpenManualsMenuForTest(); };
                t.Start();
            };
            mw.Show();
            return; // left open for an external screen capture
        }

        Window window = mode switch
        {
            "serialdialog" => new SerialSettingsWindow(Path.Combine(AppContext.BaseDirectory, "settings.ini")),
            "about" => new AboutWindow(),
            "support" => new SupportWindow(),
            "rename" => new RenameWindow(3, "Xbox One"),
            "createprofile" => new RenameWindow("New profile", "Name (without .ini):", "New") { Title = "Create a Profile" },
            "confirm" => new ConfirmWindow("Unsaved changes", "You have unsaved changes to the current profile. Save them before exiting?", "Save & exit", "Don't save"),
            "removemachine" => new RemoveMachineWindow(new[] { "Xbox One Rig", "Bench Reflow" }),
            "terminal" => MakeTerminalDemo(),
            "complete" => new ConfirmWindow("Profile Complete", "The profile has finished and the cycle has been stopped.", "OK", showCancel: false),
            "compareok" => new ConfirmWindow("Compare & Start", "All parameters match the controller. Start the process?", "Start"),
            "comparenok" => new ConfirmWindow("Compare & Start",
                "3 parameter(s) differ from the PC profile.\n\nDiffering: R2, L3, HB\n\nUpload the PC values to the controller first, or start without updating?",
                "Upload & start", "Start anyway"),
            "downloadnew" => new ConfirmWindow("Download from Controller",
                "This downloads all ten profile sets from the controller and overwrites the data in the current profile file (“Default.ini”).\n\nCreate a new profile file for the downloaded data instead?",
                "New file", "Current file"),
            "uploadconfirm" => new ConfirmWindow("Upload to Controller",
                "This uploads all ten profile sets to the controller, overwriting the profile data stored on the machine.\n\nContinue?",
                "Upload"),
            "exportdb" => new ConfirmWindow("Export Local Database",
                "This exports only the local database of your custom profile-set names.\n\nIt is NOT a profile export — the profile recipes are stored separately in the .ini files in the \\profiles folder.\n\nContinue?",
                "Export"),
            "cleandb" => new ConfirmWindow("Clean Database",
                "This removes all locally stored application data in:\n" +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BGA Reflow Studio") +
                "\n\nYour custom profile names will be permanently deleted. The profile .ini files themselves are not affected.\n\nContinue?",
                "Clean"),
            _ => new MainWindow { SkipExitConfirm = true },
        };

        window.Loaded += (_, _) =>
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { RenderToPng(window, outPath); }
                finally { Shutdown(); }
            };
            t.Start();
        };
        window.Show();
    }

    private static SerialTerminalWindow MakeTerminalDemo()
    {
        var w = new SerialTerminalWindow();
        w.AppendLine("2026-11-03 10:15:01 open COM3 9600 7E1 DTR=True RTS=True CTS=True DSR=True CD=False");
        w.AppendLine("10:15:01.669 TX 04 30 30 31 31 23 33 05");
        w.AppendLine("10:15:01.699 RX 02 23 33 20 20 20 31 2E 03 2C  '#3' crc=OK");
        w.AppendLine("10:15:02.610 TX 04 30 30 31 31 50 56 05");
        w.AppendLine("10:15:02.634 RX 02 50 56 20 31 33 38 2E 03 2A  'PV' crc=OK");
        w.AppendLine("10:15:03.238 TX 04 30 30 31 31 53 50 05");
        w.AppendLine("10:15:03.261 RX 02 53 50 20 31 34 38 2E 03 3B  'SP' crc=OK");
        return w;
    }

    internal static void RenderToPng(Window w, string path)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        int pw = Math.Max(1, (int)Math.Ceiling(w.ActualWidth * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Ceiling(w.ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void LogFatal(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "achi-reflow.log"), $"{DateTime.Now:u} FATAL {ex}\r\n"); }
        catch { }
    }
}
