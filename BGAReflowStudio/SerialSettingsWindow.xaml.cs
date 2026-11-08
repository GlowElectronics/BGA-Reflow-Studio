using System.Windows;
using BGAReflowStudio.Models;

namespace BGAReflowStudio;

public partial class SerialSettingsWindow : Window
{
    private readonly string _iniPath;

    public SerialSettingsWindow(string iniPath)
    {
        InitializeComponent();
        _iniPath = iniPath;

        for (int i = 1; i <= 20; i++) PortBox.Items.Add($"COM{i}");
        foreach (var b in new[] { "300", "600", "1200", "2400", "4800", "9600", "19200" }) BaudBox.Items.Add(b);
        foreach (var d in new[] { "7", "8" }) DataBox.Items.Add(d);
        foreach (var p in new[] { "EVEN", "MARK", "NONE", "ODD", "SPACE" }) ParityBox.Items.Add(p);
        foreach (var s in new[] { "1", "2" }) StopBox.Items.Add(s);

        Select(PortBox, IniFile.Read("Serial", "Port", _iniPath, "COM1"), 0);
        Select(BaudBox, IniFile.Read("Serial", "BaudRate", _iniPath, "9600"), 5);
        Select(DataBox, IniFile.Read("Serial", "DataBits", _iniPath, "7"), 0);
        Select(ParityBox, IniFile.Read("Serial", "Parity", _iniPath, "EVEN"), 0);
        Select(StopBox, IniFile.Read("Serial", "StopBits", _iniPath, "1"), 0);
    }

    private static void Select(System.Windows.Controls.ComboBox box, string value, int fallback)
    {
        box.SelectedItem = value;
        if (box.SelectedIndex < 0) box.SelectedIndex = fallback;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        IniFile.Write("Serial", "Port", PortBox.SelectedItem?.ToString() ?? "COM1", _iniPath);
        IniFile.Write("Serial", "BaudRate", BaudBox.SelectedItem?.ToString() ?? "9600", _iniPath);
        IniFile.Write("Serial", "DataBits", DataBox.SelectedItem?.ToString() ?? "7", _iniPath);
        IniFile.Write("Serial", "Parity", ParityBox.SelectedItem?.ToString() ?? "EVEN", _iniPath);
        IniFile.Write("Serial", "StopBits", StopBox.SelectedItem?.ToString() ?? "1", _iniPath);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
