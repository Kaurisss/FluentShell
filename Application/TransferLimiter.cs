namespace FluentShell.Core;

/// <summary>Global batch limit; each session still uses its own serial transfer channel.</summary>
public sealed class TransferLimiter
{
    private readonly object _sync = new();
    private int _limit = 3;
    private int _active;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetLimit(int value)
    {
        lock (_sync) { _limit = Math.Clamp(value, 1, 8); Pulse(); }
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_active < _limit) { _active++; return new Lease(this); }
                wait = _changed.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    private void Pulse() { var previous = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
    private sealed class Lease(TransferLimiter owner) : IDisposable
    {
        private TransferLimiter? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._sync) { current._active--; current.Pulse(); }
        }
    }
}
