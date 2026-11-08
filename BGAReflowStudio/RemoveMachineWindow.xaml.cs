using System.Collections.Generic;
using System.Windows;

namespace BGAReflowStudio;

/// <summary>Themed picker for removing a custom machine (built-in machines are never listed).</summary>
public partial class RemoveMachineWindow : Window
{
    public string? Selected { get; private set; }

    public RemoveMachineWindow(IEnumerable<string> customMachines)
    {
        InitializeComponent();
        foreach (var m in customMachines) MachineList.Items.Add(m);
        if (MachineList.Items.Count > 0) MachineList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Selected = MachineList.SelectedItem?.ToString();
        DialogResult = Selected != null;
    }
}
