using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

/// <summary>浏览轴的状态。传输在独立的传输轴上进行（<see cref="SftpTransferState"/>），
/// 两轴各走一条 SFTP 通道，浏览不必等传输。</summary>
public enum SftpSessionState
{
    Idle,
    ListingDirectory,
    Failed,
    Deleting
}

/// <summary>传输轴的状态。终态（完成/取消/失败）连同消息保留到下一次传输开始。</summary>
public enum SftpTransferState
{
    None,
    Transferring,
    Completed,
    Cancelled,
    Failed
}

public sealed record SftpTransferSnapshot(
    SftpTransferState State,
    string Message,
    SftpTransferProgress? Progress)
{
    public static readonly SftpTransferSnapshot None = new(SftpTransferState.None, string.Empty, null);

    public bool IsActive => State == SftpTransferState.Transferring;
}

/// <summary>
/// 失败的种类，决定视图用多重的方式打扰用户：目录读取失败是浏览途中的常态
/// （没权限的目录、敲错的路径），内联提示就够；文件操作失败对应一次明确下达的
/// 指令没有完成，值得弹窗。
/// </summary>
public enum SftpFailureKind
{
    None,
    DirectoryRead,
    Operation
}

public sealed record SftpDirectoryListing(string Path, IReadOnlyList<RemoteFileItem> Items)
{
    public static SftpDirectoryListing Empty(string path) => new(path, []);
}

public sealed record SftpSessionSnapshot(
    SftpSessionState State,
    SftpDirectoryListing DirectoryListing,
    bool CanNavigate,
    bool CanModifyRemoteFiles,
    bool CanTransfer,
    string StatusMessage,
    string? ErrorMessage,
    SftpFailureKind FailureKind = SftpFailureKind.None)
{
    /// <summary>传输轴。与浏览轴并行，浏览状态不受传输影响。</summary>
    public SftpTransferSnapshot Transfer { get; init; } = SftpTransferSnapshot.None;

    /// <summary>传输队列。展示批量传输中每个文件的状态和进度。</summary>
    public TransferQueue Queue { get; init; } = TransferQueue.Empty;
}

/// <summary>一次传输的确定进度。总量未知时快照里就没有进度，视图退回不确定指示。</summary>
public sealed record SftpTransferProgress(
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond);

/// <summary>下载落地的本地文件系统接缝：存在性检查、输出流、目录创建与残件清理。</summary>
public sealed record DownloadDestination(
    Func<string, bool> FileExists,
    Func<string, Stream> CreateOutput,
    Action<string> CreateDirectory,
    Action<string> DeleteFile)
{
    public Action<string, string> ValidatePath { get; init; } = SftpPathValidator.EnsureSafeDownloadPath;
}

public sealed class SftpSessionController : IDisposable
{
    private readonly ISftpFileService _fileService;
    private readonly ISftpFileService _transferService;
    private readonly Action<Action> _dispatchProgress;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly TransferQueueManager _queueManager;
    private SftpDirectoryListing _directoryListing = SftpDirectoryListing.Empty("/");
    private CancellationTokenSource? _transferCts;
    private TransferControl? _batchControl;
    private bool _disposed;

    public void BeginBatch(TransferControl control)
    {
        if (_batchControl is not null || _transfer.IsActive) throw new InvalidOperationException("已有传输正在进行。");
        _batchControl = control;
        _queueManager.Clear();
        _transfer = SftpTransferSnapshot.None;
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    public void EndBatch()
    {
        _batchControl = null;
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    public Task WaitForTransferAsync(CancellationToken token = default) =>
        _batchControl?.WaitAsync(token) ?? Task.CompletedTask;

    private SftpSessionState _state = SftpSessionState.Idle;
    private string _statusMessage = string.Empty;
    private string? _errorMessage;
    private SftpFailureKind _failureKind = SftpFailureKind.None;
    private SftpTransferSnapshot _transfer = SftpTransferSnapshot.None;

    /// <param name="fileService">浏览轴的远程文件操作：目录列表、重命名、删除、新建。</param>
    /// <param name="transferFileService">
    /// 传输轴的远程文件操作。给独立通道才能边传输边浏览；缺省复用浏览通道（供测试用）。
    /// </param>
    /// <param name="dispatchProgress">
    /// 字节进度回调发生在传输流的写入线程上，与其余一律在调用方线程发布的快照不同，
    /// 必须经此接缝编组回调用方线程。缺省为就地执行（供测试用）。
    /// </param>
    public SftpSessionController(
        ISftpFileService fileService,
        ISftpFileService? transferFileService = null,
        Action<Action>? dispatchProgress = null)
    {
        _fileService = fileService;
        _transferService = transferFileService ?? fileService;
        _dispatchProgress = dispatchProgress ?? (work => work());
        _queueManager = new TransferQueueManager(dispatchProgress);
    }

    public SftpSessionSnapshot Snapshot => CreateSnapshot();

    public event EventHandler<SftpSessionSnapshot>? SnapshotChanged;

    public void CancelTransfer()
    {
        _batchControl?.Cancel();
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
            PublishOperationStatus("当前操作尚未完成。");
            return;
        }

        var targetPath = RemotePath.Normalize(_directoryListing.Path, path);
        await RefreshDirectoryAsync(targetPath);
    }

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
            Transition(SftpSessionState.Deleting, $"正在删除“{item.Name}”…");
            await _fileService.DeleteAsync(item);
            await RefreshDirectoryAsync(_directoryListing.Path, "删除完成。");
        }
        catch (Exception exception)
        {
            FailOperation("删除失败", exception);
        }
    }

    /// <summary>一批文件和目录在同一传输轴上统计、创建目录并上传；浏览不会改变目标。</summary>
    public Task UploadEntriesAsync(IReadOnlyList<SftpUploadEntry> entries,
        Func<string, Task<bool>> confirmOverwrite, string? destinationDirectory = null) =>
        RunTransferAsync("上传", async cancellationToken =>
        {
            var target = destinationDirectory ?? _directoryListing.Path;
            _queueManager.Clear();
            TransitionTransfer(SftpTransferState.Transferring, "正在统计待上传的文件和文件夹…");
            var plan = await SftpUploadPlanner.BuildAsync(entries, WaitForTransferAsync, cancellationToken);
            _queueManager.AddPendingItems(plan.Select(item => (item.RelativePath, item.SizeBytes)));
            SnapshotChanged?.Invoke(this, CreateSnapshot());
            var reporter = new TransferProgressReporter(this, plan.Sum(item => item.SizeBytes));
            var failedDirectories = new List<string>();
            var files = 0;
            var folders = 0;
            OperationOutcome? lastResult = null;
            foreach (var item in plan)
            {
                await WaitForTransferAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (item.Error is not null) throw new IOException(item.Error);
                    if (failedDirectories.Any(path => item.RelativePath.StartsWith(path + "/", StringComparison.Ordinal)))
                        throw new IOException("父文件夹上传失败。");
                    if (item.IsDirectory)
                    {
                        _queueManager.StartTransfer(item.RelativePath);
                        TransitionTransfer(SftpTransferState.Transferring, $"正在创建文件夹 {item.RelativePath}…");
                        var remotePath = RemotePath.Combine(target, item.RelativePath);
                        if (await _transferService.ExistsAsync(remotePath))
                        {
                            // 合并已有文件夹；同名文件不能被当成文件夹，也不删除远程条目。
                            if (!await _transferService.IsDirectoryAsync(remotePath))
                                throw new IOException("远程同名条目是文件，无法合并文件夹。");
                        }
                        else
                        {
                            await WaitForTransferAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            await _transferService.CreateDirectoryAsync(remotePath);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        _queueManager.CompleteTransfer(item.RelativePath);
                        folders++;
                    }
                    else
                    {
                        lastResult = await UploadFileAsync(item.RelativePath, item.File!.OpenRead,
                            confirmOverwrite, target, reporter, cancellationToken);
                        if (lastResult.Succeeded) { reporter.CompleteFile(item.SizeBytes); files++; }
                        else reporter.RemoveFromTotal(item.SizeBytes);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    _queueManager.FailTransfer(item.RelativePath, DescribeError(exception));
                    reporter.RemoveFromTotal(item.SizeBytes);
                    if (item.IsDirectory) failedDirectories.Add(item.RelativePath);
                }
                SnapshotChanged?.Invoke(this, CreateSnapshot());
            }

            var queue = _queueManager.CreateSnapshot();
            if (queue.FailedCount > 0)
                return OperationOutcome.Error($"上传完成，{queue.FailedCount} 项失败，请查看文件明细。");
            if (plan.Count == 1 && !plan[0].IsDirectory && lastResult is not null) return lastResult;
            return OperationOutcome.Success($"已上传 {files} 个文件、{folders} 个文件夹，跳过 {queue.SkippedCount} 个文件。");
        }, refreshDirectory: true, refreshDirectoryOnFailure: true);

    private async Task<OperationOutcome> UploadFileAsync(string relativePath, Func<Task<Stream>> openInput,
        Func<string, Task<bool>> confirmOverwrite, string destinationDirectory,
        TransferProgressReporter reporter, CancellationToken cancellationToken)
    {
        if (!SftpPathValidator.TryValidateUploadRelativePath(relativePath, out var error))
        {
            _queueManager.FailTransfer(relativePath, error);
            return OperationOutcome.Failure(error);
        }

        var remotePath = RemotePath.Combine(destinationDirectory, relativePath);

        _queueManager.StartTransfer(relativePath);
        TransitionTransfer(SftpTransferState.Transferring, $"正在上传 {relativePath}…");

        if (await _transferService.ExistsAsync(remotePath) && !await confirmOverwrite(relativePath))
        {
            _queueManager.SkipTransfer(relativePath);
            SnapshotChanged?.Invoke(this, CreateSnapshot());
            return OperationOutcome.Failure("已跳过现有文件。");
        }

        await WaitForTransferAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = await openInput();
        using var countingStream = new ByteCountingStream(input, bytesRead =>
        {
            reporter.OnCurrentFileBytes(bytesRead);
            _queueManager.UpdateProgress(relativePath, bytesRead, () => SnapshotChanged?.Invoke(this, CreateSnapshot()));
        }, _batchControl, cancellationToken);

        await _transferService.UploadAsync(countingStream, remotePath, cancellationToken);

        _queueManager.CompleteTransfer(relativePath);
        SnapshotChanged?.Invoke(this, CreateSnapshot());

        return OperationOutcome.Success($"已上传 {relativePath}。");
    }

    public Task DownloadAsync(RemoteFileItem item, string destinationDirectory,
        DownloadDestination destination, Func<string, Task<bool>> confirmOverwrite) =>
        DownloadEntriesAsync([item], destinationDirectory, destination, confirmOverwrite);

    /// <summary>文件和文件夹共用一个批次，保留根目录和空目录，失败条目不阻断其他分支。</summary>
    public Task DownloadEntriesAsync(IReadOnlyList<RemoteFileItem> entries, string destinationDirectory,
        DownloadDestination destination, Func<string, Task<bool>> confirmOverwrite) =>
        RunTransferAsync("下载", async cancellationToken =>
        {
            _queueManager.Clear();
            TransitionTransfer(SftpTransferState.Transferring, "正在统计待下载的文件和文件夹…");
            var plan = await SftpDownloadPlanner.BuildAsync(entries, destinationDirectory, _transferService,
                WaitForTransferAsync, (path, count) => TransitionTransfer(SftpTransferState.Transferring,
                    $"正在统计 {path}…（已发现 {count} 项）"), cancellationToken);
            _queueManager.AddPendingItems(plan.Select(item => (item.RelativePath, item.SizeBytes)));
            SnapshotChanged?.Invoke(this, CreateSnapshot());
            var reporter = new TransferProgressReporter(this, plan.Sum(item => item.SizeBytes));
            var failedDirectories = new List<string>();
            var files = 0;
            var folders = 0;
            foreach (var item in plan)
            {
                await WaitForTransferAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var outputCreated = false;
                try
                {
                    if (item.Error is not null) throw new IOException(item.Error);
                    if (failedDirectories.Any(path => item.RelativePath.StartsWith(path + "/", StringComparison.Ordinal)))
                        throw new IOException("父文件夹下载失败。");
                    _queueManager.StartTransfer(item.RelativePath);
                    if (item.IsDirectory)
                    {
                        TransitionTransfer(SftpTransferState.Transferring, $"正在创建文件夹 {item.RelativePath}…");
                        await Task.Run(() =>
                        {
                            destination.ValidatePath(destinationDirectory, item.LocalPath);
                            destination.CreateDirectory(item.LocalPath);
                        }, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        _queueManager.CompleteTransfer(item.RelativePath);
                        folders++;
                    }
                    else
                    {
                        var exists = await Task.Run(() =>
                        {
                            destination.ValidatePath(destinationDirectory, item.LocalPath);
                            return destination.FileExists(item.LocalPath);
                        }, cancellationToken);
                        if (exists && !await confirmOverwrite(item.RelativePath))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _queueManager.SkipTransfer(item.RelativePath);
                            reporter.RemoveFromTotal(item.SizeBytes);
                            continue;
                        }
                        await WaitForTransferAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        TransitionTransfer(SftpTransferState.Transferring, $"正在下载 {item.RelativePath}…");
                        var localOutput = await Task.Run(() =>
                        {
                            // 覆盖确认期间目录可能已变成链接，因此打开前再次检查。
                            destination.ValidatePath(destinationDirectory, item.LocalPath);
                            return destination.CreateOutput(item.LocalPath);
                        }, cancellationToken);
                        outputCreated = true;
                        using (var output = new ByteCountingStream(localOutput, bytes =>
                        {
                            reporter.OnCurrentFileBytes(bytes);
                            _queueManager.UpdateProgress(item.RelativePath, bytes,
                                () => SnapshotChanged?.Invoke(this, CreateSnapshot()));
                        }, _batchControl, cancellationToken))
                        {
                            await _transferService.DownloadAsync(item.RemotePath, output, cancellationToken);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        _queueManager.CompleteTransfer(item.RelativePath);
                        reporter.CompleteFile(item.SizeBytes);
                        files++;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (outputCreated && _batchControl?.PreservePartialFiles != true)
                        await DeleteDownloadPartialAsync(destination, destinationDirectory, item.LocalPath);
                    throw;
                }
                catch (Exception exception)
                {
                    if (outputCreated && _batchControl?.PreservePartialFiles != true)
                        await DeleteDownloadPartialAsync(destination, destinationDirectory, item.LocalPath);
                    _queueManager.FailTransfer(item.RelativePath, DescribeError(exception));
                    reporter.RemoveFromTotal(item.SizeBytes);
                    if (item.IsDirectory) failedDirectories.Add(item.RelativePath);
                }
                finally { SnapshotChanged?.Invoke(this, CreateSnapshot()); }
            }

            var queue = _queueManager.CreateSnapshot();
            var summary = $"已下载 {files} 个文件、{folders} 个文件夹，跳过 {queue.SkippedCount} 个文件。";
            if (queue.FailedCount > 0)
            {
                var first = queue.Items.First(item => item.State == TransferItemState.Failed);
                return OperationOutcome.Error($"{summary}{queue.FailedCount} 个失败，首个失败：{first.RelativePath}（{first.ErrorMessage}）。");
            }
            if (plan.Count == 1 && !plan[0].IsDirectory)
                return OperationOutcome.Success(queue.SkippedCount > 0 ? "已保留现有文件。" : $"已下载 {plan[0].RelativePath}。");
            return OperationOutcome.Success(summary);
        }, refreshDirectory: false);

    private static Task DeleteDownloadPartialAsync(DownloadDestination destination, string root, string path) =>
        Task.Run(() =>
        {
            try
            {
                destination.ValidatePath(root, path);
                destination.DeleteFile(path);
            }
            catch { /* 尽力清理残件；路径已被换成链接时不要删除链接目标。 */ }
        });

    private void PublishTransferProgress(SftpTransferProgress progress)
    {
        // 编组过来的进度可能晚于传输收尾到达，传输已结束就丢弃。
        if (!_transfer.IsActive) return;
        _transfer = _transfer with { Progress = progress };
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    private async Task RefreshCurrentDirectoryAsync()
    {
        if (!_fileService.IsConnected)
        {
            FailDirectoryRead("SFTP 尚未连接。");
            return;
        }
        if (!CanNavigate())
        {
            PublishOperationStatus("当前操作尚未完成。");
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
        bool refreshDirectory,
        bool refreshDirectoryOnFailure = false)
    {
        if (_disposed || !CanStartTransfer())
        {
            PublishOperationStatus(_transfer.IsActive ? "已有传输正在进行。" : "SFTP 尚未连接。");
            return;
        }

        await _transferGate.WaitAsync();
        _transferCts = CancellationTokenSource.CreateLinkedTokenSource(_batchControl?.Token ?? CancellationToken.None);
        TransitionTransfer(SftpTransferState.Transferring, $"正在{action}…");
        try
        {
            await WaitForTransferAsync(_transferCts.Token);
            _transferCts.Token.ThrowIfCancellationRequested();
            var result = await operation(_transferCts.Token);
            _transferCts.Token.ThrowIfCancellationRequested();
            if (!result.Succeeded)
            {
                // Error 是部分完成、值得弹窗解释的结果；普通 Failure（校验、用户拒绝）安静收尾。
                TransitionTransfer(
                    result.IsError ? SftpTransferState.Failed : SftpTransferState.Completed,
                    result.Message);
                PublishOperationStatus(result.Message);
                return;
            }

            TransitionTransfer(SftpTransferState.Completed, result.Message);
            if (refreshDirectory && CanNavigate())
                await RefreshDirectoryAsync(_directoryListing.Path, result.Message);
            else
                PublishOperationStatus(result.Message);
        }
        catch (OperationCanceledException)
        {
            _queueManager.FailUnfinished("已取消");
            TransitionTransfer(SftpTransferState.Cancelled, $"{action}已取消。");
            PublishOperationStatus($"{action}已取消。");
        }
        catch (Exception exception)
        {
            var message = $"{action}失败：{DescribeError(exception)}";
            _queueManager.FailUnfinished(message);
            TransitionTransfer(SftpTransferState.Failed, message);
            PublishOperationStatus(message);
        }
        finally
        {
            // 批量上传可能已创建部分目录/文件，失败或取消后也要让当前浏览目录看到结果。
            if (refreshDirectoryOnFailure && (_transfer.State is SftpTransferState.Failed or SftpTransferState.Cancelled)
                && _fileService.IsConnected && CanNavigate())
                await RefreshDirectoryAsync(_directoryListing.Path, _transfer.Message);
            _transferCts.Dispose();
            _transferCts = null;
            _transferGate.Release();
        }
    }

    private void FailDirectoryRead(string message) =>
        Transition(SftpSessionState.Failed, message, message, SftpFailureKind.DirectoryRead);

    private void FailOperation(string action, Exception exception)
    {
        var message = $"{action}：{DescribeError(exception)}";
        Transition(SftpSessionState.Failed, message, message, SftpFailureKind.Operation);
    }

    /// <summary>SFTP 协议把一类拒绝统一报成一句 "Failure"，翻译成能行动的提示。</summary>
    private static string DescribeError(Exception exception) =>
        exception.Message == "Failure"
            ? "远程主机拒绝了该操作（常见于权限不足、目录非空或符号链接等特殊文件）"
            : exception.Message;

    private void PublishOperationStatus(string message) =>
        Transition(_state, message);

    private bool CanNavigate() => _state is not (SftpSessionState.ListingDirectory or SftpSessionState.Deleting);
    private bool CanModifyRemoteFiles() => CanNavigate() && _fileService.IsConnected;
    private bool CanStartTransfer() => !_transfer.IsActive && _transferService.IsConnected;

    private void Transition(
        SftpSessionState state,
        string statusMessage,
        string? errorMessage = null,
        SftpFailureKind failureKind = SftpFailureKind.None)
    {
        _state = state;
        _statusMessage = statusMessage;
        _errorMessage = errorMessage;
        _failureKind = state == SftpSessionState.Failed ? failureKind : SftpFailureKind.None;
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    private void TransitionTransfer(SftpTransferState state, string message)
    {
        // 传输途中的消息更新（逐文件、统计心跳）保留进度；进入终态即清空。
        var progress = state == SftpTransferState.Transferring ? _transfer.Progress : null;
        _transfer = new SftpTransferSnapshot(state, message, progress);
        SnapshotChanged?.Invoke(this, CreateSnapshot());
    }

    private SftpSessionSnapshot CreateSnapshot() =>
        new(
            _state,
            _directoryListing,
            CanNavigate(),
            CanModifyRemoteFiles(),
            CanStartTransfer() && _batchControl is null,
            _statusMessage,
            _errorMessage,
            _failureKind)
        {
            Transfer = _transfer,
            Queue = _queueManager.CreateSnapshot()
        };

    public void Dispose()
    {
        _disposed = true;
        CancelTransfer();
        // An in-flight operation owns CTS disposal and releases its gate in finally.
        // Disposing either here races paused/cancelled continuations.
    }

    private sealed record OperationOutcome(bool Succeeded, string Message, bool IsError = false)
    {
        public static OperationOutcome Success(string message) => new(true, message);
        public static OperationOutcome Failure(string message) => new(false, message);
        public static OperationOutcome Error(string message) => new(false, message, IsError: true);
    }

    /// <summary>
    /// 把逐字节回调折算成整数百分比变化才发布的进度。
    /// <see cref="OnCurrentFileBytes"/> 在传输流的写入线程上被调，发布经 _dispatchProgress 编组。
    /// </summary>
    private sealed class TransferProgressReporter
    {
        private readonly SftpSessionController _owner;
        private readonly Queue<(DateTime Time, long Bytes)> _speedSamples;
        private const int SpeedWindowSeconds = 5;
        private long _totalBytes;
        private long _completedBytes;
        private int _lastPercent = -1;

        public TransferProgressReporter(SftpSessionController owner, long totalBytes)
        {
            _owner = owner;
            _totalBytes = totalBytes;
            _speedSamples = new Queue<(DateTime, long)>();
        }

        public void OnCurrentFileBytes(long currentFileBytes) =>
            Publish(_completedBytes + currentFileBytes);

        public void CompleteFile(long sizeBytes)
        {
            _completedBytes += sizeBytes;
            Publish(_completedBytes);
        }

        /// <summary>用户拒绝覆盖后从总量里剔除该文件，进度条不为跳过的字节停留。</summary>
        public void RemoveFromTotal(long sizeBytes)
        {
            _totalBytes -= sizeBytes;
            Publish(_completedBytes);
        }

        private void Publish(long transferred)
        {
            var total = _totalBytes;
            if (total <= 0) return; // 总量未知：不发布进度，视图保持不确定指示。

            var percent = (int)Math.Min(100, transferred * 100 / total);
            if (Interlocked.Exchange(ref _lastPercent, percent) == percent) return;

            var now = DateTime.UtcNow;
            var bytesPerSecond = CalculateSpeed(now, transferred);

            var progress = new SftpTransferProgress(
                transferred,
                total,
                bytesPerSecond);
            _owner._dispatchProgress(() => _owner.PublishTransferProgress(progress));
        }

        private double CalculateSpeed(DateTime now, long transferred)
        {
            // 添加当前样本到窗口
            _speedSamples.Enqueue((now, transferred));

            // 移除超出窗口的旧样本（保留最近 5 秒）
            while (_speedSamples.Count > 0)
            {
                var oldest = _speedSamples.Peek();
                if ((now - oldest.Time).TotalSeconds <= SpeedWindowSeconds)
                    break;
                _speedSamples.Dequeue();
            }

            // 需要至少 2 个样本才能计算速度
            if (_speedSamples.Count < 2)
                return 0;

            var earliest = _speedSamples.Peek();
            var elapsedSeconds = (now - earliest.Time).TotalSeconds;

            // 时间跨度太短（< 0.5 秒），样本不足以计算可靠速度
            if (elapsedSeconds < 0.5)
                return 0;

            var bytesDelta = transferred - earliest.Bytes;
            return bytesDelta / elapsedSeconds;
        }
    }
}
