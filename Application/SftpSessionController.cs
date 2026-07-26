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
    bool IsBusy,
    bool CanNavigate,
    bool CanModifyRemoteFiles,
    bool CanTransfer,
    string StatusMessage,
    string? ErrorMessage,
    SftpFailureKind FailureKind = SftpFailureKind.None,
    SftpTransferProgress? TransferProgress = null)
{
    public string CurrentPath => DirectoryListing.Path;
}

/// <summary>一次传输的确定进度。总量未知时快照里就没有进度，视图退回不确定指示。</summary>
public sealed record SftpTransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Percent => TotalBytes <= 0 ? 0 : Math.Min(100d, BytesTransferred * 100d / TotalBytes);
}

/// <summary>下载落地的本地文件系统接缝：存在性检查、输出流、目录创建与残件清理。</summary>
public sealed record DownloadDestination(
    Func<string, bool> FileExists,
    Func<string, Stream> CreateOutput,
    Action<string> CreateDirectory,
    Action<string> DeleteFile);

public sealed class SftpSessionController : IDisposable
{
    private const int MaxDownloadDepth = 32;

    private readonly ISftpFileService _fileService;
    private readonly Action<Action> _dispatchProgress;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private SftpDirectoryListing _directoryListing = SftpDirectoryListing.Empty("/");
    private CancellationTokenSource? _transferCts;
    private SftpSessionState _state = SftpSessionState.Idle;
    private string _statusMessage = string.Empty;
    private string? _errorMessage;
    private SftpFailureKind _failureKind = SftpFailureKind.None;
    private SftpTransferProgress? _transferProgress;

    /// <param name="fileService">远程文件操作。</param>
    /// <param name="dispatchProgress">
    /// 字节进度回调发生在传输流的写入线程上，与其余一律在调用方线程发布的快照不同，
    /// 必须经此接缝编组回调用方线程。缺省为就地执行（供测试用）。
    /// </param>
    public SftpSessionController(ISftpFileService fileService, Action<Action>? dispatchProgress = null)
    {
        _fileService = fileService;
        _dispatchProgress = dispatchProgress ?? (work => work());
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
        DownloadDestination destination,
        Func<string, Task<bool>> confirmOverwrite) =>
        RunTransferAsync("下载", async cancellationToken =>
        {
            if (!SftpPathValidator.TryResolveDownloadPath(
                    destinationDirectory,
                    item.Name,
                    out var localPath,
                    out var error))
            {
                return OperationOutcome.Failure(error);
            }

            if (!item.IsDirectory)
            {
                if (destination.FileExists(localPath) && !await confirmOverwrite(item.Name))
                    return OperationOutcome.Failure("已保留现有文件。");

                var reporter = new TransferProgressReporter(this, Math.Max(0, item.SizeBytes));
                try
                {
                    using var output = new ByteCountingStream(
                        destination.CreateOutput(localPath),
                        reporter.OnCurrentFileBytes);
                    await _fileService.DownloadAsync(item.FullPath, output, cancellationToken);
                }
                catch
                {
                    // 无论取消还是出错，半截文件都不该冒充下载成功。
                    TryDeletePartialFile(destination, localPath);
                    throw;
                }
                return OperationOutcome.Success($"已下载 {item.Name}。");
            }

            // 目录先统计再传输：总量已知，进度才能是确定的。统计阶段快照没有进度，
            // 视图在这段时间退回不确定指示。
            Transition(SftpSessionState.Transferring, $"正在统计 {item.Name} 中的文件…");
            var plan = new List<PlannedDownload>();
            var collectFailure = await CollectDownloadPlanAsync(
                item.FullPath,
                item.Name,
                localPath,
                depth: 0,
                destination,
                plan,
                cancellationToken);
            if (collectFailure is not null) return collectFailure;

            var directoryReporter = new TransferProgressReporter(this, plan.Sum(file => file.SizeBytes));
            var files = 0;
            var skipped = 0;
            var failures = new List<string>();
            foreach (var file in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (destination.FileExists(file.LocalPath) && !await confirmOverwrite(file.RelativePath))
                {
                    skipped++;
                    directoryReporter.RemoveFromTotal(file.SizeBytes);
                    continue;
                }

                Transition(SftpSessionState.Transferring, $"正在下载 {file.RelativePath}…");
                try
                {
                    using var output = new ByteCountingStream(
                        destination.CreateOutput(file.LocalPath),
                        directoryReporter.OnCurrentFileBytes);
                    await _fileService.DownloadAsync(file.RemotePath, output, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    TryDeletePartialFile(destination, file.LocalPath);
                    throw;
                }
                catch (Exception exception)
                {
                    // 单个条目失败（符号链接、特殊文件、权限）不拖垮整批，记下继续。
                    TryDeletePartialFile(destination, file.LocalPath);
                    failures.Add($"{file.RelativePath}（{DescribeError(exception)}）");
                    directoryReporter.RemoveFromTotal(file.SizeBytes);
                    continue;
                }

                directoryReporter.CompleteFile(file.SizeBytes);
                files++;
            }

            var parts = new List<string> { $"已下载 {files} 个文件" };
            if (skipped > 0) parts.Add($"跳过 {skipped} 个");
            if (failures.Count > 0) parts.Add($"{failures.Count} 个失败");
            var summary = string.Join("，", parts) + "。";
            return failures.Count > 0
                ? OperationOutcome.Error($"{summary}首个失败：{failures[0]}。")
                : OperationOutcome.Success(summary);
        }, refreshDirectory: false);

    /// <summary>
    /// 递归收集一个远程目录的下载计划，并沿途创建本地目录（空目录也要落地）。
    /// 每个条目名都经 <see cref="SftpPathValidator"/> 校验后才落到本地路径上——
    /// 目录列表来自远程主机，条目名不可信。返回 <c>null</c> 表示收集完成。
    /// </summary>
    private async Task<OperationOutcome?> CollectDownloadPlanAsync(
        string remotePath,
        string relativePath,
        string localDirectory,
        int depth,
        DownloadDestination destination,
        List<PlannedDownload> plan,
        CancellationToken cancellationToken)
    {
        // 远程符号链接可能构成环；层级上限把环变成一条明确的失败消息而不是无限递归。
        if (depth > MaxDownloadDepth)
            return OperationOutcome.Failure($"“{relativePath}”层级过深，可能存在循环链接。");

        // 大目录树的统计要走完整棵树，逐目录汇报发现数，别让用户以为卡死了。
        Transition(SftpSessionState.Transferring, $"正在统计 {relativePath}…（已发现 {plan.Count} 个文件）");
        destination.CreateDirectory(localDirectory);
        var entries = await _fileService.ListDirectoryAsync(remotePath);
        foreach (var entry in entries)
        {
            // 目录列表为呈现合成的父目录条目，不属于目录内容。
            if (entry.Name == "..") continue;

            cancellationToken.ThrowIfCancellationRequested();
            var entryRelativePath = $"{relativePath}/{entry.Name}";
            if (!SftpPathValidator.TryResolveDownloadPath(
                    localDirectory,
                    entry.Name,
                    out var entryLocalPath,
                    out var error))
            {
                return OperationOutcome.Failure($"“{entryRelativePath}”：{error}");
            }

            if (entry.IsDirectory)
            {
                var failure = await CollectDownloadPlanAsync(
                    entry.FullPath,
                    entryRelativePath,
                    entryLocalPath,
                    depth + 1,
                    destination,
                    plan,
                    cancellationToken);
                if (failure is not null) return failure;
                continue;
            }

            plan.Add(new PlannedDownload(
                entry.FullPath,
                entryLocalPath,
                entryRelativePath,
                Math.Max(0, entry.SizeBytes)));
        }

        return null;
    }

    private void PublishTransferProgress(SftpTransferProgress progress)
    {
        // 编组过来的进度可能晚于传输收尾到达，传输已结束就丢弃。
        if (_state != SftpSessionState.Transferring) return;
        _transferProgress = progress;
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
                // Error 是部分完成、值得弹窗解释的结果；普通 Failure（校验、用户拒绝）安静收尾。
                if (result.IsError)
                    Transition(SftpSessionState.Failed, result.Message, result.Message, SftpFailureKind.Operation);
                else
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

    /// <summary>尽力清掉失败或取消留下的半截文件，清不掉也不额外报错。</summary>
    private static void TryDeletePartialFile(DownloadDestination destination, string localPath)
    {
        try
        {
            destination.DeleteFile(localPath);
        }
        catch
        {
        }
    }

    private void PublishOperationStatus(string message) =>
        Transition(_state, message);

    private bool CanNavigate() => _state is not SftpSessionState.ListingDirectory and not SftpSessionState.Transferring;
    private bool CanModifyRemoteFiles() => CanNavigate() && _fileService.IsConnected;
    private bool CanStartTransfer() => CanNavigate() && _fileService.IsConnected;

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
        // 传输途中的状态更新（逐文件消息）保留进度；离开传输态即清空。
        if (state != SftpSessionState.Transferring) _transferProgress = null;
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
            _errorMessage,
            _failureKind,
            _transferProgress);

    public void Dispose()
    {
        _transferCts?.Cancel();
        _transferCts?.Dispose();
        _transferGate.Dispose();
    }

    private sealed record OperationOutcome(bool Succeeded, string Message, bool IsError = false)
    {
        public static OperationOutcome Success(string message) => new(true, message);
        public static OperationOutcome Failure(string message) => new(false, message);
        public static OperationOutcome Error(string message) => new(false, message, IsError: true);
    }

    private sealed record PlannedDownload(
        string RemotePath,
        string LocalPath,
        string RelativePath,
        long SizeBytes);

    /// <summary>
    /// 把逐字节回调折算成整数百分比变化才发布的进度。
    /// <see cref="OnCurrentFileBytes"/> 在传输流的写入线程上被调，发布经 _dispatchProgress 编组。
    /// </summary>
    private sealed class TransferProgressReporter
    {
        private readonly SftpSessionController _owner;
        private long _totalBytes;
        private long _completedBytes;
        private int _lastPercent = -1;

        public TransferProgressReporter(SftpSessionController owner, long totalBytes)
        {
            _owner = owner;
            _totalBytes = totalBytes;
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

            var progress = new SftpTransferProgress(transferred, total);
            _owner._dispatchProgress(() => _owner.PublishTransferProgress(progress));
        }
    }
}
