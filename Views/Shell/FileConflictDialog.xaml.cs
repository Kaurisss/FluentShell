using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Shell;

public enum FileConflictResolution
{
    Overwrite,
    Skip,
    CancelAll
}

public sealed partial class FileConflictDialog : ContentDialog
{
    public FileConflictDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        SecondaryButtonClick += OnSecondaryButtonClick;
        CloseButtonClick += OnCloseButtonClick;
    }

    public string Message
    {
        get => MessageText.Text;
        set => MessageText.Text = value;
    }

    public bool ApplyToAll => ApplyToAllCheckBox.IsChecked == true;

    public FileConflictResolution Resolution { get; private set; } = FileConflictResolution.CancelAll;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Resolution = FileConflictResolution.Overwrite;
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Resolution = FileConflictResolution.Skip;
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Resolution = FileConflictResolution.CancelAll;
    }
}
