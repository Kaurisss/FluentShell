using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

/// <summary>
/// SFTP 工作区的提示流程：先向用户取得确认、名称或目录，再把结果作为值交给
/// <see cref="SftpSessionController"/>，并把控制器的快照送回视图。
/// </summary>
/// <remarks>
/// 每条流程都以 <c>public Task</c> 方法暴露，视图事件只是它们的转发器 —— 这样
/// “先确认再执行”的判断可以在不启动窗口的情况下被等待和断言。
/// </remarks>
public sealed class SftpWorkspace : IDisposable
{
    private readonly SftpSessionController _controller;
    private readonly ISftpWorkspaceView _view;
    private readonly Func<string, bool> _localFileExists;
    private readonly Func<string, Stream> _createLocalOutput;

    public SftpWorkspace(
        ISftpFileService fileService,
        ISftpWorkspaceView view,
        Func<string, bool>? localFileExists = null,
        Func<string, Stream>? createLocalOutput = null)
    {
        _controller = new SftpSessionController(fileService);
        _view = view;
        _localFileExists = localFileExists ?? File.Exists;
        _createLocalOutput = createLocalOutput ?? (path => File.Create(path));

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

    public bool IsTransferActive => _controller.Snapshot.State == SftpSessionState.Transferring;

    public Task RefreshAsync() => _controller.RefreshAsync();

    public Task NavigateToAsync(string path) => _controller.NavigateToAsync(path);

    public void CancelTransfer() => _controller.CancelTransfer();

    public async Task CreateFolderAsync()
    {
        var name = await _view.PromptTextAsync("新建文件夹", "文件夹名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _controller.CreateDirectoryAsync(name);
    }

    public async Task UploadAsync()
    {
        foreach (var file in await _view.PickUploadFilesAsync())
        {
            await _controller.UploadAsync(file.Name, file.OpenRead, _view.ConfirmOverwriteAsync);
            // 用户按下取消是针对整批的，不只是当前这个文件：控制器停在 Cancelled 上，
            // 而 Cancelled 允许下一次传输开始，所以停止的判断必须在这里做。
            if (_controller.Snapshot.State == SftpSessionState.Cancelled) return;
        }
    }

    public async Task DownloadAsync(RemoteFileItem item)
    {
        var destinationDirectory = await _view.PickDownloadDirectoryAsync();
        if (destinationDirectory is null) return;

        await _controller.DownloadAsync(
            item,
            destinationDirectory,
            _localFileExists,
            _createLocalOutput,
            _view.ConfirmOverwriteAsync);
    }

    public async Task RenameAsync(RemoteFileItem item)
    {
        var name = await _view.PromptTextAsync("重命名", "输入新名称", item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _controller.RenameAsync(item, name);
    }

    public async Task DeleteAsync(RemoteFileItem item)
    {
        if (!await _view.ConfirmDeleteAsync(item)) return;
        await _controller.DeleteAsync(item);
    }

    private void Controller_SnapshotChanged(object? sender, SftpSessionSnapshot snapshot) =>
        _view.Render(snapshot);

    private async void View_RefreshRequested(object? sender, EventArgs e) => await RefreshAsync();

    private async void View_NavigateRequested(object? sender, string path) => await NavigateToAsync(path);

    private async void View_NewFolderRequested(object? sender, EventArgs e) => await CreateFolderAsync();

    private async void View_UploadRequested(object? sender, EventArgs e) => await UploadAsync();

    private async void View_DownloadRequested(object? sender, RemoteFileItem item) =>
        await DownloadAsync(item);

    private async void View_RenameRequested(object? sender, RemoteFileItem item) => await RenameAsync(item);

    private async void View_DeleteRequested(object? sender, RemoteFileItem item) => await DeleteAsync(item);

    private void View_CancelTransferRequested(object? sender, EventArgs e) => CancelTransfer();

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
