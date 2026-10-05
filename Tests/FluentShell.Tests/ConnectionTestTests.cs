using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class ConnectionTestTests
{
    [TestMethod]
    public async Task Success_confirms_fingerprint_and_disposes_connection()
    {
        var connection = new Probe();
        await ConnectionTest.RunAsync(connection, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.IsTrue(connection.Disposed);
        Assert.IsTrue(connection.Accepted);
        Assert.IsFalse(connection.HasHandler);
    }

    [TestMethod]
    public async Task Rejected_fingerprint_fails_and_disposes_connection()
    {
        var connection = new Probe();
        await Assert.ThrowsAsync<IOException>(() => ConnectionTest.RunAsync(
            connection, (_, _) => Task.FromResult(false), CancellationToken.None));
        Assert.IsTrue(connection.Disposed);
        Assert.IsFalse(connection.HasHandler);
    }

    [TestMethod]
    public async Task Cancellation_interrupts_pending_confirmation_and_disposes_connection()
    {
        var connection = new Probe();
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = ConnectionTest.RunAsync(connection, (_, _) =>
        {
            reached.SetResult();
            return pending.Task;
        }, cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => test);
        Assert.IsTrue(connection.Disposed);
        Assert.IsFalse(connection.HasHandler);
    }

    private sealed class Probe : ISshConnection
    {
        public bool Accepted { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsConnected => Accepted && !Disposed;
        public bool HasHandler => HostFingerprintRequired is not null;
        public ISftpClient? SftpClient => null;
        public ISftpClient? TransferSftpClient => null;
        public event EventHandler<string>? OutputReceived { add { } remove { } }
        public event EventHandler? Disconnected { add { } remove { } }
        public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            var args = new HostFingerprintRequiredEventArgs();
            HostFingerprintRequired?.Invoke(this, args);
            Accepted = args.Accepted;
            if (!Accepted) throw new IOException("Fingerprint rejected");
            return Task.CompletedTask;
        }
        public Task SendRawAsync(string input) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows) => Task.CompletedTask;
        public Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult<ServerMetrics?>(null);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
