using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Shell;
using Microsoft.UI.Xaml.Controls;

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
    private readonly DownloadDestination _downloadDestination;
    private readonly FileConflictResolver _conflictResolver = new();
    private readonly TransferCenter _transfers;
    private readonly Guid _connectionId;
    private readonly string _connectionLabel;
    private readonly ISftpFileService _transferService;
    private TransferTask? _currentTask;
    private bool _batchRunning;
    private bool _picking;
    private bool _disposed;
    private UserPreferences _preferences = new();
    public void SetPreferences(UserPreferences preferences) => _preferences = preferences.Normalize();

    public SftpWorkspace(
        ISftpFileService fileService,
        ISftpWorkspaceView view,
        Func<string, bool>? localFileExists = null,
        Func<string, Stream>? createLocalOutput = null,
        Action<string>? createLocalDirectory = null,
        Action<string>? deleteLocalFile = null,
        Action<Action>? dispatchProgress = null,
        ISftpFileService? transferFileService = null,
        TransferCenter? transfers = null,
        Guid? connectionId = null,
        string connectionLabel = "当前连接")
    {
        _controller = new SftpSessionController(fileService, transferFileService, dispatchProgress);
        _view = view;
        _transfers = transfers ?? new TransferCenter();
        _connectionId = connectionId ?? Guid.NewGuid();
        _connectionLabel = connectionLabel;
        _transferService = transferFileService ?? fileService;
        _downloadDestination = new DownloadDestination(
            localFileExists ?? File.Exists,
            createLocalOutput ?? (path => File.Create(path)),
            createLocalDirectory ?? (path => Directory.CreateDirectory(path)),
            deleteLocalFile ?? File.Delete);

        _controller.SnapshotChanged += Controller_SnapshotChanged;
        _view.RefreshRequested += View_RefreshRequested;
        _view.NavigateRequested += View_NavigateRequested;
        _view.NewFolderRequested += View_NewFolderRequested;
        _view.UploadRequested += View_UploadRequested;
        _view.UploadFolderRequested += View_UploadFolderRequested;
        _view.DownloadRequested += View_DownloadRequested;
        _view.RenameRequested += View_RenameRequested;
        _view.DeleteRequested += View_DeleteRequested;
        if (_view is ISftpPaneTransferView panes)
        {
            panes.UploadSelectionRequested += UploadSelectionRequested;
            panes.DownloadToLocalRequested += DownloadToLocalRequested;
        }
        _view.Render(_controller.Snapshot);
    }

    public bool IsTransferActive => _batchRunning || _controller.Snapshot.Transfer.IsActive;

    public Task RefreshAsync() => _controller.RefreshAsync();

    public Task NavigateToAsync(string path) => _controller.NavigateToAsync(path);

    public async Task CreateFolderAsync()
    {
        var name = await _view.PromptTextAsync("新建文件夹", "文件夹名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _controller.CreateDirectoryAsync(name);
    }

    public Task UploadAsync(IReadOnlyList<SftpUploadEntry>? selectedFiles = null) =>
        PickAndUploadAsync(async () => (IReadOnlyList<SftpUploadEntry>?)selectedFiles?.ToArray()
            ?? await _view.PickUploadFilesAsync());

    public Task UploadFolderAsync() => PickAndUploadAsync(async () =>
        await _view.PickUploadFolderAsync() is { } folder ? [folder] : []);

    private async Task PickAndUploadAsync(Func<Task<IReadOnlyList<SftpUploadEntry>>> pick)
    {
        if (_disposed || _batchRunning || _picking) return;
        _picking = true;
        try
        {
            var files = await pick();
            if (files.Count == 0 || _disposed) return;
            // Capture the target once: browsing another directory must not redirect later files.
            var target = _controller.Snapshot.DirectoryListing.Path;
            TransferTask? task = null;
            async Task Run() => await RunBatchAsync(task!, () =>
                _controller.UploadEntriesAsync(files, ConfirmOverwriteAsync, target));
            task = _transfers.Add(_connectionId, _connectionLabel, "上传",
                files.Count == 1 ? files[0].Name : $"{files[0].Name} 等 {files.Count} 项",
                target, Run, CanRetry);
            await Run();
        }
        finally { _picking = false; _transfers.RefreshCommands(); }
    }

    public async Task DownloadAsync(RemoteFileItem item, string? targetDirectory = null)
    {
        if (_disposed || _batchRunning || _picking) return;
        _picking = true;
        try
        {
            var destination = targetDirectory ?? await _view.PickDownloadDirectoryAsync();
            if (destination is null || _disposed) return;
            TransferTask? task = null;
            async Task Run() => await RunBatchAsync(task!, () =>
                _controller.DownloadAsync(item, destination, _downloadDestination, ConfirmOverwriteAsync));
            task = _transfers.Add(_connectionId, _connectionLabel, "下载", item.Name,
                destination, Run, CanRetry);
            await Run();
        }
        finally { _picking = false; _transfers.RefreshCommands(); }
    }

    private bool CanRetry() => !_disposed && !_batchRunning && !_picking && _transferService.IsConnected;

    private async Task RunBatchAsync(TransferTask task, Func<Task> operation)
    {
        if (_disposed || _batchRunning) return;
        _batchRunning = true;
        _currentTask = task;
        using var control = new TransferControl();
        task.Start(control);
        _controller.BeginBatch(control);
        _conflictResolver.Reset(_preferences.ConflictPolicy);
        _transfers.RefreshCommands();
        try
        {
            using var lease = await _transfers.Limiter.AcquireAsync(control.Token);
            await control.WaitAsync();
            await operation();
            var snapshot = _controller.Snapshot;
            task.Update(snapshot);
            task.Finish(snapshot.Queue.FailedCount > 0 ||
                snapshot.Transfer.State is SftpTransferState.Failed or SftpTransferState.Cancelled,
                snapshot.Queue.FailedCount > 0
                    ? $"{snapshot.Queue.FailedCount} 项失败，请展开文件明细。重试会重新执行本批任务，并再次确认覆盖。"
                    : snapshot.Transfer.Message);
        }
        catch (OperationCanceledException)
        {
            task.Finish(true, "传输已取消。");
        }
        catch (Exception exception)
        {
            task.Finish(true, $"传输失败：{exception.Message}");
        }
        finally
        {
            _currentTask = null;
            _batchRunning = false;
            _controller.EndBatch();
            if (_view is ISftpPaneTransferView panes) panes.RefreshLocalDirectory();
            _transfers.RefreshCommands();
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(string name)
    {
        var resolution = await _conflictResolver.ResolveConflictAsync(name, async fileName =>
        {
            if (_view is not UserControl view)
                return (await _view.ConfirmOverwriteAsync(fileName), false, false);
            if (view.XamlRoot is null) return (false, false, true);
            var dialog = new FileConflictDialog
            {
                Message = $"\"{fileName}\"已存在，是否覆盖？",
                XamlRoot = view.XamlRoot
            };
            await dialog.ShowAsync();
            return (dialog.Resolution == FileConflictResolution.Overwrite,
                dialog.ApplyToAll, dialog.Resolution == FileConflictResolution.CancelAll);
        });
        if (resolution is null) _controller.CancelTransfer();
        return resolution ?? false;
    }

    public void ConnectionLost()
    {
        _currentTask?.Disconnect();
        _controller.CancelTransfer();
        _transfers.RefreshCommands();
    }

    public void RefreshTransferCommands() => _transfers.RefreshCommands();

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

    private void Controller_SnapshotChanged(object? sender, SftpSessionSnapshot snapshot)
    {
        if (_disposed) return;
        _currentTask?.Update(snapshot);
        _view.Render(snapshot);
    }

    private async void View_RefreshRequested(object? sender, EventArgs e) => await RefreshAsync();

    private async void View_NavigateRequested(object? sender, string path) => await NavigateToAsync(path);

    private async void View_NewFolderRequested(object? sender, EventArgs e) => await CreateFolderAsync();

    private async void View_UploadRequested(object? sender, EventArgs e) => await UploadAsync();

    private async void View_UploadFolderRequested(object? sender, EventArgs e) => await UploadFolderAsync();

    private async void View_DownloadRequested(object? sender, RemoteFileItem item) =>
        await DownloadAsync(item);

    private async void View_RenameRequested(object? sender, RemoteFileItem item) => await RenameAsync(item);

    private async void View_DeleteRequested(object? sender, RemoteFileItem item) => await DeleteAsync(item);

    private async void UploadSelectionRequested(object? sender, IReadOnlyList<SftpUploadEntry> files) => await UploadAsync(files);

    private async void DownloadToLocalRequested(object? sender, SftpPaneDownload request) =>
        await DownloadAsync(request.Item, request.Destination);

    public void Dispose()
    {
        _disposed = true;
        if (_view is ISftpPaneTransferView panes)
        {
            panes.UploadSelectionRequested -= UploadSelectionRequested;
            panes.DownloadToLocalRequested -= DownloadToLocalRequested;
        }
        _transfers.Detach(_connectionId);
        _controller.SnapshotChanged -= Controller_SnapshotChanged;
        _view.RefreshRequested -= View_RefreshRequested;
        _view.NavigateRequested -= View_NavigateRequested;
        _view.NewFolderRequested -= View_NewFolderRequested;
        _view.UploadRequested -= View_UploadRequested;
        _view.UploadFolderRequested -= View_UploadFolderRequested;
        _view.DownloadRequested -= View_DownloadRequested;
        _view.RenameRequested -= View_RenameRequested;
        _view.DeleteRequested -= View_DeleteRequested;
        _controller.Dispose();
    }
}
