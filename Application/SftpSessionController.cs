using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

public enum SftpSessionState
{
    Idle,
    ListingDirectory,
    Transferring,
    Cancelled,
    Failed
}

public sealed record SftpDirectoryListing(string Path, IReadOnlyList<RemoteFileItem> Items)
{
    public static SftpDirectoryListing Empty(string path) => new(path, []);
}

public sealed record SftpSessionSnapshot(
    SftpSessionState State,
    SftpDirectoryListing DirectoryListing,
    bool IsBusy,
    bool CanNavigate,
    bool CanModifyRemoteFiles,
    bool CanTransfer,
    string StatusMessage,
    string? ErrorMessage)
{
    public string CurrentPath => DirectoryListing.Path;
}

public sealed class SftpSessionController : IDisposable
{
    private readonly ISftpFileService _fileService;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private SftpDirectoryListing _directoryListing = SftpDirectoryListing.Empty("/");
    private CancellationTokenSource? _transferCts;
    private SftpSessionState _state = SftpSessionState.Idle;
    private string _statusMessage = string.Empty;
    private string? _errorMessage;

    public SftpSessionController(ISftpFileService fileService)
    {
        _fileService = fileService;
    }

    public SftpSessionSnapshot Snapshot => CreateSnapshot();

    public event EventHandler<SftpSessionSnapshot>? SnapshotChanged;

    public void CancelTransfer()
    {
        if (_state != SftpSessionState.Transferring) return;
        _transferCts?.Cancel();
    }

    public Task RefreshAsync() => RefreshCurrentDirectoryAsync();

    public async Task NavigateToAsync(string path)
    {
        if (!_fileService.IsConnected)
        {
            FailDirectoryRead("SFTP 尚未连接。");
            return;
        }
        if (!CanNavigate())
        {
            FailDirectoryRead("当前操作尚未完成。");
            return;
        }

        var targetPath = RemotePath.Normalize(_directoryListing.Path, path);
        await RefreshDirectoryAsync(targetPath);
    }

    public Task NavigateUpAsync() =>
        _directoryListing.Path == "/"
            ? RefreshAsync()
            : NavigateToAsync(RemotePath.Parent(_directoryListing.Path));

    public async Task CreateDirectoryAsync(string name)
    {
        if (!CanModifyRemoteFiles())
        {
            PublishOperationStatus("当前操作尚未完成。");
            return;
        }
        if (!SftpPathValidator.TryValidateRemoteName(name, out var error))
        {
            PublishOperationStatus(error);
            return;
        }

        try
        {
            await _fileService.CreateDirectoryAsync(RemotePath.Combine(_directoryListing.Path, name));
            await RefreshDirectoryAsync(_directoryListing.Path, "文件夹已创建。");
        }
        catch (Exception exception)
        {
            FailOperation("新建失败", exception);
        }
    }

    public async Task RenameAsync(RemoteFileItem item, string name)
    {
        if (!CanModifyRemoteFiles() || item.Name == "..")
        {
            PublishOperationStatus("当前项目不可重命名。");
            return;
        }
        if (!SftpPathValidator.TryValidateRemoteName(name, out var error))
        {
            PublishOperationStatus(error);
            return;
        }
        if (string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            PublishOperationStatus("名称未改变。");
            return;
        }

        try
        {
            await _fileService.RenameAsync(item.FullPath, RemotePath.Combine(_directoryListing.Path, name));
            await RefreshDirectoryAsync(_directoryListing.Path, "重命名完成。");
        }
        catch (Exception exception)
        {
            FailOperation("重命名失败", exception);
        }
    }

    public async Task DeleteAsync(RemoteFileItem item)
    {
        if (!CanModifyRemoteFiles() || item.Name == "..")
        {
            PublishOperationStatus("当前项目不可删除。");
            return;
        }

        try
        {
            await _fileService.DeleteAsync(item);
            await RefreshDirectoryAsync(_directoryListing.Path, "删除完成。");
        }
        catch (Exception exception)
        {
            FailOperation("删除失败", exception);
        }
    }

    public Task UploadAsync(
        string localFileName,
        Func<Task<Stream>> openInput,
        Func<string, Task<bool>> confirmOverwrite) =>
        RunTransferAsync("上传", async cancellationToken =>
        {
            if (!SftpPathValidator.TryValidateRemoteName(localFileName, out var error))
                return OperationOutcome.Failure(error);

            var remotePath = RemotePath.Combine(_directoryListing.Path, localFileName);
            if (await _fileService.ExistsAsync(remotePath) && !await confirmOverwrite(localFileName))
                return OperationOutcome.Failure("已跳过现有文件。");

            using var input = await openInput();
            await _fileService.UploadAsync(input, remotePath, cancellationToken);
            return OperationOutcome.Success($"已上传 {localFileName}。");
        }, refreshDirectory: true);

    public Task DownloadAsync(
        RemoteFileItem item,
        string destinationDirectory,
        Func<string, bool> localFileExists,
        Func<string, Stream> createLocalOutput,
        Func<string, Task<bool>> confirmOverwrite) =>
        RunTransferAsync("下载", async cancellationToken =>
        {
            if (item.IsDirectory)
                return OperationOutcome.Failure("只能下载文件。");
            if (!SftpPathValidator.TryResolveDownloadPath(
                    destinationDirectory,
                    item.Name,
                    out var localPath,
                    out var error))
            {
                return OperationOutcome.Failure(error);
            }
            if (localFileExists(localPath) && !await confirmOverwrite(item.Name))
                return OperationOutcome.Failure("已保留现有文件。");

            using var output = createLocalOutput(localPath);
            await _fileService.DownloadAsync(item.FullPath, output, cancellationToken);
            return OperationOutcome.Success($"已下载 {item.Name}。");
        }, refreshDirectory: false);

    private async Task RefreshCurrentDirectoryAsync()
    {
        if (!_fileService.IsConnected)
        {
            FailDirectoryRead("SFTP 尚未连接。");
            return;
        }
        if (!CanNavigate())
        {
            FailDirectoryRead("当前操作尚未完成。");
            return;
        }

        await RefreshDirectoryAsync(_directoryListing.Path);
    }

    private async Task RefreshDirectoryAsync(string path, string successMessage = "")
    {
        Transition(SftpSessionState.ListingDirectory, $"正在读取 {path}…");
        try
        {
            var items = await _fileService.ListDirectoryAsync(path);
            _directoryListing = new SftpDirectoryListing(path, items.ToList());
            Transition(SftpSessionState.Idle, successMessage);
        }
        catch (Exception exception)
        {
            FailDirectoryRead($"读取目录失败：{exception.Message}");
        }
    }

    private async Task RunTransferAsync(
        string action,
        Func<CancellationToken, Task<OperationOutcome>> operation,
        bool refreshDirectory)
    {
        if (!CanStartTransfer())
        {
            PublishOperationStatus("当前操作尚未完成。");
            return;
        }

        await _transferGate.WaitAsync();
        _transferCts = new CancellationTokenSource();
        Transition(SftpSessionState.Transferring, $"正在{action}…");
        try
        {
            var result = await operation(_transferCts.Token);
            if (!result.Succeeded)
            {
                Transition(SftpSessionState.Idle, result.Message);
                return;
            }

            if (refreshDirectory)
                await RefreshDirectoryAsync(_directoryListing.Path, result.Message);
            else
                Transition(SftpSessionState.Idle, result.Message);
        }
        catch (OperationCanceledException)
        {
            Transition(SftpSessionState.Cancelled, $"{action}已取消。");
        }
        catch (Exception exception)
        {
            FailOperation($"{action}失败", exception);
        }
        finally
        {
            _transferCts.Dispose();
            _transferCts = null;
            _transferGate.Release();
        }
    }

    private void FailDirectoryRead(string message) =>
        Transition(SftpSessionState.Failed, message, message);

    private void FailOperation(string action, Exception exception)
    {
        var message = $"{action}：{exception.Message}";
        Transition(SftpSessionState.Failed, message, message);
    }

    private void PublishOperationStatus(string message) =>
        Transition(_state, message);

    private bool CanNavigate() => _state is not SftpSessionState.ListingDirectory and not SftpSessionState.Transferring;
    private bool CanModifyRemoteFiles() => CanNavigate() && _fileService.IsConnected;
    private bool CanStartTransfer() => CanNavigate() && _fileService.IsConnected;

    private void Transition(SftpSessionState state, string statusMessage, string? errorMessage = null)
    {
        _state = state;
        _statusMessage = statusMessage;
        _errorMessage = errorMessage;
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    private SftpSessionSnapshot CreateSnapshot() =>
        new(
            _state,
            _directoryListing,
            _state is SftpSessionState.ListingDirectory or SftpSessionState.Transferring,
            CanNavigate(),
            CanModifyRemoteFiles(),
            CanStartTransfer(),
            _statusMessage,
            _errorMessage);

    public void Dispose()
    {
        _transferCts?.Cancel();
        _transferCts?.Dispose();
        _transferGate.Dispose();
    }

    private sealed record OperationOutcome(bool Succeeded, string Message)
    {
        public static OperationOutcome Success(string message) => new(true, message);
        public static OperationOutcome Failure(string message) => new(false, message);
    }
}
