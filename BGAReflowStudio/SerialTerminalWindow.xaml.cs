using System;
using System.Windows;

namespace BGAReflowStudio;

/// <summary>A live view of the serial TX/RX traffic (fed by Pc410Controller.TrafficLogged).</summary>
public partial class SerialTerminalWindow : Window
{
    private const int MaxChars = 200_000;   // cap so a long session can't grow unbounded

    public SerialTerminalWindow() => InitializeComponent();

    public void AppendLine(string line)
    {
        Output.AppendText(line + Environment.NewLine);
        if (Output.Text.Length > MaxChars)
            Output.Text = Output.Text[^(MaxChars * 3 / 4)..];   // trim the oldest quarter-ish
        if (AutoScroll.IsChecked == true) { Output.CaretIndex = Output.Text.Length; Output.ScrollToEnd(); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Output.Clear();
}
