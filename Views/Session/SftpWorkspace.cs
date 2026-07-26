using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using Microsoft.UI.Xaml;

namespace FluentShell.Views.Session;

public sealed class SftpWorkspace : IDisposable
{
    private readonly SftpSessionController _controller;
    private readonly SftpWorkspaceView _view;

    public SftpWorkspace(
        IntPtr windowHandle,
        ISftpFileService fileService,
        ElementTheme workspaceTheme)
    {
        _controller = new SftpSessionController(fileService);
        _view = new SftpWorkspaceView(windowHandle, workspaceTheme);
        _controller.SnapshotChanged += Controller_SnapshotChanged;
        _view.RefreshRequested += View_RefreshRequested;
        _view.NavigateRequested += View_NavigateRequested;
        _view.NewFolderRequested += View_NewFolderRequested;
        _view.UploadRequested += View_UploadRequested;
        _view.DownloadRequested += View_DownloadRequested;
        _view.RenameRequested += View_RenameRequested;
        _view.DeleteRequested += View_DeleteRequested;
        _view.CancelTransferRequested += View_CancelTransferRequested;
        _view.Render(_controller.Snapshot);
    }

    public FrameworkElement View => _view;
    public bool IsTransferActive => _controller.Snapshot.State == SftpSessionState.Transferring;

    public Task RefreshAsync() => _controller.RefreshAsync();

    public void CancelTransfer() => _controller.CancelTransfer();

    private void Controller_SnapshotChanged(object? sender, SftpSessionSnapshot snapshot) =>
        _view.Render(snapshot);

    private async void View_RefreshRequested(object? sender, EventArgs e) => await RefreshAsync();

    private async void View_NavigateRequested(object? sender, string path) =>
        await _controller.NavigateToAsync(path);

    private async void View_NewFolderRequested(object? sender, EventArgs e)
    {
        var name = await _view.PromptTextAsync("新建文件夹", "文件夹名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _controller.CreateDirectoryAsync(name);
    }

    private async void View_UploadRequested(object? sender, EventArgs e)
    {
        var files = await _view.PickUploadFilesAsync();
        foreach (var file in files)
        {
            await _controller.UploadAsync(
                file.Name,
                file.OpenRead,
                _view.ConfirmOverwriteAsync);
        }
    }

    private async void View_DownloadRequested(object? sender, RemoteFileItem item)
    {
        var destinationDirectory = await _view.PickDownloadDirectoryAsync();
        if (destinationDirectory is null) return;

        await _controller.DownloadAsync(
            item,
            destinationDirectory,
            File.Exists,
            File.Create,
            _view.ConfirmOverwriteAsync);
    }

    private async void View_RenameRequested(object? sender, RemoteFileItem item)
    {
        var name = await _view.PromptTextAsync("重命名", "输入新名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _controller.RenameAsync(item, name);
    }

    private async void View_DeleteRequested(object? sender, RemoteFileItem item)
    {
        if (!await _view.ConfirmDeleteAsync(item)) return;
        await _controller.DeleteAsync(item);
    }

    private void View_CancelTransferRequested(object? sender, EventArgs e) => _controller.CancelTransfer();

    public void Dispose()
    {
        _controller.SnapshotChanged -= Controller_SnapshotChanged;
        _view.RefreshRequested -= View_RefreshRequested;
        _view.NavigateRequested -= View_NavigateRequested;
        _view.NewFolderRequested -= View_NewFolderRequested;
        _view.UploadRequested -= View_UploadRequested;
        _view.DownloadRequested -= View_DownloadRequested;
        _view.RenameRequested -= View_RenameRequested;
        _view.DeleteRequested -= View_DeleteRequested;
        _view.CancelTransferRequested -= View_CancelTransferRequested;
        _controller.Dispose();
    }
}
