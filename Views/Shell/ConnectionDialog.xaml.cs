using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public sealed partial class ConnectionDialog : ContentDialog
{
    public ConnectionDialog()
    {
        InitializeComponent();
    }

    public void UpdateMessage(string message)
    {
        ConnectionMessageText.Text = message;
    }
}
