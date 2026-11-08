using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Microsoft.Win32;
using BGAReflowStudio.Models;
using BGAReflowStudio.Protocol;

namespace BGAReflowStudio;

/// <summary>One editable row of the 8-step profile grid.</summary>
public sealed class StepRow : INotifyPropertyChanged
{
    private decimal _ramp, _target, _dwell;
    public int Step { get; set; }

    public decimal Ramp { get => _ramp; set => Set(ref _ramp, Clamp(value, -0.01m, 99.99m)); }
    public decimal Target { get => _target; set => Set(ref _target, Clamp(value, 0m, 300m)); }
    public decimal Dwell { get => _dwell; set => Set(ref _dwell, Clamp(value, 0m, 999m)); }

    private static decimal Clamp(decimal v, decimal lo, decimal hi) => Math.Clamp(v, lo, hi);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref decimal field, decimal value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class MainWindow : Window
{
    // ---- paths / hardware ----
    private readonly string _appDir = AppContext.BaseDirectory;
    private string _settingsIni = "";
    private string _profilesFolder = "";
    private string _profilePath = "";
    private string _nameDbPath = "";   // local DB of custom profile names (never written to the .ini)
    private readonly Dictionary<int, string> _pendingNames = new(); // renames for the current file, saved on Save
    private ThermalProfile? _copiedSet;    // right-click Copy Set / Paste Set clipboard
    private string _copiedSetName = "";
    private readonly Pc410Controller _ctrl = new();
    private readonly PidRegulator _pid = new();

    private int _profileIndex;
    private bool _loading;
    private bool _dirty;               // unsaved edits to the current profile
    internal bool SkipExitConfirm;

    // ---- step rows ----
    private readonly ObservableCollection<StepRow> _steps = new();

    // ---- live values ----
    private int _topTemp, _bottomTemp, _setPoint, _segment, _alarm2, _ifRunning;

    // ---- run flags ----
    private bool _running, _hold, _compare;
    private bool _flagStart, _flagGoStart, _flagStop, _flagHold, _flagHotUpdate, _tryDoChange, _al2Stop;

    // ---- counters ----
    private int _counter3, _counter4, _counter6, _counter7, _counter9, _counter11;
    private int _worktime; private bool _flagWorking, _criticalShown;

    // ---- alarm ----
    private bool _alm, _flagTimeAlarm; private int _timeAlarmCount; private SoundPlayer? _alarmPlayer;

    // ---- transfer ----
    private bool _flar, _flal, _flad, _flahb, _flall, _flaSingle;
    private bool _flagar, _flagal, _flagad, _flagahb, _flagall, _flagaSingle;
    private bool _flagCompare; private int _errorCounter; private string _nameError = "";
    private int _xferStep, _xferProfile, _post;
    private bool _transferring;   // a multi-step Download / Upload / Compare is in progress
    private string? _awaiting;    // read-poll reply we're waiting for (null = not gated) — paces polls to replies
    private int _awaitTicks, _awaitRetries;
    private bool _flaFinishRead;  // terminal read step: save once the last reply (Hb) is in
    private const int AwaitMaxTicks = 3;     // ~360 ms to wait for a reply before re-polling
    private const int AwaitMaxRetries = 4;   // give up on one parameter after this many re-polls

    // ---- cycle time ----
    private int _cycleSeconds, _sek, _min;
    private int _lastMin, _lastSek; private bool _hadCycle;   // last completed/stopped cycle duration
    private int _lastBeepStep;                                 // last segment/step we beeped for
    private int _zeroSpSeconds;                                // seconds the Set Point (SV) has read 0 °C while running
    private bool _sawNonZeroSp;                                // Set Point has been > 0 at least once this run
    // Safety/Alarm: each threshold keeps both a static value and a dynamic offset so switching mode preserves both.
    private decimal _critStatic = 230, _critOffset = 15, _alarmStatic = 225, _alarmOffset = 10;

    // ---- chart ----
    private readonly List<double> _curveX = new(), _curveY = new();
    private readonly List<double> _t1X = new(), _t1Y = new();
    private readonly List<double> _t2X = new(), _t2Y = new();
    private readonly List<double> _spX = new(), _spY = new();
    private readonly List<double> _segX = new(), _segY = new();
    private int _bottomContinue;

    // ---- hot update selection ----
    private enum ParamKind { None, Ramp, Target, Dwell, Holdback }
    private ParamKind _selKind = ParamKind.None;
    private int _selStep;

    // ---- timers ----
    private readonly DispatcherTimer _t1 = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer _t2 = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _t3 = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _t4 = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public MainWindow()
    {
        InitializeComponent();
        _settingsIni = Path.Combine(_appDir, "settings.ini");
        StepsGrid.ItemsSource = _steps;
        for (int i = 0; i < 10; i++) PowerBottomBox.Items.Add($"{(i + 1) * 10}%");
        foreach (var m in new[] { "Static", "Dynamic" }) { CritModeBox.Items.Add(m); AlarmModeBox.Items.Add(m); }

        _ctrl.ResponseReceived += (s, v, d) => Ui(() => OnResponse(s, v, d));
        // ACK/NAK prove the controller is alive and responding, so they reset the comms watchdog
        // too — otherwise a long bulk upload (All To), which only draws ACKs, trips the 10 s timeout.
        // A single ACK is just one command of a transfer — don't announce "Transfer OK" per ACK.
        // During a multi-step transfer the completion message is shown when the whole run finishes.
        _ctrl.AckReceived += () => Ui(() => { _worktime = 0; if (!_transferring) StatusText.Text = "Transfer OK"; });
        _ctrl.NakReceived += () => Ui(() => { _worktime = 0; StatusText.Text = "Controller NAK"; });
        _ctrl.PortError += ex => Ui(() => LogError(ex));

        Loaded += OnLoaded;
        Closing += OnClosing;

        _t1.Tick += Timer1_Tick;
        _t2.Tick += Timer2_Tick;
        _t3.Tick += Timer3_Tick;
        _t4.Tick += Timer4_Tick;

        var plot = Plot.Plot;
        plot.Axes.Bottom.Label.Text = "Time [s]";
        plot.Axes.Left.Label.Text = "Temperature [°C]";
        plot.HideLegend();
        // Drop only the "Open in New Window" item from the right-click menu — it opens a static
        // snapshot that never updates; the "Pop out" button is the live equivalent. The rest of the
        // default menu (save/copy image, autoscale) is rebuilt unchanged.
        try
        {
            if (Plot.Menu is ScottPlot.WPF.WpfPlotMenu menu)
            {
                var items = menu.GetDefaultContextMenuItems();
                menu.Clear();
                foreach (var it in items)
                    if (it.OnInvoke is not null && !string.IsNullOrEmpty(it.Label) && it.Label != "Open in New Window")
                        menu.Add(it.Label, it.OnInvoke);
            }
        }
        catch { /* leave the default menu intact if the ScottPlot API differs */ }

        WireDirtyTracking();
        WireSettingsPersistence();
        UpdateSaveButton();
    }

    // =================================================================================
    //  Unsaved-changes tracking (Save button grey/green + exit prompt)
    // =================================================================================

    private void WireDirtyTracking()
    {
        foreach (var tb in new[] { HoldbackBox, CritBox, AlarmBox, TimeAlarmBox, AutoBoostBox, B40Box, B60Box, B80Box, B100Box })
            tb.TextChanged += (_, _) => SetDirty();
        foreach (var cb in new[] { Chk40, Chk60, Chk80, Chk100 })
        {
            cb.Checked += (_, _) => SetDirty();
            cb.Unchecked += (_, _) => SetDirty();
        }
        // Power-bottom changes during a run are driven by the controller/PID, not the user.
        PowerBottomBox.SelectionChanged += (_, _) => { if (!_running) SetDirty(); };
    }

    // Unsaved = edited profile fields OR pending profile-set renames.
    private bool HasUnsaved => _dirty || _pendingNames.Count > 0;

    private void SetDirty()
    {
        if (_loading) return;
        _dirty = true;
        UpdateSaveButton();
    }

    private void ClearDirty()
    {
        _dirty = false;
        UpdateSaveButton();
    }

    private void UpdateSaveButton()
    {
        if (HasUnsaved)
        {
            SaveButton.IsEnabled = true;
            SaveButton.Style = (Style)FindResource("SuccessButton");   // green
        }
        else
        {
            SaveButton.IsEnabled = false;                              // greyed out
            SaveButton.Style = (Style)FindResource(typeof(Button));
        }
    }

    /// <summary>Persist pending profile-set renames to the local name DB.</summary>
    private void SaveNames()
    {
        if (_pendingNames.Count == 0) return;
        foreach (var kv in _pendingNames) IniFile.Write(NameDbKey, kv.Key.ToString(), kv.Value, _nameDbPath);
        _pendingNames.Clear();
    }

    private void WireSettingsPersistence()
    {
        MachineBox.SelectionChanged += (_, _) => { if (!_loading) IniFile.Write("General", "Machine", MachineBox.SelectedIndex.ToString(), _settingsIni); };
        ControllerBox.SelectionChanged += (_, _) => { if (!_loading) IniFile.Write("General", "Controller", ControllerBox.SelectedIndex.ToString(), _settingsIni); };
        PortBox.SelectionChanged += (_, _) => { if (!_loading && PortBox.SelectedItem is string s && s.Length > 0) IniFile.Write("Serial", "Port", s, _settingsIni); };
    }

    // =================================================================================
    //  Lifecycle
    // =================================================================================

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Local DB for custom profile-set names (kept out of the .ini files).
        _nameDbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BGA Reflow Studio", "profile-names.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(_nameDbPath)!);

        // The Profile Library is every .ini file in <app>\profiles. Seed it if empty.
        _profilesFolder = Path.Combine(_appDir, "profiles");
        Directory.CreateDirectory(_profilesFolder);
        if (!Directory.EnumerateFiles(_profilesFolder, "*.ini").Any())
        {
            var shipped = Path.Combine(_appDir, "profiles.ini");
            var dest = Path.Combine(_profilesFolder, "Default.ini");
            if (File.Exists(shipped)) File.Copy(shipped, dest, overwrite: false);
            else SeedDefaultProfiles(dest);
        }

        LoadAlarmSound();
        Show1.IsChecked = Bool(IniFile.Read("General", "ShowTemp1", _settingsIni, "True"));
        // Off by default: Temp.2 (bottom sensor) is not wired on every machine and reads a huge value.
        Show2.IsChecked = Bool(IniFile.Read("General", "ShowTemp2", _settingsIni, "False"));
        SegBeep.IsChecked = Bool(IniFile.Read("General", "SegmentBeep", _settingsIni, "True"));

        // Restore machine / controller selections (port is restored by RefreshPorts).
        _loading = true;
        LoadCustomMachines();   // re-add user-defined machines before restoring the saved index
        MachineBox.SelectedIndex = ClampIndex(IniFile.Read("General", "Machine", _settingsIni, "0"), MachineBox.Items.Count);
        ControllerBox.SelectedIndex = ClampIndex(IniFile.Read("General", "Controller", _settingsIni, "0"), ControllerBox.Items.Count);
        _loading = false;

        RefreshPorts();
        HeaterBox.SelectedIndex = 0;

        PopulateLibraryFiles();
        // Restore the last-used file if it still exists, else the first one.
        var configured = IniFile.Read("General", "ProfilesFile", _settingsIni, "");
        var names = (List<string>)LibraryFilesList.ItemsSource;
        int sel = 0;
        if (configured.Length > 0)
        {
            var wanted = Path.GetFileNameWithoutExtension(configured);
            var i = names.FindIndex(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) sel = i;
        }
        if (names.Count > 0) LibraryFilesList.SelectedIndex = sel; // triggers LibraryFiles_SelectionChanged
        _t1.Start();
    }

    private void PopulateLibraryFiles()
    {
        var names = Directory.EnumerateFiles(_profilesFolder, "*.ini")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null).Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LibraryFilesList.ItemsSource = names;
    }

    private void LibraryFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryFilesList.SelectedItem is not string name) return;
        _pendingNames.Clear();            // discard unsaved renames from the previous file
        _profilePath = Path.Combine(_profilesFolder, name + ".ini");
        IniFile.Write("General", "ProfilesFile", _profilePath, _settingsIni);
        PopulateProfileNames();           // fills ProfileBox + Available Profiles list (resets their selection)
        _profileIndex = 0;
        if (ProfileBox.SelectedIndex == 0) LoadProfileToUi(); else ProfileBox.SelectedIndex = 0;
    }

    // ---- add / remove / rename profile files ------------------------------------------

    private void AddProfileFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RenameWindow("New profile", "Name (without .ini):", "New") { Owner = this, Title = "Create a Profile" };
        if (dlg.ShowDialog() != true) return;
        var name = Sanitize(dlg.Value);
        if (name.Length == 0) return;
        var path = Path.Combine(_profilesFolder, name + ".ini");
        if (File.Exists(path)) { InfoDialog("Create a Profile", $"A file named “{name}.ini” already exists."); return; }
        try
        {
            SeedDefaultProfiles(path);
            PopulateLibraryFiles();
            SelectLibraryFile(name);
            StatusText.Text = $"Created {name}.ini";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Create a Profile", ex.Message); }
    }

    private void RemoveProfileFile_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryFilesList.SelectedItem is not string name) return;
        if ((LibraryFilesList.ItemsSource as List<string>)?.Count <= 1)
        {
            InfoDialog("Delete Profile File", "At least one profile file must remain.");
            return;
        }
        var dlg = new ConfirmWindow("Delete Profile File",
            $"Delete profile file “{name}.ini”?\n\nThis permanently removes the file and its custom profile names. It cannot be undone.",
            "Delete") { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result != ConfirmResult.Primary) return;
        try
        {
            File.Delete(Path.Combine(_profilesFolder, name + ".ini"));
            IniFile.DeleteSection(name, _nameDbPath);
            _pendingNames.Clear();
            PopulateLibraryFiles();
            if ((LibraryFilesList.ItemsSource as List<string>)?.Count > 0) LibraryFilesList.SelectedIndex = 0;
            StatusText.Text = $"Deleted {name}.ini";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Delete Profile File", ex.Message); }
    }

    private void DuplicateProfileFile_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryFilesList.SelectedItem is not string src)
        {
            InfoDialog("Duplicate a Profile", "Select a profile file to duplicate first.");
            return;
        }
        var dlg = new RenameWindow("Duplicate profile", "Name for the copy (without .ini):", src + " copy") { Owner = this, Title = "Duplicate a Profile" };
        if (dlg.ShowDialog() != true) return;
        var name = Sanitize(dlg.Value);
        if (name.Length == 0 || string.Equals(name, src, StringComparison.OrdinalIgnoreCase)) return;
        var dest = Path.Combine(_profilesFolder, name + ".ini");
        if (File.Exists(dest)) { InfoDialog("Duplicate a Profile", $"A file named “{name}.ini” already exists."); return; }
        try
        {
            File.Copy(Path.Combine(_profilesFolder, src + ".ini"), dest);
            CopyNameDbSection(src, name);     // carry the custom profile-set names across
            PopulateLibraryFiles();
            SelectLibraryFile(name);
            StatusText.Text = $"Duplicated {src}.ini → {name}.ini";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Duplicate a Profile", ex.Message); }
    }

    private void CopyNameDbSection(string srcSection, string destSection)
    {
        for (int i = 0; i < 10; i++)
        {
            var v = IniFile.Read(srcSection, i.ToString(), _nameDbPath, "");
            if (v.Length > 0) IniFile.Write(destSection, i.ToString(), v, _nameDbPath);
        }
    }

    private void LibraryFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryFilesList.SelectedItem is not string oldName) return;
        var dlg = new RenameWindow("Rename profile", "New name (without .ini):", oldName) { Owner = this, Title = "Rename Profile" };
        if (dlg.ShowDialog() != true) return;
        var newName = Sanitize(dlg.Value);
        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;
        var oldPath = Path.Combine(_profilesFolder, oldName + ".ini");
        var newPath = Path.Combine(_profilesFolder, newName + ".ini");
        if (File.Exists(newPath)) { InfoDialog("Rename Profile", $"A file named “{newName}.ini” already exists."); return; }
        try
        {
            File.Move(oldPath, newPath);            // rename the real file on disk
            MigrateNameDbSection(oldName, newName); // carry the custom profile names across
            _pendingNames.Clear();
            _profilePath = newPath;
            IniFile.Write("General", "ProfilesFile", _profilePath, _settingsIni);
            PopulateLibraryFiles();
            SelectLibraryFile(newName);
            StatusText.Text = $"Renamed file to {newName}.ini";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Rename Profile", ex.Message); }
    }

    private void MigrateNameDbSection(string oldSection, string newSection)
    {
        for (int i = 0; i < 10; i++)
        {
            var v = IniFile.Read(oldSection, i.ToString(), _nameDbPath, "");
            if (v.Length > 0) IniFile.Write(newSection, i.ToString(), v, _nameDbPath);
        }
        IniFile.DeleteSection(oldSection, _nameDbPath);
    }

    private static string Sanitize(string name)
    {
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return name.Trim();
    }

    // ---- custom machines --------------------------------------------------------------
    // The first three MachineBox items are built-ins; any beyond them are user-added and
    // persisted (semicolon-separated) under [General]CustomMachines in settings.ini.
    private const int BuiltInMachineCount = 2;

    private void LoadCustomMachines()
    {
        var csv = IniFile.Read("General", "CustomMachines", _settingsIni, "");
        if (csv.Length == 0) return;
        foreach (var raw in csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!MachineExists(raw)) MachineBox.Items.Add(new ComboBoxItem { Content = raw });
    }

    private static string MachineText(object item) => (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";

    private bool MachineExists(string name)
    {
        foreach (var it in MachineBox.Items)
            if (string.Equals(MachineText(it), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void SelectMachine(string name)
    {
        for (int i = 0; i < MachineBox.Items.Count; i++)
            if (string.Equals(MachineText(MachineBox.Items[i]), name, StringComparison.OrdinalIgnoreCase)) { MachineBox.SelectedIndex = i; return; }
    }

    private void SaveCustomMachines()
    {
        var custom = new List<string>();
        for (int i = BuiltInMachineCount; i < MachineBox.Items.Count; i++)
        {
            var text = MachineText(MachineBox.Items[i]);
            if (!string.IsNullOrWhiteSpace(text)) custom.Add(text);
        }
        IniFile.Write("General", "CustomMachines", string.Join(";", custom), _settingsIni);
    }

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RenameWindow("Add machine", "Machine name:", "") { Owner = this, Title = "Add Machine" };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim().Replace(";", "");
        if (name.Length == 0) return;
        if (MachineExists(name)) { SelectMachine(name); StatusText.Text = $"Machine “{name}” already exists"; return; }
        MachineBox.Items.Add(new ComboBoxItem { Content = name });
        SelectMachine(name);
        SaveCustomMachines();
        StatusText.Text = $"Added machine “{name}”";
    }

    private void RemoveMachine_Click(object sender, RoutedEventArgs e)
    {
        // Only custom machines (those after the built-ins) can be removed — never the two ACHI defaults.
        var custom = new List<string>();
        for (int i = BuiltInMachineCount; i < MachineBox.Items.Count; i++)
        {
            var t = MachineText(MachineBox.Items[i]);
            if (!string.IsNullOrWhiteSpace(t)) custom.Add(t);
        }
        if (custom.Count == 0)
        {
            new ConfirmWindow("Remove Machine",
                "There are no custom machines to remove. The two built-in ACHI machines cannot be removed.",
                "OK", showCancel: false) { Owner = this }.ShowDialog();
            return;
        }

        var dlg = new RemoveMachineWindow(custom) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected is null) return;
        var name = dlg.Selected;

        for (int i = BuiltInMachineCount; i < MachineBox.Items.Count; i++)
        {
            if (string.Equals(MachineText(MachineBox.Items[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                bool wasSelected = MachineBox.SelectedIndex == i;
                MachineBox.Items.RemoveAt(i);
                if (wasSelected) MachineBox.SelectedIndex = 0;   // fall back to a built-in machine
                break;
            }
        }
        SaveCustomMachines();
        StatusText.Text = $"Removed machine “{name}”";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SkipExitConfirm)
        {
            if (HasUnsaved)
            {
                var dlg = new ConfirmWindow("Unsaved changes",
                    "You have unsaved changes to the current profile. Save them before exiting?",
                    "Save & exit", "Don't save") { Owner = this };
                dlg.ShowDialog();
                if (dlg.Result == ConfirmResult.Cancel) { e.Cancel = true; return; }
                if (dlg.Result == ConfirmResult.Primary)
                {
                    StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
                    WriteToIni();
                }
            }
            else
            {
                var dlg = new ConfirmWindow("Exit program",
                    "Are you sure you want to exit the program?", "Exit") { Owner = this };
                dlg.ShowDialog();
                if (dlg.Result != ConfirmResult.Primary) { e.Cancel = true; return; }
            }
        }
        try { _alarmPlayer?.Stop(); } catch { }
        try { _ctrl.Close(); } catch { }
        _ctrl.Dispose();
    }

    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    // =================================================================================
    //  Profiles
    // =================================================================================

    /// <summary>DB section key for the currently selected profiles file.</summary>
    private string NameDbKey => Path.GetFileNameWithoutExtension(_profilePath);

    /// <summary>Display names for the 10 slots: custom name from the local DB, else the .ini's own Name.</summary>
    private List<string> BuildResolvedNames()
    {
        var key = NameDbKey;
        var list = new List<string>(10);
        for (int i = 0; i < 10; i++)
        {
            if (_pendingNames.TryGetValue(i, out var pending)) { list.Add(pending); continue; }
            var custom = IniFile.Read(key, i.ToString(), _nameDbPath, "");
            list.Add(custom.Length > 0 ? custom : IniFile.Read(i.ToString(), "Name", _profilePath, i.ToString()));
        }
        return list;
    }

    private void PopulateProfileNames()
    {
        var names = BuildResolvedNames();
        _loading = true;
        ProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = names;
        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = names.Select((n, i) => $"{i}: {n}").ToList();
        _loading = false;
    }

    /// <summary>Rebuild the name lists after a rename without disturbing the current selection.</summary>
    private void RefreshProfileNames()
    {
        var pIdx = ProfileBox.SelectedIndex;
        var lIdx = LibraryList.SelectedIndex;
        var names = BuildResolvedNames();
        _loading = true;
        try
        {
            ProfileBox.ItemsSource = null; ProfileBox.ItemsSource = names;
            LibraryList.ItemsSource = null; LibraryList.ItemsSource = names.Select((n, i) => $"{i}: {n}").ToList();
            if (pIdx >= 0 && pIdx < names.Count) ProfileBox.SelectedIndex = pIdx;
            if (lIdx >= 0 && lIdx < names.Count) LibraryList.SelectedIndex = lIdx;
        }
        finally { _loading = false; }
    }

    private void AvailableProfiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        int idx = LibraryList.SelectedIndex;
        if (idx < 0 || idx > 9) return;
        var names = BuildResolvedNames();
        string current = idx < names.Count ? names[idx] : idx.ToString();
        var dlg = new RenameWindow(idx, current) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string input = dlg.Value;
        if (string.IsNullOrWhiteSpace(input) || input == current) return;
        _pendingNames[idx] = input;          // persisted to the name DB on Save
        RefreshProfileNames();
        UpdateSaveButton();                  // a pending rename counts as an unsaved change
        StatusText.Text = $"Renamed profile {idx} to “{input}” (unsaved)";
    }

    // Right-click a set to select it, so Copy/Paste act on what was clicked.
    private void LibraryList_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LibraryList, (DependencyObject)e.OriginalSource) is ListBoxItem item)
        { item.IsSelected = true; item.Focus(); }
    }

    private void CopySet_Click(object sender, RoutedEventArgs e)
    {
        int idx = LibraryList.SelectedIndex;
        if (idx < 0 || idx > 9) return;
        // Snapshot the set's data — use the live editor for the loaded set (includes unsaved edits), else the file.
        _copiedSet = idx == _profileIndex ? UiToProfile() : ThermalProfile.Load(idx, _profilePath);
        var names = BuildResolvedNames();
        _copiedSetName = idx < names.Count ? names[idx] : idx.ToString();
        StatusText.Text = $"Copied set {idx} (“{_copiedSetName}”)";
    }

    private void PasteSet_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedSet == null)
        {
            InfoDialog("Paste Set", "Copy a set first — right-click a set and choose Copy Set.");
            return;
        }
        int idx = LibraryList.SelectedIndex;
        if (idx < 0 || idx > 9) return;

        _copiedSet.Save(idx, _profilePath);                                               // recipe + settings -> target set
        IniFile.Write(NameDbKey, idx.ToString(), _copiedSetName + "-copy", _nameDbPath);  // name becomes "<copied>-copy"
        _pendingNames.Remove(idx);
        if (idx == _profileIndex) LoadProfileToUi();                                      // reflect the pasted data in the editor
        RefreshProfileNames();
        StatusText.Text = $"Pasted “{_copiedSetName}-copy” into set {idx}";
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedIndex < 0) return;
        _profileIndex = ProfileBox.SelectedIndex;
        LoadProfileToUi();
    }

    private void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LibraryList.SelectedIndex < 0) return;
        ProfileBox.SelectedIndex = LibraryList.SelectedIndex;
    }

    private void LoadProfileToUi()
    {
        var p = ThermalProfile.Load(_profileIndex, _profilePath);
        _loading = true;
        try
        {
            _steps.Clear();
            for (int i = 0; i < 8; i++)
                _steps.Add(new StepRow { Step = i + 1, Ramp = p.Ramp[i], Target = p.Target[i], Dwell = p.Dwell[i] });
            HoldbackBox.Text = p.Holdback.ToString();
            _critStatic = p.CritTemp; _critOffset = p.CritOffset;
            _alarmStatic = p.SoundAlarm; _alarmOffset = p.AlarmOffset;
            CritModeBox.SelectedIndex = p.CritDynamic ? 1 : 0;
            AlarmModeBox.SelectedIndex = p.SoundAlarmDynamic ? 1 : 0;
            CritBox.Text = (p.CritDynamic ? p.CritOffset : p.CritTemp).ToString();
            AlarmBox.Text = (p.SoundAlarmDynamic ? p.AlarmOffset : p.SoundAlarm).ToString();
            TimeAlarmBox.Text = p.TimeAlarm.ToString();
            AutoBoostBox.Text = p.AutoBottom.ToString();
            B40Box.Text = p.Bottom40.ToString();
            B60Box.Text = p.Bottom60.ToString();
            B80Box.Text = p.Bottom80.ToString();
            B100Box.Text = p.Bottom100.ToString();
            Chk40.IsChecked = p.Check40; Chk60.IsChecked = p.Check60; Chk80.IsChecked = p.Check80; Chk100.IsChecked = p.Check100;
            PowerBottomBox.SelectedIndex = Math.Clamp(p.PowerBottomIndex, 0, 9);
            if (LibraryList.SelectedIndex != _profileIndex) LibraryList.SelectedIndex = _profileIndex;
        }
        finally { _loading = false; }

        RebuildCurveFromUi();
        RedrawChart();
        ClearDirty();   // a freshly loaded profile has no unsaved edits
    }

    private ThermalProfile UiToProfile()
    {
        // Keep the .ini's own Name intact — custom display names live only in the local name DB.
        var p = new ThermalProfile { Name = IniFile.Read(_profileIndex.ToString(), "Name", _profilePath, _profileIndex.ToString()) };
        for (int i = 0; i < 8 && i < _steps.Count; i++)
        {
            p.Ramp[i] = _steps[i].Ramp;
            p.Target[i] = _steps[i].Target;
            p.Dwell[i] = _steps[i].Dwell;
        }
        p.Holdback = Math.Clamp(Dec(HoldbackBox.Text), 0m, 30m);   // upper limit 30; low values are allowed (user was warned)
        p.PowerBottomIndex = Math.Max(0, PowerBottomBox.SelectedIndex);
        // Sync the live box into the active mode's store, then persist both values + the mode.
        SyncSafetyBoxes();
        p.CritTemp = _critStatic; p.CritOffset = _critOffset; p.CritDynamic = CritModeBox.SelectedIndex == 1;
        p.SoundAlarm = _alarmStatic; p.AlarmOffset = _alarmOffset; p.SoundAlarmDynamic = AlarmModeBox.SelectedIndex == 1;
        p.Bottom40 = Dec(B40Box.Text); p.Bottom60 = Dec(B60Box.Text); p.Bottom80 = Dec(B80Box.Text); p.Bottom100 = Dec(B100Box.Text);
        p.TimeAlarm = Dec(TimeAlarmBox.Text); p.AutoBottom = Dec(AutoBoostBox.Text);
        p.Check40 = Chk40.IsChecked == true; p.Check60 = Chk60.IsChecked == true;
        p.Check80 = Chk80.IsChecked == true; p.Check100 = Chk100.IsChecked == true;
        return p;
    }

    // ---- Safety / Alarm: Static vs Dynamic thresholds --------------------------------
    private void CritMode_Changed(object sender, SelectionChangedEventArgs e)
        => SwitchSafetyMode(CritModeBox, CritBox, ref _critStatic, ref _critOffset);
    private void AlarmMode_Changed(object sender, SelectionChangedEventArgs e)
        => SwitchSafetyMode(AlarmModeBox, AlarmBox, ref _alarmStatic, ref _alarmOffset);

    private void SwitchSafetyMode(System.Windows.Controls.ComboBox modeBox, System.Windows.Controls.TextBox valueBox,
                                  ref decimal stat, ref decimal offset)
    {
        if (_loading) return;
        bool dyn = modeBox.SelectedIndex == 1;
        // The box was showing the value for the mode we're leaving — stash it, then show the new mode's value.
        if (dyn) stat = Dec(valueBox.Text); else offset = Dec(valueBox.Text);
        _loading = true;
        try { valueBox.Text = (dyn ? offset : stat).ToString(); } finally { _loading = false; }
        SetDirty();
    }

    /// <summary>Push the live boxes into the active mode's stored value (before saving or evaluating).</summary>
    private void SyncSafetyBoxes()
    {
        if (CritModeBox.SelectedIndex == 1) _critOffset = Dec(CritBox.Text); else _critStatic = Dec(CritBox.Text);
        if (AlarmModeBox.SelectedIndex == 1) _alarmOffset = Dec(AlarmBox.Text); else _alarmStatic = Dec(AlarmBox.Text);
    }

    /// <summary>Highest target in the current set, up to the end sentinel (basis for a dynamic threshold).</summary>
    private int MaxSetTarget()
    {
        decimal max = 0;
        foreach (var s in _steps)
        {
            if (s.Ramp == ThermalProfile.EndSentinel) break;
            if (s.Target > max) max = s.Target;
        }
        return (int)max;
    }

    /// <summary>Target temperature of the step the controller is currently executing (0 when idle).</summary>
    private int StepTarget()
    {
        if (!_running || _curveY.Count == 0) return 0;
        int idx = Math.Clamp(_segment + 1, 0, _curveY.Count - 1);
        return (int)Math.Round(_curveY[idx]);
    }

    /// <summary>Number of active steps in the set (up to the end sentinel) — the total segment count.</summary>
    private int ActiveStepCount()
    {
        int n = 0;
        foreach (var s in _steps)
        {
            if (s.Ramp == ThermalProfile.EndSentinel) break;
            n++;
        }
        return n;
    }

    private int EffectiveCrit()
        => CritModeBox.SelectedIndex == 1 ? MaxSetTarget() + (int)Dec(CritBox.Text) : (int)Dec(CritBox.Text);
    private int EffectiveAlarm()
        => AlarmModeBox.SelectedIndex == 1 ? MaxSetTarget() + (int)Dec(AlarmBox.Text) : (int)Dec(AlarmBox.Text);

    private void WriteToIni()
    {
        TransferBar.Maximum = 43; _post = 0;
        UiToProfile().Save(_profileIndex, _profilePath);
        TransferBar.Value = 0;
        SaveNames();     // persist any pending profile-set renames
        ClearDirty();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        WriteToIni();
        PopulateProfileNames();
        ProfileBox.SelectedIndex = _profileIndex;
        LoadProfileToUi();
        StatusText.Text = "Profile saved";
    }

    // =================================================================================
    //  Connection
    // =================================================================================

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var current = PortBox.SelectedItem as string;
        var names = System.IO.Ports.SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        PortBox.ItemsSource = names;
        var configured = IniFile.Read("Serial", "Port", _settingsIni, "");
        if (current is not null && names.Contains(current)) PortBox.SelectedItem = current;
        else if (configured.Length > 0 && names.Contains(configured)) PortBox.SelectedItem = configured;
        else if (names.Length > 0) PortBox.SelectedIndex = 0;
        StatusText.Text = names.Length == 0 ? "No serial ports detected" : $"Found {names.Length} serial port(s)";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_ctrl.IsOpen) { _ctrl.Close(); AbortTransfer(); _t2.Stop(); SetBadge("COM CLOSED", false); ConnectButton.Content = "Connect"; ConnectionText.Text = "Not connected"; return; }
        OpenPort();
    }

    private void OpenPort_Click(object sender, RoutedEventArgs e) => OpenPort();
    private void ClosePort_Click(object sender, RoutedEventArgs e) { _ctrl.Close(); AbortTransfer(); _t2.Stop(); SetBadge("COM CLOSED", false); ConnectButton.Content = "Connect"; }

    private void OpenPort()
    {
        try
        {
            if (_ctrl.IsOpen) return;
            if (PortBox.SelectedItem is string p && p.Length > 0) IniFile.Write("Serial", "Port", p, _settingsIni);
            var port = IniFile.Read("Serial", "Port", _settingsIni, PortBox.SelectedItem as string ?? "COM1");
            var baud = (int)Math.Round(Conversion.Val(IniFile.Read("Serial", "BaudRate", _settingsIni, "9600")));
            var data = (int)Math.Round(Conversion.Val(IniFile.Read("Serial", "DataBits", _settingsIni, "7")));
            var stop = (int)Math.Round(Conversion.Val(IniFile.Read("Serial", "StopBits", _settingsIni, "1")));
            var dtr = Bool(IniFile.Read("Serial", "DtrEnable", _settingsIni, "True"));
            var rts = Bool(IniFile.Read("Serial", "RtsEnable", _settingsIni, "True"));
            // Diagnostic serial log (default on) — TX/RX bytes land next to the app.
            _ctrl.LogPath = Bool(IniFile.Read("General", "SerialLog", _settingsIni, "True"))
                ? Path.Combine(_appDir, "serial.log") : null;
            _ctrl.Open(port, baud, data, stop, dtr, rts);
            _t2.Start(); _worktime = 0;
            SetBadge("COM OPEN", true);
            ConnectButton.Content = "Disconnect";
            ConnectionText.Text = $"Connected: {port}";
            StatusText.Text = $"Opened {port} @ {baud} {data}{(IniFile.Read("Serial", "Parity", _settingsIni, "EVEN"))}{stop}";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Connection", "Could not open the port:\n" + ex.Message); }
    }

    private void ConfigPort_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SerialSettingsWindow(_settingsIni) { Owner = this };
        if (dlg.ShowDialog() == true) RefreshPorts();
    }

    // ---- Serial Terminal (live TX/RX view) --------------------------------------------
    private SerialTerminalWindow? _terminal;

    private void SerialTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_terminal != null) { _terminal.Activate(); return; }
        _terminal = new SerialTerminalWindow { Owner = this };
        _ctrl.TrafficLogged += OnTraffic;
        _terminal.Closed += (_, _) => { _ctrl.TrafficLogged -= OnTraffic; _terminal = null; };
        _terminal.Show();
        if (!_ctrl.IsOpen) _terminal.AppendLine("(not connected — open the COM port to see traffic)");
    }

    // TrafficLogged is raised on the UI context by the controller, so this is safe to touch the UI.
    private void OnTraffic(string line) => _terminal?.AppendLine(line);

    // =================================================================================
    //  Run control
    // =================================================================================

    private void Start_Click(object sender, RoutedEventArgs e) => StartCycle();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopCycle();
    private void Hold_Click(object sender, RoutedEventArgs e) => ToggleHold();

    private void StartCycle()
    {
        if (!_ctrl.IsOpen) { InfoDialog("BGA Reflow Studio", "Open the COM port first."); return; }
        if (CurrentProfileIsExample())
        {
            var dlg = new ConfirmWindow("Example profile",
                "This profile still contains the built-in example data — it is a placeholder, not tuned for your board or components. Start it anyway?",
                "Start anyway") { Owner = this };
            dlg.ShowDialog();
            if (dlg.Result != ConfirmResult.Primary) return;
        }
        _topTemp = 0; _bottomTemp = 0;
        SetTransferButtons(false);
        _t1X.Clear(); _t1Y.Clear(); _t2X.Clear(); _t2Y.Clear(); _spX.Clear(); _spY.Clear(); _segX.Clear(); _segY.Clear();
        _pid.Reset();
        _flagStart = true;
        ProcessState.Text = "Starting";
    }

    private void StopCycle()
    {
        LoadProfileToUi();
        SetTransferButtons(true);
        _counter7 = 0; _timeAlarmCount = 0; _flagTimeAlarm = false; _criticalShown = false;
        _flagStop = true;
        ProcessState.Text = "Idle";
    }

    /// <summary>
    /// A cycle has concluded (end-of-profile report, or a 0 °C final step). Stop the controller and
    /// bottom output immediately, reset the run state, re-enable Start, then inform the user. Safe to
    /// call from a timer: <see cref="Stops"/> clears <c>_running</c>, so the completion conditions
    /// won't re-fire while the dialog is up.
    /// </summary>
    private void FinishCycle(string title, string message)
    {
        Stops();                      // sends _ctrl.Stop(), _running = false, power bars 0, ProcessState = Idle
        _ctrl.AlarmStop();            // cut the bottom-heater output now (not deferred behind the dialog)
        _flagStop = false;
        _counter7 = 0; _timeAlarmCount = 0; _flagTimeAlarm = false; _criticalShown = false;
        _zeroSpSeconds = 0; _sawNonZeroSp = false;
        SetTransferButtons(true);     // re-enable Start
        LoadProfileToUi();
        InfoDialog(title, message);
    }

    private void ToggleHold()
    {
        if (!_hold) { _flagHold = true; _hold = true; }
        else { _flagStart = true; _hold = false; }
    }

    private void Starts() { _ctrl.Start((Pc410Controller.HeaterMode)HeaterBox.SelectedIndex); _counter4 = 0; }

    private void Stops()
    {
        _ctrl.Stop();
        if (_cycleSeconds > 0) { _lastMin = _min; _lastSek = _sek; _hadCycle = true; }  // remember last cycle time
        _running = false; _al2Stop = true;
        _cycleSeconds = 0; _sek = 0; _min = 0;
        PowerTopBar.Value = 0; PowerBottomBar.Value = 0;
        PowerTopText.Text = "Power 0%"; PowerBottomText.Text = "Power 0%";
        ProcessState.Text = "Idle";
    }

    private void Holds() => _ctrl.Hold();

    // =================================================================================
    //  Hot update
    // =================================================================================

    private void HotUpdate_Click(object sender, RoutedEventArgs e)
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_running) { _flagHotUpdate = true; return; }
        _flagHotUpdate = true;
        WriteToIni();
    }

    private void HotUpdate()
    {
        if (_selStep < 1 || _selStep > 8) { if (_selKind != ParamKind.Holdback) return; }
        switch (_selKind)
        {
            case ParamKind.Ramp: _ctrl.WriteRamp(_selStep, _steps[_selStep - 1].Ramp); break;
            case ParamKind.Target: _ctrl.WriteLevel(_selStep, _steps[_selStep - 1].Target); break;
            case ParamKind.Dwell: _ctrl.WriteDwell(_selStep, _steps[_selStep - 1].Dwell); break;
            case ParamKind.Holdback: _ctrl.WriteHoldback(Dec(HoldbackBox.Text)); break;
        }
    }

    private void StepsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is StepRow row)
        {
            _selStep = row.Step;
            _selKind = e.Column.DisplayIndex switch { 1 => ParamKind.Ramp, 2 => ParamKind.Target, 3 => ParamKind.Dwell, _ => ParamKind.None };
        }
        SetDirty();
        Dispatcher.BeginInvoke(() => { RebuildCurveFromUi(); RedrawChart(); }, DispatcherPriority.Background);
    }

    private void Holdback_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var v = Dec(HoldbackBox.Text);
        if (v > 30m) SetHoldbackText(30m);                  // hard upper limit
        else if (v < 5m)
        {
            // Low holdback is allowed, but warn — the user decides whether to keep it.
            var dlg = new ConfirmWindow("Low holdback",
                "A holdback below 5 can cause stepping issues during a cycle. Use this value anyway?",
                "Use anyway") { Owner = this };
            dlg.ShowDialog();
            if (dlg.Result != ConfirmResult.Primary) SetHoldbackText(5m);
        }
        _selKind = ParamKind.Holdback; _selStep = 0;
    }

    private void SetHoldbackText(decimal v)
    {
        _loading = true;
        try { HoldbackBox.Text = v.ToString(); } finally { _loading = false; }
    }

    // The DataGrid and the plot both swallow the mouse wheel (the plot would zoom). Forward
    // it to the card's ScrollViewer so the whole Thermal Profile panel scrolls instead.
    private void ForwardWheelToParent(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent((DependencyObject)sender) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
    }

    // =================================================================================
    //  Copy / Export
    // =================================================================================

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        // Default name: <profile file>_<profile set>.csv, e.g. Default_Xbox_One.csv
        var fileName = Path.GetFileNameWithoutExtension(_profilePath);
        var setName = ProfileBox.SelectedItem?.ToString() ?? _profileIndex.ToString();
        var suggested = Sanitize($"{fileName}_{setName}".Replace(' ', '_'));
        if (suggested.Length == 0) suggested = $"profile-{_profileIndex}";
        var dlg = new SaveFileDialog { Filter = "CSV profile (*.csv)|*.csv", FileName = suggested + ".csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("Step,Ramp C per s,Target C,Dwell s\r\n");
        foreach (var s in _steps) sb.AppendLine($"{s.Step},{s.Ramp},{s.Target},{s.Dwell}");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusText.Text = "Exported CSV: " + Path.GetFileName(dlg.FileName);
    }

    // =================================================================================
    //  Transfers
    // =================================================================================

    private void SingleFrom_Click(object sender, RoutedEventArgs e) => StartDownload(all: false);
    private void SingleTo_Click(object sender, RoutedEventArgs e) => StartUpload(all: false);
    private void AllFrom_Click(object sender, RoutedEventArgs e) => StartDownload(all: true);
    private void AllTo_Click(object sender, RoutedEventArgs e) => StartUpload(all: true);
    private void Compare_Click(object sender, RoutedEventArgs e) => BeginTransfer(read: true, all: false, compare: true);

    // Downloading (reading) from the controller overwrites the profile sets in the current file.
    // Offer to drop the downloaded data into a brand-new file (named after the machine) instead.
    private void StartDownload(bool all)
    {
        if (!_ctrl.IsOpen) { InfoDialog("BGA Reflow Studio", "Open the COM port first."); return; }

        var currentName = Path.GetFileNameWithoutExtension(_profilePath);
        var what = all ? "all ten profile sets" : "the current profile set";
        var dlg = new ConfirmWindow("Download from Controller",
            $"This downloads {what} from the controller and overwrites the data in the current profile file (“{currentName}.ini”).\n\n" +
            "Create a new profile file for the downloaded data instead?",
            "New file", "Current file") { Owner = this };
        dlg.ShowDialog();
        switch (dlg.Result)
        {
            case ConfirmResult.Primary:        // download into a new (machine-named) file
                if (!CreateProfileFileForDownload()) return;
                BeginTransfer(read: true, all: all, compare: false);
                break;
            case ConfirmResult.Secondary:      // download into the current file
                BeginTransfer(read: true, all: all, compare: false);
                break;
            default:                           // Cancel — do nothing
                break;
        }
    }

    /// <summary>Creates a new machine-named profile file, selects it and makes it current. Returns false on failure.</summary>
    private bool CreateProfileFileForDownload()
    {
        try
        {
            var baseName = Sanitize(MachineText(MachineBox.SelectedItem));
            if (baseName.Length == 0) baseName = "Downloaded";
            var name = baseName;
            var path = Path.Combine(_profilesFolder, name + ".ini");
            for (int n = 2; File.Exists(path); n++) { name = $"{baseName} ({n})"; path = Path.Combine(_profilesFolder, name + ".ini"); }
            SeedDefaultProfiles(path);
            PopulateLibraryFiles();
            SelectLibraryFile(name);           // sets _profilePath and loads the new file
            StatusText.Text = $"Created {name}.ini for the download";
            return true;
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Download from Controller", ex.Message); return false; }
    }

    // Uploading overwrites the profile(s) stored on the controller — confirm before sending.
    private void StartUpload(bool all)
    {
        if (!_ctrl.IsOpen) { InfoDialog("BGA Reflow Studio", "Open the COM port first."); return; }
        var what = all ? "all ten profile sets" : "the current profile set";
        var dlg = new ConfirmWindow("Upload to Controller",
            $"This uploads {what} to the controller, overwriting the profile data stored on the machine.\n\nContinue?",
            "Upload") { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result == ConfirmResult.Primary) BeginTransfer(read: false, all: all, compare: false);
    }

    private void BeginTransfer(bool read, bool all, bool compare)
    {
        if (!_ctrl.IsOpen) { InfoDialog("BGA Reflow Studio", "Open the COM port first."); return; }
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _t2.Stop();
        TransferBar.Value = 0; TransferBar.Maximum = 27; _post = 0; _xferProfile = 0; _xferStep = 0;
        _compare = compare;
        _transferring = true;
        StatusText.Text = compare ? "Comparing with controller…" : read ? "Downloading from controller…" : "Uploading to controller…";
        if (all) { if (read) _flall = true; else _flagall = true; }
        else { if (read) _flaSingle = true; else _flagaSingle = true; }
        _t3.Start();
    }

    /// <summary>Stops an in-progress transfer (e.g. when the port closes) and clears its state.</summary>
    private void AbortTransfer()
    {
        if (!_transferring) return;
        _t3.Stop();
        _transferring = false; _compare = false; _flagCompare = false;
        _flar = _flal = _flad = _flahb = _flall = _flaSingle = false;
        _flagar = _flagal = _flagad = _flagahb = _flagall = _flagaSingle = false;
        _flaFinishRead = false; _awaiting = null; _awaitTicks = 0; _awaitRetries = 0;
        TransferBar.Value = 0;
    }

    private void PollAwait(string name)
    {
        _awaiting = name; _awaitTicks = 0; _awaitRetries = 0;
        _ctrl.Poll(name);
    }

    private void Timer3_Tick(object? sender, EventArgs e)
    {
        if (!_flagall && !_flagaSingle && !_flagar && !_flagal && !_flagad && !_flagahb
            && !_flall && !_flaSingle && !_flar && !_flal && !_flad && !_flahb && !_flaFinishRead)
        {
            _t3.Stop();
            // The whole transfer has finished — give feedback now (Compare shows its own dialog instead).
            if (_transferring) { _transferring = false; if (!_compare) StatusText.Text = "Transfer OK"; }
            if (_ctrl.IsOpen) _t2.Start();
            return;
        }

        // Pace reads to their replies: never send the next poll until the current one has answered,
        // otherwise a slow or stray reply collides with the next poll and that value is silently lost.
        if (_awaiting != null)
        {
            if (++_awaitTicks < AwaitMaxTicks) return;        // reply not in yet — keep waiting
            _awaitTicks = 0;
            if (_awaitRetries < AwaitMaxRetries) { _awaitRetries++; _ctrl.Poll(_awaiting); return; }  // re-poll the same value
            _awaiting = null;                                  // give up on this one and continue
        }

        // write path
        // Holdback 0 means "off" and the controller NAKs a write of 0, so only send a real value.
        // This is the terminal step of the write path — clear the progress bar when done (as the read path does).
        if (_flagahb) { var hb = Dec(HoldbackBox.Text); if (hb != 0) _ctrl.WriteHoldback(hb); _flagahb = false; _post = 0; TransferBar.Value = 0; }
        else if (_flagad) { _xferStep++; _ctrl.WriteDwell(_xferStep, _steps[_xferStep - 1].Dwell); _post++; TransferBar.Value = ClampBar(_post); if (_xferStep == 8) { _xferStep = 0; _flagad = false; _flagahb = true; } }
        else if (_flagal) { _xferStep++; _ctrl.WriteLevel(_xferStep, _steps[_xferStep - 1].Target); _post++; TransferBar.Value = ClampBar(_post); if (_xferStep == 8) { _xferStep = 0; _flagal = false; _flagad = true; } }
        else if (_flagar) { _xferStep++; _ctrl.WriteRamp(_xferStep, _steps[_xferStep - 1].Ramp); _post++; TransferBar.Value = ClampBar(_post); if (_xferStep == 8) { _xferStep = 0; _flagar = false; _flagal = true; } }
        else if (_flagall) { ProfileBox.SelectedIndex = _xferProfile; _ctrl.SelectChannel(_profileIndex); _flagar = true; _xferProfile++; _post = 0; if (_xferProfile == 10) { _xferProfile = 0; _flagall = false; } }
        else if (_flagaSingle) { _ctrl.SelectChannel(_profileIndex); _flagaSingle = false; _flagar = true; }

        // read path — each poll waits for its reply (via _awaiting) before the next is sent
        if (_flahb) { PollAwait("Hb"); _flahb = false; _flaFinishRead = true; }
        else if (_flaFinishRead) { _flaFinishRead = false; if (_compare) _flagCompare = true; else WriteToIni(); _post = 0; TransferBar.Value = 0; }
        else if (_flad) { _xferStep++; _post++; TransferBar.Value = ClampBar(_post); PollAwait("t" + _xferStep); if (_xferStep == 8) { _flad = false; _flahb = true; _xferStep = 0; _post = 0; TransferBar.Value = 0; } }
        else if (_flal) { _xferStep++; _post++; TransferBar.Value = ClampBar(_post); PollAwait("l" + _xferStep); if (_xferStep == 8) { _flal = false; _flad = true; _xferStep = 0; } }
        else if (_flar) { TransferBar.Maximum = 25; _xferStep++; _post++; TransferBar.Value = ClampBar(_post); PollAwait("r" + _xferStep); if (_xferStep == 8) { _flar = false; _flal = true; _xferStep = 0; } }
        else if (_flaSingle) { _ctrl.SelectChannel(_profileIndex); _flar = true; _flaSingle = false; }
        else if (_flall) { ProfileBox.SelectedIndex = _xferProfile; _ctrl.SelectChannel(_profileIndex); _xferStep = 0; _flar = true; _xferProfile++; _post = 0; if (_xferProfile == 10) { _xferProfile = 0; _flall = false; } }
    }

    // =================================================================================
    //  Poll / command timer
    // =================================================================================

    private void Timer2_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_flagHotUpdate) { HotUpdate(); _flagHotUpdate = false; return; }

            if (!_running && _ctrl.IsOpen && !_flagStop && !_flagStart && !_flagGoStart && !_flagHold)
            {
                // Idle polling: keep the run-status/alarm checks (watchdog), and also refresh the
                // top/bottom temperatures and set point so the tiles show live values before Start.
                _counter6++;
                switch (_counter6)
                {
                    case 3: _ctrl.PollTop(); break;
                    case 5: _ctrl.PollSetPoint(); break;
                    case 7: _ctrl.PollBottom(); break;
                    case 10: _ctrl.PollAlarm2(); break;
                    case 13: _ctrl.PollTop(); break;
                    case 16: _ctrl.PollBottom(); break;
                    case 20: _ctrl.PollRunStatus(); _counter6 = 0; break;
                }
            }

            if (_running && !_flagStop && !_flagHold && !_flagStart && !_flagGoStart)
            {
                _counter3++;
                switch (_counter3)
                {
                    case 2: _ctrl.PollTop(); break;
                    case 4: _ctrl.PollBottom(); break;
                    case 6: _ctrl.PollPowerTop(); break;
                    case 8: _counter11++; if (_counter11 == 1) _ctrl.PollSegment(); else { _ctrl.PollSetPoint(); _counter11 = 0; } break;
                    case 10: _ctrl.PollRunStatus(); break;
                    case 11: BottomPwmTick(); _counter3 = 0; break;
                }
            }

            if (_al2Stop) { _ctrl.AlarmStop(); _al2Stop = false; }
            if (_flagHold) { _flagHold = false; Holds(); }
            if (_flagStop) { Stops(); _flagStop = false; }
            if (_flagGoStart) { Starts(); _running = true; ProcessState.Text = "Running"; _flagGoStart = false; }
            if (_flagStart) StartHandshake();

            UpdateAlarmSound();
        }
        catch (Exception ex) { LogError(ex); }
    }

    private void StartHandshake()
    {
        _counter9++;
        switch (_counter9)
        {
            case 1: _ctrl.SelectChannel(_profileIndex); break;
            case 2: _ctrl.PollChannel(); break;
            case 3: if (_tryDoChange) { _flagStart = false; _flagGoStart = true; _tryDoChange = false; _counter9 = 0; } break;
        }
    }

    private void BottomPwmTick()
    {
        if (HeaterBox.SelectedIndex != 0 && HeaterBox.SelectedIndex != 2) return;
        // No bottom sensor (reads > 1000 °C / "No Data"): hold the bottom output off.
        if (_bottomTemp > 1000) { PowerBottomBar.Value = 0; PowerBottomText.Text = "Power 0%"; _ctrl.AlarmStop(); return; }
        int idx = Math.Max(0, PowerBottomBox.SelectedIndex);
        int percent = (idx + 1) * 10;
        PowerBottomBar.Value = Math.Clamp(percent, 0, 100);
        PowerBottomText.Text = $"Power {percent}%";
        if (idx >= 9) { _ctrl.AlarmStart(); return; }
        _counter4++;
        if (_counter4 == 1) _ctrl.AlarmStart();
        else if (_counter4 == idx + 2) _ctrl.AlarmStop();
        if (_counter4 == 11) _counter4 = 0;
    }

    // =================================================================================
    //  Auto PID
    // =================================================================================

    private void Timer4_Tick(object? sender, EventArgs e)
    {
        if (!(_running && AutoCheck.IsChecked == true)) return;
        float cv = _pid.Step(_setPoint, Dec(AutoBoostBox.Text), _bottomTemp, _topTemp, _bottomContinue);
        int idx = PidRegulator.CvToPowerIndex(cv);
        if (PowerBottomBox.SelectedIndex != idx) PowerBottomBox.SelectedIndex = idx;
    }

    private void Auto_Changed(object sender, RoutedEventArgs e)
    {
        bool auto = AutoCheck.IsChecked == true;
        Chk40.IsEnabled = Chk60.IsEnabled = Chk80.IsEnabled = Chk100.IsEnabled = !auto;
        B40Box.IsEnabled = B60Box.IsEnabled = B80Box.IsEnabled = B100Box.IsEnabled = !auto;
        HeaterBox.IsEnabled = !auto;
        if (auto) { HeaterBox.SelectedIndex = 0; _t4.Start(); } else _t4.Stop();
    }

    // =================================================================================
    //  UI / watchdog timer
    // =================================================================================

    private void Timer1_Tick(object? sender, EventArgs e)
    {
        if (_flagCompare)
        {
            _flagCompare = false;
            if (_errorCounter is >= 1 and <= 30) CompareMessageNok(); else CompareMessageOk();
            _compare = false; _worktime = 0; _errorCounter = 0; _nameError = ""; _criticalShown = false;
        }

        if (_ctrl.IsOpen && _counter7 < 5 && _topTemp < EffectiveCrit())
        {
            _worktime++;
            if (_worktime == 10)
            {
                _worktime = 0;
                _ctrl.Close(); AbortTransfer(); _t2.Stop();
                SetBadge("COM CLOSED", false); ConnectButton.Content = "Connect"; ConnectionText.Text = "Not connected";
                InfoDialog("Machine Status", "Communication error or machine not connected.\n\nCheck the settings and re-open the COM port.");
            }
        }

        if (_alm)
        {
            _timeAlarmCount++;
            if (_timeAlarmCount == (int)Dec(TimeAlarmBox.Text)) { _flagTimeAlarm = true; _timeAlarmCount = 0; }
        }

        if (_running)
        {
            _cycleSeconds++; _sek++;
            if (_sek > 59) { _min++; _sek = 0; }
            if (Show1.IsChecked == true) { _t1X.Add(_cycleSeconds); _t1Y.Add(_topTemp); }
            if (Show2.IsChecked == true) { _t2X.Add(_cycleSeconds); _t2Y.Add(_bottomTemp); }
            if (!(_cycleSeconds > 10 && _setPoint < 2)) { _spX.Add(_cycleSeconds); _spY.Add(_setPoint); }
        }

        CycleText.Text = $"Cycle {_min:00}:{_sek:00}";
        ElapsedText.Text = _running ? $"Running {_min:00}:{_sek:00}"
            : (_hadCycle ? $"Last cycle {_lastMin:00}:{_lastSek:00}" : "Ready for profile");
        TopTemp.Text = $"{_topTemp} °C";
        // A reading above 1000 °C means the bottom sensor isn't wired on this machine.
        if (_bottomTemp > 1000)
        {
            BottomTemp.Text = "No Data";
            PowerBottomBar.Value = 0; PowerBottomText.Text = "Power 0%";
        }
        else BottomTemp.Text = $"{_bottomTemp} °C";
        // Moving set point (SV) / the active step's target temperature.
        SetPointText.Text = $"{_setPoint} °C / {StepTarget()} °C";
        // The controller numbers ramp + soak phases (2 per step), so map the phase back to a step 1..N.
        int totalSteps = ActiveStepCount();
        int curStep = _running && totalSteps > 0 ? Math.Min(_segment / 2 + 1, totalSteps) : 0;
        SegmentText.Text = $"Segment {curStep} / {totalSteps}";

        // Subtle beep each time the segment advances (optional).
        if (!_running) _lastBeepStep = 0;
        else if (curStep > _lastBeepStep)
        {
            if (SegBeep.IsChecked == true)
                // Console.Beep sounds a real tone through the audio device (not the "Default Beep"
                // sound-scheme entry, which is often silent). Run it off-thread — it blocks for the duration.
                System.Threading.Tasks.Task.Run(() => { try { Console.Beep(1000, 120); } catch { /* ignore */ } });
            _lastBeepStep = curStep;
        }

        // The set concludes when the controller's Set Point (SV) returns to 0 °C, but the machine keeps
        // running — so stop it ourselves ~5 s later. Require the Set Point to have been > 0 first, so the
        // brief 0 at the very start of a run doesn't trip it.
        if (!_running) { _zeroSpSeconds = 0; _sawNonZeroSp = false; }
        else
        {
            if (_setPoint > 0) _sawNonZeroSp = true;
            if (_sawNonZeroSp && _setPoint == 0)
            {
                _zeroSpSeconds++;
                if (_zeroSpSeconds >= 5)
                    FinishCycle("Profile Complete", "The set point returned to 0 °C — the set has concluded and the cycle has been stopped.");
            }
            else _zeroSpSeconds = 0;
        }

        if (_ctrl.IsOpen && _worktime < 3)
        {
            SetBadge("COM OPEN", true);
            _flagWorking = !_flagWorking;
        }
        else if (!_ctrl.IsOpen) SetBadge("COM CLOSED", false);

        RedrawChart();
    }

    private void SetBadge(string text, bool open)
    {
        StatusBadgeText.Text = text;
        StatusBadgeText.Foreground = new SolidColorBrush(open ? Color.FromRgb(0x1F, 0x8A, 0x4C) : Color.FromRgb(0x92, 0x5E, 0x08));
        StatusBadge.Background = new SolidColorBrush(open ? Color.FromRgb(0xE4, 0xF7, 0xEC) : Color.FromRgb(0xFF, 0xF4, 0xE5));
    }

    // =================================================================================
    //  Response handling
    // =================================================================================

    private void OnResponse(string symbol, string value, float dec)
    {
        _worktime = 0;
        if (_awaiting != null && symbol == _awaiting) _awaiting = null;   // the read poll we were waiting for has replied
        switch (symbol)
        {
            case "r1": case "r2": case "r3": case "r4": case "r5": case "r6": case "r7": case "r8":
                RouteStep(symbol[1] - '1', StepField.Ramp, value, dec, "R" + symbol[1]); break;
            case "l1": case "l2": case "l3": case "l4": case "l5": case "l6": case "l7": case "l8":
                RouteStep(symbol[1] - '1', StepField.Target, value, dec, "L" + symbol[1]); break;
            case "t1": case "t2": case "t3": case "t4": case "t5": case "t6": case "t7": case "t8":
                RouteStep(symbol[1] - '1', StepField.Dwell, value, dec, "D" + symbol[1]); break;
            case "Hb": RouteHoldback(dec); break;

            case "PV": OnProcessValue((int)Math.Round(dec)); break;
            case "P2": _bottomTemp = (int)Math.Round(dec); break;
            case "OP":
                PowerTopText.Text = $"Power {value.Trim()}%";
                PowerTopBar.Value = Math.Clamp((int)Math.Round((double)Conversion.Val(value)), 0, 100);
                break;
            case "SP": _setPoint = (int)Math.Round(dec); break;
            case "SE":
                if (_running)
                {
                    _segment = (int)Math.Round(Conversion.Val(value.Length >= 5 ? value[3..5] : value));
                    if (_segment + 1 < _curveX.Count) { _segX.Add(_curveX[_segment + 1]); _segY.Add(_curveY[_segment + 1]); }
                }
                break;
            case "A2": _alarm2 = (int)Math.Round(dec); if (!_running && _alarm2 < 500) Stops(); break;
            case "#3": OnRunStatus((int)Conversion.Val(value.Length >= 4 ? value[3..4] : "0")); break;
            case "ch": if ((float)_profileIndex == dec) _tryDoChange = true; break;
        }
    }

    private enum StepField { Ramp, Target, Dwell }

    private void RouteStep(int idx, StepField field, string value, float dec, string name)
    {
        if (idx < 0 || idx >= _steps.Count) return;
        // Ramp carries a decimal (comma) — parse the string in the app culture. Target/Dwell are read
        // leniently from the controller's numeric value (Conversion.Val), matching the original: the
        // fixed-width reply field can carry trailing characters that strict decimal parsing would reject.
        decimal v;
        try { v = field == StepField.Ramp ? Conversions.ToDecimal(value) : new decimal(dec); }
        catch { return; }
        decimal current = field switch { StepField.Ramp => _steps[idx].Ramp, StepField.Target => _steps[idx].Target, _ => _steps[idx].Dwell };
        if (_compare && (double)current != (double)v) { _errorCounter++; _nameError += name + ", "; return; }

        _loading = true;
        try
        {
            switch (field)
            {
                case StepField.Ramp: _steps[idx].Ramp = v; break;
                case StepField.Target: _steps[idx].Target = v; break;
                case StepField.Dwell: _steps[idx].Dwell = v; break;
            }
        }
        finally { _loading = false; }
    }

    private void RouteHoldback(float dec)
    {
        decimal v; try { v = new decimal(dec); } catch { return; }   // lenient, matches the original read-back
        if (_compare && (double)Dec(HoldbackBox.Text) != (double)v) { _errorCounter++; _nameError += "HB, "; return; }
        _loading = true; try { HoldbackBox.Text = v.ToString(); } finally { _loading = false; }
    }

    private void OnProcessValue(int t)
    {
        _topTemp = t;
        // At idle we poll PV only to display the live temperature; the critical-temp auto-stop and
        // the staged bottom-power logic are run-time behaviours (the latter would dirty the profile).
        if (!_running) return;
        if (t >= EffectiveCrit())
        {
            _flagStop = true;
            if (!_criticalShown)
            {
                _criticalShown = true;
                InfoDialog("Critical Temperature", $"Critical temperature reached — the process is being stopped.\n\n{t} °C");
            }
        }
        if (AutoCheck.IsChecked == true) return;

        if (t <= (int)Dec(B40Box.Text)) { if (Chk40.IsChecked == true) PowerBottomBox.SelectedIndex = 3; }
        else if (t <= (int)Dec(B60Box.Text)) { if (Chk60.IsChecked == true) PowerBottomBox.SelectedIndex = 5; }
        else if (t <= (int)Dec(B80Box.Text)) { if (Chk80.IsChecked == true) PowerBottomBox.SelectedIndex = 7; }
        else if (t <= (int)Dec(B100Box.Text) && Chk100.IsChecked == true) PowerBottomBox.SelectedIndex = 9;
    }

    private void OnRunStatus(int ifRunning)
    {
        _ifRunning = ifRunning;
        if (ifRunning == 0 && _running && HeaterBox.SelectedIndex != 2)
        {
            _counter7++;
            if (_counter7 == 5)
                FinishCycle("Profile Complete", "The profile has finished and the cycle has been stopped.");
        }
        if (ifRunning == 2) ProcessState.Text = "Hold";
        else if (_running) ProcessState.Text = "Running";
        if (ifRunning is 0 or 1 or 2) _worktime = 0;
        if (!_running && ifRunning == 1) Stops();
    }

    // =================================================================================
    //  Alarm sound
    // =================================================================================

    private void UpdateAlarmSound()
    {
        if (_running && !_alm && EffectiveAlarm() <= _topTemp && !_flagTimeAlarm)
        {
            _alm = true;
            try { _alarmPlayer?.PlayLooping(); } catch { SystemSounds.Exclamation.Play(); }
        }
        if ((!_running && _alm) || _flagTimeAlarm)
        {
            _alm = false;
            try { _alarmPlayer?.Stop(); } catch { }
        }
    }

    private void LoadAlarmSound()
    {
        var p = Path.Combine(_appDir, "alarm.wav");
        if (File.Exists(p)) _alarmPlayer = new SoundPlayer(p);
    }

    // =================================================================================
    //  Charting
    // =================================================================================

    private void RebuildCurveFromUi()
    {
        var result = ReflowCurve.Build(UiToProfile());
        _bottomContinue = result.BottomContinue;
        _curveX.Clear(); _curveY.Clear();
        foreach (var (x, y) in result.Points) { _curveX.Add(x); _curveY.Add(y); }
    }

    private void RedrawChart()
    {
        DrawInto(Plot.Plot);
        Plot.Refresh();
        // Mirror onto the live pop-out window, if one is open.
        if (_popoutPlot != null)
        {
            DrawInto(_popoutPlot.Plot);
            _popoutPlot.Refresh();
        }
    }

    private void DrawInto(ScottPlot.Plot plot)
    {
        plot.Clear();
        if (_curveX.Count > 0) AddScatter(plot, _curveX, _curveY, ScottPlot.Colors.Red, "Set Temperature");
        if (Show1.IsChecked == true && _t1X.Count > 0) AddScatter(plot, _t1X, _t1Y, ScottPlot.Colors.Green, "Temperature.1");
        if (Show2.IsChecked == true && _t2X.Count > 0) AddScatter(plot, _t2X, _t2Y, ScottPlot.Colors.Blue, "Temperature.2");
        if (_spX.Count > 0) AddScatter(plot, _spX, _spY, ScottPlot.Colors.Orange, "Set Point");
        if (_segX.Count > 0) AddScatter(plot, _segX, _segY, ScottPlot.Color.FromHex("#6FB3E0"), "Current Segment");
        plot.Axes.AutoScale();
    }

    // ---- live pop-out chart window ----------------------------------------------------
    private ScottPlot.WPF.WpfPlot? _popoutPlot;
    private Window? _popoutWindow;

    private void PopoutChart_Click(object sender, RoutedEventArgs e)
    {
        if (_popoutWindow != null) { _popoutWindow.Activate(); return; }
        var wp = new ScottPlot.WPF.WpfPlot();
        wp.Plot.Axes.Bottom.Label.Text = "Time [s]";
        wp.Plot.Axes.Left.Label.Text = "Temperature [°C]";
        wp.Plot.HideLegend();
        var win = new Window
        {
            Title = "Thermal Profile — live",
            Width = 900, Height = 600,
            Content = wp,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        try { win.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/icon.ico")); } catch { /* ignore */ }
        win.Closed += (_, _) => { _popoutPlot = null; _popoutWindow = null; };
        _popoutPlot = wp; _popoutWindow = win;
        win.Show();
        RedrawChart();   // paint it immediately with the current data
    }

    private static void AddScatter(ScottPlot.Plot plot, List<double> xs, List<double> ys, ScottPlot.Color color, string label)
    {
        var s = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        s.Color = color; s.LegendText = label; s.MarkerSize = 4;
    }

    // =================================================================================
    //  Screenshot (menu + self-test)
    // =================================================================================

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"ACHI-Reflow-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dlg.ShowDialog() != true) return;
        try { SaveWindowImage(dlg.FileName); StatusText.Text = "Screenshot saved: " + Path.GetFileName(dlg.FileName); }
        catch (Exception ex) { LogError(ex); InfoDialog("Screenshot", "Could not save the screenshot:\n" + ex.Message); }
    }

    /// <summary>Render the window to a PNG (used by the menu and the hidden self-test).</summary>
    internal void SaveWindowImage(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // =================================================================================
    //  Menu / misc
    // =================================================================================

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ManualsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        ManualsMenu.Items.Clear();
        var dir = Path.Combine(_appDir, "manuals");
        try { Directory.CreateDirectory(dir); } catch { }

        var pdfs = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        if (pdfs.Count == 0)
        {
            ManualsMenu.Items.Add(new MenuItem { Header = "(No manuals found)", IsEnabled = false });
        }
        else
        {
            foreach (var f in pdfs)
            {
                var path = f;
                var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(f) };
                item.Click += (_, _) => OpenWithShell(path);
                ManualsMenu.Items.Add(item);
            }
        }

        ManualsMenu.Items.Add(new Separator());
        var openFolder = new MenuItem { Header = "Open Folder" };
        openFolder.Click += (_, _) => OpenWithShell(dir);
        ManualsMenu.Items.Add(openFolder);
    }

    private void OpenWithShell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { LogError(ex); InfoDialog("Manuals", "Could not open:\n" + path + "\n\n" + ex.Message); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Support_Click(object sender, RoutedEventArgs e) => new SupportWindow { Owner = this }.ShowDialog();

    /// <summary>Open Help > Manuals (self-test visual only).</summary>
    internal void OpenManualsMenuForTest()
    {
        Activate();
        if (ManualsMenu.Parent is MenuItem help) help.IsSubmenuOpen = true;
        ManualsMenu.IsSubmenuOpen = true;   // fires SubmenuOpened -> populates the list
    }

    // The "local database" is the custom profile-set names kept in profile-names.ini (NOT the profiles
    // themselves — those are the .ini files in \profiles). Export/import moves just those names.
    private void ExportLocalDb_Click(object sender, RoutedEventArgs e)
    {
        var note = new ConfirmWindow("Export Local Database",
            "This exports only the local database of your custom profile-set names.\n\n" +
            "It is NOT a profile export — the profile recipes are stored separately in the .ini files in the \\profiles folder.\n\nContinue?",
            "Export") { Owner = this };
        note.ShowDialog();
        if (note.Result != ConfirmResult.Primary) return;

        var sfd = new SaveFileDialog
        {
            Filter = "ACHI local database (*.ini)|*.ini",
            FileName = "BGA Reflow Studio - Local Database.ini",
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            if (File.Exists(_nameDbPath)) File.Copy(_nameDbPath, sfd.FileName, overwrite: true);
            else File.WriteAllText(sfd.FileName, "");   // nothing stored yet -> empty database
            StatusText.Text = "Exported local database: " + Path.GetFileName(sfd.FileName);
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Export Local Database", "Could not export:\n" + ex.Message); }
    }

    private void ImportLocalDb_Click(object sender, RoutedEventArgs e)
    {
        var note = new ConfirmWindow("Import Local Database",
            "This imports a local database of custom profile-set names, replacing your current custom names.\n\n" +
            "It is NOT a profile import — the profile recipes are stored separately in the .ini files in the \\profiles folder.\n\nContinue?",
            "Import") { Owner = this };
        note.ShowDialog();
        if (note.Result != ConfirmResult.Primary) return;

        var ofd = new OpenFileDialog
        {
            Filter = "ACHI local database (*.ini)|*.ini|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_nameDbPath)!);
            File.Copy(ofd.FileName, _nameDbPath, overwrite: true);
            _pendingNames.Clear();
            RefreshProfileNames();      // reflect the imported custom names
            UpdateSaveButton();
            StatusText.Text = "Imported local database: " + Path.GetFileName(ofd.FileName);
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Import Local Database", "Could not import:\n" + ex.Message); }
    }

    /// <summary>Themed single-button info dialog.</summary>
    private void InfoDialog(string title, string message)
        => new ConfirmWindow(title, message, "OK", showCancel: false) { Owner = this }.ShowDialog();

    private void CleanDb_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_nameDbPath)!;
        var dlg = new ConfirmWindow("Clean Database",
            "This removes all locally stored application data in:\n" + dir + "\n\n" +
            "Your custom profile names will be permanently deleted. The profile .ini files themselves are not affected.\n\nContinue?",
            "Clean") { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result != ConfirmResult.Primary) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            _pendingNames.Clear();
            RefreshProfileNames();   // custom names are gone -> revert to the .ini names
            UpdateSaveButton();
            StatusText.Text = "Local database cleaned";
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Clean Database", ex.Message); }
    }
    private void SegBeep_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        IniFile.Write("General", "SegmentBeep", (SegBeep.IsChecked == true).ToString(), _settingsIni);
    }

    private void ShowSeries_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        IniFile.Write("General", "ShowTemp1", (Show1.IsChecked == true).ToString(), _settingsIni);
        IniFile.Write("General", "ShowTemp2", (Show2.IsChecked == true).ToString(), _settingsIni);
        RedrawChart();
    }

    // "Load Profile" imports an external .ini into the \profiles library, then selects it.
    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "Ini files (*.ini)|*.ini", InitialDirectory = _profilesFolder, RestoreDirectory = true };
        if (ofd.ShowDialog() != true) return;
        try
        {
            var fileName = Path.GetFileName(ofd.FileName);
            var dest = Path.Combine(_profilesFolder, fileName);
            if (!PathsEqual(Path.GetDirectoryName(ofd.FileName), _profilesFolder))
                File.Copy(ofd.FileName, dest, overwrite: true);
            PopulateLibraryFiles();
            SelectLibraryFile(Path.GetFileNameWithoutExtension(fileName));
            StatusText.Text = "Added to library: " + fileName;
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Load Profile", ex.Message); }
    }

    // "Save Profile As" duplicates the current file (with pending edits) under a new name.
    private void SaveProfileAs_Click(object sender, RoutedEventArgs e)
    {
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        WriteToIni(); // persist current edits into the current file first
        var sfd = new SaveFileDialog { Filter = "Ini files (*.ini)|*.ini", InitialDirectory = _profilesFolder, FileName = Path.GetFileName(_profilePath) };
        if (sfd.ShowDialog() != true) return;
        try
        {
            if (!PathsEqual(sfd.FileName, _profilePath)) File.Copy(_profilePath, sfd.FileName, overwrite: true);
            if (PathsEqual(Path.GetDirectoryName(sfd.FileName), _profilesFolder))
            {
                PopulateLibraryFiles();
                SelectLibraryFile(Path.GetFileNameWithoutExtension(sfd.FileName));
            }
            StatusText.Text = "Saved profiles to " + Path.GetFileName(sfd.FileName);
        }
        catch (Exception ex) { LogError(ex); InfoDialog("Save Profile As", ex.Message); }
    }

    private void SelectLibraryFile(string nameWithoutExt)
    {
        if (LibraryFilesList.ItemsSource is List<string> names)
        {
            var i = names.FindIndex(n => string.Equals(n, nameWithoutExt, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (LibraryFilesList.SelectedIndex == i) LibraryFiles_SelectionChanged(LibraryFilesList, null!);
                else LibraryFilesList.SelectedIndex = i;
            }
        }
    }

    private static bool PathsEqual(string? a, string? b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a ?? ""), Path.TrimEndingDirectorySeparator(b ?? ""), StringComparison.OrdinalIgnoreCase);

    private void CompareMessageOk()
    {
        var dlg = new ConfirmWindow("Compare & Start",
            "All parameters match the controller. Start the process?", "Start") { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result == ConfirmResult.Primary) StartCycle();
    }

    private void CompareMessageNok()
    {
        var names = string.IsNullOrWhiteSpace(_nameError) ? "" : $"\n\nDiffering: {_nameError.TrimEnd(',', ' ')}";
        var dlg = new ConfirmWindow("Compare & Start",
            $"{_errorCounter} parameter(s) differ from the PC profile.{names}\n\n" +
            "Upload the PC values to the controller first, or start without updating?",
            "Upload & start", "Start anyway") { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result == ConfirmResult.Primary) { BeginTransfer(false, false, false); StartCycle(); }
        else if (dlg.Result == ConfirmResult.Secondary) StartCycle();
        // Cancel → do nothing
    }

    private void SetTransferButtons(bool enabled)
    {
        StartButton.IsEnabled = enabled;
    }

    private void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(_appDir, "achi-reflow.log"), $"{DateTime.Now:u} {ex.Message}\r\n"); } catch { }
    }

    // ---- helpers ----
    private int ClampBar(int v) => Math.Clamp(v, 0, (int)Math.Max(1, TransferBar.Maximum));
    private static decimal Dec(string s) { try { return s.Trim().Length == 0 ? 0m : Conversions.ToDecimal(s); } catch { return 0m; } }
    private static int ClampIndex(string s, int count) { int i = (int)Math.Round(Conversion.Val(s)); return count <= 0 ? 0 : Math.Clamp(i, 0, count - 1); }
    private static bool Bool(string s) => s.Equals("True", StringComparison.OrdinalIgnoreCase) || s == "1";

    private static void SeedDefaultProfiles(string path)
    {
        for (int i = 0; i < 10; i++) ExampleProfile(i).Save(i, path);
    }

    /// <summary>The built-in placeholder ("example") recipe used to seed new profile files.</summary>
    private static ThermalProfile ExampleProfile(int i)
    {
        var p = new ThermalProfile
        {
            Name = $"Profile {i}", Holdback = 15, TimeAlarm = 3,
            CritTemp = 230, CritOffset = 15, SoundAlarm = 225, AlarmOffset = 10,
        };
        p.Ramp[0] = 0.45m; p.Target[0] = 150; p.Dwell[0] = 90;
        p.Ramp[1] = 1.0m; p.Target[1] = 183; p.Dwell[1] = 60;
        p.Ramp[2] = 1.0m; p.Target[2] = 217; p.Dwell[2] = 45;
        p.Ramp[3] = ThermalProfile.EndSentinel;
        p.Bottom40 = 30; p.Bottom60 = 70; p.Bottom80 = 120; p.Bottom100 = 160;
        return p;
    }

    /// <summary>True when the loaded recipe still matches the built-in example (unmodified placeholder data).</summary>
    private bool CurrentProfileIsExample()
    {
        var ex = ExampleProfile(_profileIndex);
        if (_steps.Count < ThermalProfile.StepCount) return false;
        for (int i = 0; i < ThermalProfile.StepCount; i++)
            if (_steps[i].Ramp != ex.Ramp[i] || _steps[i].Target != ex.Target[i] || _steps[i].Dwell != ex.Dwell[i])
                return false;
        return true;
    }
}
