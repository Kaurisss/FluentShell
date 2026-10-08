using FluentShell.Services;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Session;

public sealed partial class SftpWorkspaceView
{
    private ITextFileService? _textFileService;
    private TextFileEditorDialog? _textEditor;

    public void SetTextFileService(ITextFileService? service) => _textFileService = service;
    public bool TryCloseTextEditor() => _textEditor?.TryClose() != false;
    public bool IsTextEditorOpen => _textEditor is not null;

    private void OpenSelectedRemoteText()
    {
        if (_snapshot.CanTransfer && SelectedItem is { IsDirectory: false, IsSymbolicLink: false } item && _textFileService is { } service)
        {
            if (!SftpPathValidator.TryValidateRemoteName(item.Name, out var error))
            {
                _ = ShowFailureDialogAsync(error);
                return;
            }
            var path = RemotePath.Combine(_snapshot.DirectoryListing.Path, item.Name);
            _ = ShowTextEditorAsync(service, path, item.Name, true);
        }
    }

    private Task OpenLocalItemAsync()
    {
        if (LocalFiles.SelectedItem is not LocalPaneItem item) return Task.CompletedTask;
        return item.IsDirectory ? NavigateLocalAsync(item.FullPath)
            : ShowTextEditorAsync(new LocalTextFileService(), item.FullPath, item.Name, false);
    }

    private async Task ShowTextEditorAsync(ITextFileService service, string path, string name, bool remote)
    {
        if (XamlRoot is null || _textEditor is not null) return;
        var editor = new TextFileEditorDialog(service, path, name, remote, XamlRoot, ActualTheme);
        _textEditor = editor;
        try { await editor.ShowAsync(); }
        finally
        {
            _textEditor = null;
            if (editor.WasSaved)
            {
                if (remote && _snapshot.CanNavigate) RefreshRequested?.Invoke(this, EventArgs.Empty);
                if (!remote) RefreshLocalDirectory();
            }
        }
    }
}
