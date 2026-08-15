namespace FluentShell.Core;

/// <summary>传输队列中单个项的状态。</summary>
public enum TransferItemState
{
    Pending,
    Transferring,
    Completed,
    Skipped,
    Failed
}

/// <summary>传输队列中的单个文件项。</summary>
public sealed record TransferQueueItem(
    string FileName,
    string RelativePath,
    long SizeBytes,
    TransferItemState State,
    long BytesTransferred = 0,
    string? ErrorMessage = null)
{
    /// <summary>传输进度百分比（0-100）。</summary>
    public double PercentComplete => SizeBytes <= 0 ? 0 : Math.Min(100d, BytesTransferred * 100d / SizeBytes);

    public TransferQueueItem WithState(TransferItemState state, string? error = null) =>
        this with { State = state, ErrorMessage = error };

    public TransferQueueItem WithProgress(long bytesTransferred) =>
        this with { BytesTransferred = bytesTransferred };
}

/// <summary>传输队列的完整状态快照，供视图展示。</summary>
public sealed record TransferQueue(
    IReadOnlyList<TransferQueueItem> Items,
    int TotalCount,
    int CompletedCount,
    int SkippedCount,
    int FailedCount)
{
    public static readonly TransferQueue Empty = new([], 0, 0, 0, 0);

    public int PendingCount => TotalCount - CompletedCount - SkippedCount - FailedCount;
    public bool HasItems => TotalCount > 0;
    public bool IsCompleted => TotalCount > 0 && PendingCount == 0 && !Items.Any(i => i.State == TransferItemState.Transferring);
}
