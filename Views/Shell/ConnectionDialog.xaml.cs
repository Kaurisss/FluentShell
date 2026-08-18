using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class ConnectionDialog : UserControl
{
    public ConnectionDialog()
    {
        InitializeComponent();
    }

    public event EventHandler? CancelRequested;

    public void UpdateMessage(string message)
    {
        ConnectionMessageText.Text = message;
    }

    public void FocusCancelButton() =>
        CancelButton.Focus(FocusState.Programmatic);

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);
}
