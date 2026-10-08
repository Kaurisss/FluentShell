using System.Collections.ObjectModel;
using System.ComponentModel;

namespace FluentShell.Core;

public enum TransferTaskState { Running, Paused, Completed, Failed, Disconnected, Discarded }
public enum TransferTaskKind { File, Folder, Batch }

/// <summary>Window-scoped transfer history. Mutations and commands run on the shell dispatcher.</summary>
public sealed class TransferCenter
{
    public TransferLimiter Limiter { get; } = new();
    public event EventHandler<TransferTask>? Completed;
    private readonly HashSet<TransferTask> _completed = [];
    public ObservableCollection<TransferConnectionGroup> Groups { get; } = [];
    public event EventHandler? Changed;
    public int RunningCount => Groups.SelectMany(g => g).Count(t => t.State == TransferTaskState.Running);
    public int FailedCount => Groups.SelectMany(g => g).Count(t => t.State is TransferTaskState.Failed or TransferTaskState.Disconnected);

    public TransferTask Add(Guid connectionId, string connectionLabel, string direction, string title,
        string target, Func<Task> retry, Func<bool> canRetry, TransferTaskKind kind = TransferTaskKind.File)
    {
        var group = Groups.FirstOrDefault(g => g.ConnectionId == connectionId);
        if (group is null)
        {
            group = new(connectionId, connectionLabel);
            Groups.Add(group);
        }
        var task = new TransferTask(direction, title, target, retry, canRetry, kind);
        task.PropertyChanged += TaskChanged;
        group.Add(task);
        NotifyChanged();
        return task;
    }

    public void Discard(TransferTask task)
    {
        _completed.Remove(task);
        task.Discard();
        task.PropertyChanged -= TaskChanged;
        foreach (var group in Groups.ToArray())
        {
            group.Remove(task);
            if (group.Count == 0) Groups.Remove(group);
        }
        NotifyChanged();
    }

    public void Detach(Guid connectionId)
    {
        foreach (var task in Groups.Where(g => g.ConnectionId == connectionId).SelectMany(g => g))
            task.Detach();
        NotifyChanged();
    }

    public void RefreshCommands()
    {
        foreach (var task in Groups.SelectMany(g => g)) task.NotifyChanged();
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TransferTask { State: TransferTaskState.Completed } task && _completed.Add(task))
            Completed?.Invoke(this, task);
        NotifyChanged();
    }
    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class TransferConnectionGroup(Guid connectionId, string label) : ObservableCollection<TransferTask>
{
    public Guid ConnectionId { get; } = connectionId;
    public string Label { get; } = label;
}

public sealed class TransferTask : INotifyPropertyChanged
{
    private Func<Task>? _retry;
    private Func<bool>? _canRetry;
    private TransferControl? _control;
    private SftpTransferProgress? _progress;
    private bool _retrying;
    public string Direction { get; }
    public string Title { get; }
    public string Target { get; }
    // The selected root's kind is stable while scanning, transferring and retrying.
    public TransferTaskKind Kind { get; }
    public TransferTaskState State { get; private set; } = TransferTaskState.Running;
    public string Message { get; private set; } = "正在准备传输…";
    public TransferQueue Queue { get; private set; } = TransferQueue.Empty;
    public ObservableCollection<TransferFileProgress> Files { get; } = [];
    public bool IsActive => State is TransferTaskState.Running or TransferTaskState.Paused;
    public bool CanPause => IsActive && _control is not null;
    public bool CanRetry => State == TransferTaskState.Failed && !_retrying && _retry is not null && _canRetry?.Invoke() == true;
    public string PauseLabel => State == TransferTaskState.Paused ? "继续" : "暂停";
    public string StateLabel => State switch
    {
        TransferTaskState.Paused => "已暂停",
        TransferTaskState.Completed => "已完成",
        TransferTaskState.Failed => "失败",
        TransferTaskState.Disconnected => "连接已断开",
        TransferTaskState.Discarded => "已丢弃",
        _ => "传输中"
    };
    public string FileSummary => $"文件明细 · {Queue.CompletedCount}/{Queue.TotalCount} 已完成 · {Queue.SkippedCount} 跳过 · {Queue.FailedCount} 失败";
    public bool HasFiles => Queue.HasItems;
    public long TotalBytes => Queue.HasItems ? Queue.Items.Sum(i => Math.Max(0, i.SizeBytes)) : _progress?.TotalBytes ?? 0;
    public long TransferredBytes => Queue.HasItems ? Queue.Items.Sum(i => Math.Max(0, i.BytesTransferred)) : _progress?.BytesTransferred ?? 0;
    public double Percent => TotalBytes > 0 ? Math.Min(100d, TransferredBytes * 100d / TotalBytes) : 0;
    public bool IsIndeterminate => State == TransferTaskState.Running && TotalBytes == 0;
    public string ProgressLabel => $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}" +
        (State == TransferTaskState.Running && _progress?.BytesPerSecond > 0
            ? $"  ·  {FormatBytes((long)_progress.BytesPerSecond)}/s" : string.Empty);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal TransferTask(string direction, string title, string target, Func<Task> retry, Func<bool> canRetry, TransferTaskKind kind)
    {
        Direction = direction;
        Title = title;
        Target = target;
        Kind = kind;
        _retry = retry;
        _canRetry = canRetry;
    }

    internal void Start(TransferControl control)
    {
        _control = control;
        State = TransferTaskState.Running;
        Message = "正在准备传输…";
        Queue = TransferQueue.Empty;
        Files.Clear();
        _progress = null;
        NotifyChanged();
    }

    internal void Update(SftpSessionSnapshot snapshot)
    {
        if (!IsActive) return;
        Queue = snapshot.Queue;
        UpdateFiles(Queue.Items);
        _progress = snapshot.Transfer.Progress;
        if (!string.IsNullOrEmpty(snapshot.Transfer.Message)) Message = snapshot.Transfer.Message;
        NotifyChanged();
    }

    internal void UpdateFiles(IReadOnlyList<TransferQueueItem> items)
    {
        // Snapshots are immutable, but list rows must retain identity across progress ticks.
        var wanted = items.Select(i => i.RelativePath).ToHashSet(StringComparer.Ordinal);
        for (var i = Files.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Files[i].RelativePath)) Files.RemoveAt(i);
        var existing = Files.ToDictionary(i => i.RelativePath, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (existing.TryGetValue(item.RelativePath, out var row))
            {
                if (!ReferenceEquals(Files[i], row)) Files.Move(Files.IndexOf(row), i);
                row.Update(item);
            }
            else Files.Insert(i, new TransferFileProgress(item));
        }
    }

    internal void Finish(bool failed, string? message = null)
    {
        _control = null;
        if (!IsActive) return;
        State = failed ? TransferTaskState.Failed : TransferTaskState.Completed;
        if (message is not null) Message = message;
        // Successful history has no reason to retain picker files or a workspace closure.
        if (!failed) { _retry = null; _canRetry = null; }
        NotifyChanged();
    }

    public void TogglePause()
    {
        if (!CanPause) return;
        if (State == TransferTaskState.Paused)
        {
            _control!.Resume();
            State = TransferTaskState.Running;
        }
        else
        {
            _control!.Pause();
            State = TransferTaskState.Paused;
        }
        NotifyChanged();
    }

    public async Task RetryAsync()
    {
        if (!CanRetry) return;
        var retry = _retry!;
        _retrying = true;
        NotifyChanged();
        try { await retry(); }
        finally { _retrying = false; NotifyChanged(); }
    }

    internal void Disconnect()
    {
        if (!IsActive) return;
        _control?.Cancel();
        State = TransferTaskState.Disconnected;
        Message = "连接已断开，请重新连接后重新发起传输。";
        _retry = null;
        _canRetry = null;
        NotifyChanged();
    }

    internal void Discard()
    {
        _control?.Cancel(preservePartialFiles: true);
        State = TransferTaskState.Discarded;
        _retry = null;
        _canRetry = null;
        NotifyChanged();
    }

    internal void Detach()
    {
        Disconnect();
        _retry = null;
        _canRetry = null;
        NotifyChanged();
    }

    internal void NotifyChanged() => PropertyChanged?.Invoke(this, new(string.Empty));
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576d:0.0} MB",
        _ => $"{bytes / 1073741824d:0.0} GB"
    };
}

/// <summary>Stable, UI-thread-owned presentation of an immutable file snapshot.</summary>
public sealed class TransferFileProgress(TransferQueueItem item) : INotifyPropertyChanged
{
    private TransferQueueItem _item = item;
    public string RelativePath => _item.RelativePath;
    public string StatusLabel => _item.StatusLabel;
    public string? ErrorMessage => _item.ErrorMessage;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(TransferQueueItem item)
    {
        var previous = _item;
        _item = item;
        if (previous.StatusLabel != item.StatusLabel)
            PropertyChanged?.Invoke(this, new(nameof(StatusLabel)));
        if (previous.ErrorMessage != item.ErrorMessage)
            PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
    }
}
