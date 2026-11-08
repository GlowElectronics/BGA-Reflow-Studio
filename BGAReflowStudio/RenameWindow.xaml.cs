using System.Windows;

namespace BGAReflowStudio;

public partial class RenameWindow : Window
{
    public string Value { get; private set; } = "";

    public RenameWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = title;
        PromptText.Text = prompt;
        NameBox.Text = initial;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public RenameWindow(int index, string current)
        : this("Rename profile set", $"New name for profile set {index}:", current)
    {
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = NameBox.Text.Trim();
        DialogResult = true;
    }
}
