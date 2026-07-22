using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class ShellCoordinatorTests
{
    [TestMethod]
    public async Task Connection_guard_deduplicates_pending_server_connection()
    {
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            factoryCalls++;
            return new FakeShellSession(profile, async () =>
            {
                connectStarted.SetResult();
                await completeConnection.Task;
            });
        });
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };

        var firstAttempt = coordinator.ConnectAsync(profile);
        await connectStarted.Task;
        await coordinator.ConnectAsync(profile);
        completeConnection.SetResult();
        await firstAttempt;

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(0, coordinator.SessionCount);
    }

    private static ShellCoordinator CreateCoordinator(
        Func<ServerProfile, Func<Task<string?>>, Func<HostFingerprintRequiredEventArgs, Task<bool>>, IShellSession> sessionFactory) =>
        new(
            new SettingsStore(),
            new CredentialService(),
            new ServerCatalog(new ServerProfileStore(), new CredentialService()),
            sessionFactory,
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult(false));

    private sealed class FakeShellSession : IShellSession
    {
        private readonly Func<Task> _connect;

        public FakeShellSession(ServerProfile profile, Func<Task> connect)
        {
            Profile = profile;
            _connect = connect;
        }

        public ServerProfile Profile { get; }
        public bool IsConnected => false;
        public bool IsTransferActive => false;
        public event EventHandler<ServerMetrics?>? MetricsUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync() => _connect();
        public void SetActive(bool active) { }
        public void SetTerminalFontSize(double value) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}