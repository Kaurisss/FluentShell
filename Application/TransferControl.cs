namespace FluentShell.Core;

/// <summary>Cooperative pause at stream/block boundaries. The open streams retain their offsets.</summary>
public sealed class TransferControl : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private TaskCompletionSource? _resume;
    public CancellationToken Token => _cancellation.Token;
    public bool PreservePartialFiles { get; private set; }

    public void Pause()
    {
        lock (_sync)
            _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_sync) { resume = _resume; _resume = null; }
        resume?.TrySetResult();
    }

    public void Cancel(bool preservePartialFiles = false)
    {
        PreservePartialFiles |= preservePartialFiles;
        _cancellation.Cancel();
        Resume();
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait;
            lock (_sync) wait = _resume?.Task;
            if (wait is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            await wait.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _cancellation.Dispose();
}
