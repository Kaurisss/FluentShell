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

public sealed record SftpSessionSnapshot(
    SftpSessionState State,
    string CurrentPath,
    bool IsBusy,
    bool CanNavigate,
    bool CanModifyRemoteFiles,
    bool CanTransfer,
    string StatusMessage,
    string? ErrorMessage);

public sealed record SftpDirectoryListing(
    bool Succeeded,
    string Path,
    IReadOnlyList<RemoteFileItem> Items,
    string? ErrorMessage)
{
    public static SftpDirectoryListing Success(string path, IReadOnlyList<RemoteFileItem> items) =>
        new(true, path, items, null);

    public static SftpDirectoryListing Failure(string path, string message) =>
        new(false, path, [], message);
}

public sealed record SftpOperationResult(bool Succeeded, string Message)
{
    public static SftpOperationResult Success(string message) => new(true, message);
    public static SftpOperationResult Failure(string message) => new(false, message);
}

public sealed class SftpSessionController : IDisposable
{
    private readonly ISftpFileService _fileService;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private CancellationTokenSource? _transferCts;
    private string _currentPath = "/";
    private SftpSessionState _state = SftpSessionState.Idle;

    public SftpSessionController(ISftpFileService fileService)
    {
        _fileService = fileService;
    }

    public SftpSessionSnapshot Snapshot => CreateSnapshot();

    public event EventHandler<SftpSessionSnapshot>? StateChanged;

    public void CancelTransfer()
    {
        if (_state != SftpSessionState.Transferring) return;
        _transferCts?.Cancel();
    }

    public async Task<SftpDirectoryListing> RefreshAsync(bool showHiddenFiles)
    {
        if (!_fileService.IsConnected)
            return FailListing("SFTP 尚未连接。");
        if (!CanNavigate())
            return FailListing("当前操作尚未完成。");

        return await ListDirectoryAsync(_currentPath, showHiddenFiles);
    }

    public async Task<SftpDirectoryListing> NavigateToAsync(string path, bool showHiddenFiles)
    {
        if (!_fileService.IsConnected)
            return FailListing("SFTP 尚未连接。");
        if (!CanNavigate())
            return FailListing("当前操作尚未完成。");

        var previousPath = _currentPath;
        _currentPath = RemotePath.Normalize(_currentPath, path);
        var listing = await ListDirectoryAsync(_currentPath, showHiddenFiles);
        if (!listing.Succeeded) _currentPath = previousPath;
        return listing;
    }

    public Task<SftpDirectoryListing> NavigateUpAsync(bool showHiddenFiles) =>
        _currentPath == "/"
            ? RefreshAsync(showHiddenFiles)
            : NavigateToAsync(RemotePath.Parent(_currentPath), showHiddenFiles);

    public async Task<SftpOperationResult> CreateDirectoryAsync(string name, bool showHiddenFiles)
    {
        if (!CanModifyRemoteFiles())
            return SftpOperationResult.Failure("当前操作尚未完成。");
        if (!SftpPathValidator.TryValidateRemoteName(name, out var error))
            return SftpOperationResult.Failure(error);

        try
        {
            await _fileService.CreateDirectoryAsync(RemotePath.Combine(_currentPath, name));
            await RefreshAsync(showHiddenFiles);
            return SftpOperationResult.Success("文件夹已创建。");
        }
        catch (Exception exception)
        {
            return FailOperation("新建失败", exception);
        }
    }

    public async Task<SftpOperationResult> RenameAsync(
        RemoteFileItem item,
        string name,
        bool showHiddenFiles)
    {
        if (!CanModifyRemoteFiles() || item.Name == "..")
            return SftpOperationResult.Failure("当前项目不可重命名。");
        if (!SftpPathValidator.TryValidateRemoteName(name, out var error))
            return SftpOperationResult.Failure(error);
        if (string.Equals(item.Name, name, StringComparison.Ordinal))
            return SftpOperationResult.Success("名称未改变。");

        try
        {
            await _fileService.RenameAsync(item.FullPath, RemotePath.Combine(_currentPath, name));
            await RefreshAsync(showHiddenFiles);
            return SftpOperationResult.Success("重命名完成。");
        }
        catch (Exception exception)
        {
            return FailOperation("重命名失败", exception);
        }
    }

    public async Task<SftpOperationResult> DeleteAsync(RemoteFileItem item, bool showHiddenFiles)
    {
        if (!CanModifyRemoteFiles() || item.Name == "..")
            return SftpOperationResult.Failure("当前项目不可删除。");

        try
        {
            await _fileService.DeleteAsync(item);
            await RefreshAsync(showHiddenFiles);
            return SftpOperationResult.Success("删除完成。");
        }
        catch (Exception exception)
        {
            return FailOperation("删除失败", exception);
        }
    }

    public Task<SftpOperationResult> UploadAsync(
        string localFileName,
        Func<Task<Stream>> openInput,
        Func<string, Task<bool>> confirmOverwrite,
        bool showHiddenFiles) =>
        RunTransferAsync("上传", async cancellationToken =>
        {
            if (!SftpPathValidator.TryValidateRemoteName(localFileName, out var error))
                return SftpOperationResult.Failure(error);

            var remotePath = RemotePath.Combine(_currentPath, localFileName);
            if (await _fileService.ExistsAsync(remotePath) && !await confirmOverwrite(localFileName))
                return SftpOperationResult.Failure("已跳过现有文件。");

            using var input = await openInput();
            await _fileService.UploadAsync(input, remotePath, cancellationToken);
            return SftpOperationResult.Success($"已上传 {localFileName}。");
        }, showHiddenFiles);

    public Task<SftpOperationResult> DownloadAsync(
        RemoteFileItem item,
        string destinationDirectory,
        Func<string, bool> localFileExists,
        Func<string, Stream> createLocalOutput,
        Func<string, Task<bool>> confirmOverwrite) =>
        RunTransferAsync("下载", async cancellationToken =>
        {
            if (item.IsDirectory)
                return SftpOperationResult.Failure("只能下载文件。");
            if (!SftpPathValidator.TryResolveDownloadPath(
                    destinationDirectory,
                    item.Name,
                    out var localPath,
                    out var error))
            {
                return SftpOperationResult.Failure(error);
            }
            if (localFileExists(localPath) && !await confirmOverwrite(item.Name))
                return SftpOperationResult.Failure("已保留现有文件。");

            using var output = createLocalOutput(localPath);
            await _fileService.DownloadAsync(item.FullPath, output, cancellationToken);
            return SftpOperationResult.Success($"已下载 {item.Name}。");
        }, showHiddenFiles: false);

    private async Task<SftpDirectoryListing> ListDirectoryAsync(string path, bool showHiddenFiles)
    {
        Transition(SftpSessionState.ListingDirectory, $"正在读取 {path}…");
        try
        {
            var items = await _fileService.ListDirectoryAsync(path, showHiddenFiles);
            Transition(SftpSessionState.Idle, string.Empty);
            return SftpDirectoryListing.Success(path, items);
        }
        catch (Exception exception)
        {
            var message = $"读取目录失败：{exception.Message}";
            Transition(SftpSessionState.Failed, message, message);
            return SftpDirectoryListing.Failure(path, message);
        }
    }

    private async Task<SftpOperationResult> RunTransferAsync(
        string action,
        Func<CancellationToken, Task<SftpOperationResult>> operation,
        bool showHiddenFiles)
    {
        if (!CanStartTransfer())
            return SftpOperationResult.Failure("当前操作尚未完成。");

        await _transferGate.WaitAsync();
        _transferCts = new CancellationTokenSource();
        Transition(SftpSessionState.Transferring, $"正在{action}…");
        try
        {
            var result = await operation(_transferCts.Token);
            if (!result.Succeeded)
            {
                Transition(SftpSessionState.Idle, result.Message);
                return result;
            }

            Transition(SftpSessionState.Idle, result.Message);
            if (showHiddenFiles) await RefreshAsync(showHiddenFiles);
            return result;
        }
        catch (OperationCanceledException)
        {
            Transition(SftpSessionState.Cancelled, $"{action}已取消。");
            return SftpOperationResult.Failure($"{action}已取消。");
        }
        catch (Exception exception)
        {
            return FailOperation($"{action}失败", exception);
        }
        finally
        {
            _transferCts.Dispose();
            _transferCts = null;
            _transferGate.Release();
        }
    }

    private SftpDirectoryListing FailListing(string message)
    {
        Transition(SftpSessionState.Failed, message, message);
        return SftpDirectoryListing.Failure(_currentPath, message);
    }

    private SftpOperationResult FailOperation(string action, Exception exception)
    {
        var message = $"{action}：{exception.Message}";
        Transition(SftpSessionState.Failed, message, message);
        return SftpOperationResult.Failure(message);
    }

    private bool CanNavigate() => _state is not SftpSessionState.ListingDirectory and not SftpSessionState.Transferring;
    private bool CanModifyRemoteFiles() => CanNavigate() && _fileService.IsConnected;
    private bool CanStartTransfer() => CanNavigate() && _fileService.IsConnected;

    private void Transition(SftpSessionState state, string statusMessage, string? errorMessage = null)
    {
        _state = state;
        StateChanged?.Invoke(this, CreateSnapshot(statusMessage, errorMessage));
    }

    private SftpSessionSnapshot CreateSnapshot(string statusMessage = "", string? errorMessage = null) =>
        new(
            _state,
            _currentPath,
            _state is SftpSessionState.ListingDirectory or SftpSessionState.Transferring,
            CanNavigate(),
            CanModifyRemoteFiles(),
            CanStartTransfer(),
            statusMessage,
            errorMessage);

    public void Dispose()
    {
        _transferCts?.Cancel();
        _transferCts?.Dispose();
        _transferGate.Dispose();
    }
}