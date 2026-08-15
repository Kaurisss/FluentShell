using System.Collections.Concurrent;

namespace FluentShell.Core;

/// <summary>
/// 管理传输队列的构建、更新和快照生成。线程安全，支持并发更新。
/// </summary>
/// <remarks>
/// 在传输开始前收集所有文件信息构建队列快照，在传输过程中更新每项的状态和进度。
/// 使用 ConcurrentDictionary 确保传输线程和 UI 线程的并发安全。
/// </remarks>
public sealed class TransferQueueManager
{
    private readonly ConcurrentDictionary<string, TransferQueueItem> _items = new();
    private readonly Action<Action> _dispatch;

    public TransferQueueManager(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (work => work());
    }

    /// <summary>创建队列快照供视图使用。在 UI 线程上调用。</summary>
    public TransferQueue CreateSnapshot()
    {
        var items = _items.Values.OrderBy(i => i.RelativePath).ToList();
        var completed = items.Count(i => i.State == TransferItemState.Completed);
        var skipped = items.Count(i => i.State == TransferItemState.Skipped);
        var failed = items.Count(i => i.State == TransferItemState.Failed);

        return new TransferQueue(items, items.Count, completed, skipped, failed);
    }

    /// <summary>清空队列，准备新的传输批次。</summary>
    public void Clear() => _items.Clear();

    /// <summary>批量添加待传输的文件到队列（传输前的构建阶段）。</summary>
    public void AddPendingItems(IEnumerable<(string FileName, string RelativePath, long SizeBytes)> files)
    {
        foreach (var (fileName, relativePath, sizeBytes) in files)
        {
            var item = new TransferQueueItem(
                fileName,
                relativePath,
                sizeBytes,
                TransferItemState.Pending);
            _items[relativePath] = item;
        }
    }

    /// <summary>添加单个待传输文件（用于逐文件添加场景，如上传多个本地文件）。</summary>
    public void AddPendingItem(string fileName, string relativePath, long sizeBytes)
    {
        var item = new TransferQueueItem(fileName, relativePath, sizeBytes, TransferItemState.Pending);
        _items[relativePath] = item;
    }

    /// <summary>标记文件开始传输。</summary>
    public void StartTransfer(string relativePath)
    {
        if (_items.TryGetValue(relativePath, out var item))
        {
            _items[relativePath] = item.WithState(TransferItemState.Transferring);
        }
    }

    /// <summary>更新文件传输进度（从传输流线程调用，经 dispatch 编组到 UI 线程）。</summary>
    public void UpdateProgress(string relativePath, long bytesTransferred, Action onSnapshotChanged)
    {
        if (_items.TryGetValue(relativePath, out var item))
        {
            _items[relativePath] = item.WithProgress(bytesTransferred);
            _dispatch(() => onSnapshotChanged());
        }
    }

    /// <summary>标记文件传输完成。</summary>
    public void CompleteTransfer(string relativePath)
    {
        if (_items.TryGetValue(relativePath, out var item))
        {
            _items[relativePath] = item.WithState(TransferItemState.Completed);
        }
    }

    /// <summary>标记文件被跳过（如用户拒绝覆盖）。</summary>
    public void SkipTransfer(string relativePath)
    {
        if (_items.TryGetValue(relativePath, out var item))
        {
            _items[relativePath] = item.WithState(TransferItemState.Skipped);
        }
    }

    /// <summary>标记文件传输失败。</summary>
    public void FailTransfer(string relativePath, string errorMessage)
    {
        if (_items.TryGetValue(relativePath, out var item))
        {
            _items[relativePath] = item.WithState(TransferItemState.Failed, errorMessage);
        }
    }

    /// <summary>获取队列中的统计信息（用于生成汇总消息）。</summary>
    public (int Total, int Completed, int Skipped, int Failed) GetStatistics()
    {
        var items = _items.Values;
        return (
            items.Count,
            items.Count(i => i.State == TransferItemState.Completed),
            items.Count(i => i.State == TransferItemState.Skipped),
            items.Count(i => i.State == TransferItemState.Failed)
        );
    }
}
