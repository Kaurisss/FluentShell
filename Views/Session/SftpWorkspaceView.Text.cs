using FluentShell.Services;
using Microsoft.UI.Xaml.Controls;

namespace FluentShell.Views.Session;

public sealed partial class SftpWorkspaceView
{
    private ITextFileService? _textFileService;
    private readonly List<TextFileEditorWindow> _textEditors = [];

    public void SetTextFileService(ITextFileService? service) => _textFileService = service;
    public bool TryCloseTextEditor()
    {
        var closed = true;
        foreach (var editor in _textEditors.ToArray()) closed &= editor.TryClose();
        return closed;
    }
    public bool IsTextEditorOpen => _textEditors.Count != 0;

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
        if (_localOperationBusy || !LocalFiles.IsEnabled || LocalFiles.SelectedItem is not LocalPaneItem item) return Task.CompletedTask;
        return item.IsDirectory ? NavigateLocalAsync(item.FullPath)
            : ShowTextEditorAsync(new LocalTextFileService(), item.FullPath, item.Name, false);
    }

    private async Task ShowTextEditorAsync(ITextFileService service, string path, string name, bool remote)
    {
        if (XamlRoot is null) return;
        var existing = _textEditors.FirstOrDefault(editor => editor.IsRemote == remote &&
            string.Equals(editor.FilePath, path, remote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { existing.Activate(); return; }
        TextFileEditorWindow? editor = null;
        try
        {
            editor = new TextFileEditorWindow(service, path, name, remote, XamlRoot, _windowHandle);
            _textEditors.Add(editor);
            editor.Closed += (_, _) => _textEditors.Remove(editor);
            editor.Activate();
            await editor.Completion;
            if (editor.WasSaved && IsLoaded)
            {
                if (remote && _snapshot.CanNavigate) RefreshRequested?.Invoke(this, EventArgs.Empty);
                if (!remote) RefreshLocalDirectory();
            }
        }
        catch (Exception exception)
        {
            editor?.TryClose();
            if (IsLoaded) await ShowFailureDialogAsync($"无法打开文本编辑器：{exception.Message}");
        }
        finally { if (editor is not null) _textEditors.Remove(editor); }
    }
}
