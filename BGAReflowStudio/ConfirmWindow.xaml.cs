using System.Windows;

namespace BGAReflowStudio;

public enum ConfirmResult { Primary, Secondary, Cancel }

/// <summary>A themed confirmation dialog (matches the app's card style) replacing plain MessageBoxes.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmResult Result { get; private set; } = ConfirmResult.Cancel;

    public ConfirmWindow(string title, string message, string primaryText, string? secondaryText = null,
                         string cancelText = "Cancel", bool showCancel = true)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        CancelButton.Content = cancelText;
        if (!showCancel) CancelButton.Visibility = Visibility.Collapsed;   // info-only dialog (OK)
        if (secondaryText != null)
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) { Result = ConfirmResult.Primary; DialogResult = true; }
    private void Secondary_Click(object sender, RoutedEventArgs e) { Result = ConfirmResult.Secondary; DialogResult = true; }
}
