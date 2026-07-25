using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class ShellCoordinatorTests
{
    [TestMethod]
    public async Task Connection_failure_is_exposed_to_frontend()
    {
        var coordinator = CreateCoordinator((profile, _, _) => new FakeShellSession(profile, current =>
        {
            current.ReportConnectionFailure("Session operation has timed out");
            return Task.CompletedTask;
        }));
        ConnectionFailureEventArgs? failureNotification = null;
        coordinator.ConnectionFailed += (_, args) => failureNotification = args;
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };

        await coordinator.ConnectAsync(profile);

        Assert.IsNotNull(failureNotification, "连接失败时主窗口需要收到包含原因的通知。");
        Assert.AreEqual(profile, failureNotification.Profile);
        Assert.AreEqual("Session operation has timed out", failureNotification.Message);
    }

    [TestMethod]
    public async Task Connection_guard_deduplicates_pending_server_connection()
    {
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            factoryCalls++;
            return new FakeShellSession(profile, async _ =>
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

    [TestMethod]
    public async Task Reconnect_selected_session_reuses_existing_session()
    {
        var factoryCalls = 0;
        var connectCalls = 0;
        FakeShellSession? session = null;
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            factoryCalls++;
            session = new FakeShellSession(profile, current =>
            {
                connectCalls++;
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            });
            return session;
        });
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };

        await coordinator.ConnectAsync(profile);
        session!.SetConnectionState(SessionConnectionState.Disconnected);
        await coordinator.ReconnectSelectedSessionAsync();

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(2, connectCalls);
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
        private readonly Func<FakeShellSession, Task> _connect;

        public FakeShellSession(ServerProfile profile, Func<FakeShellSession, Task> connect)
        {
            Profile = profile;
            _connect = connect;
        }

        public ServerProfile Profile { get; }
        public bool IsConnected => ConnectionState == SessionConnectionState.Connected;
        public SessionConnectionState ConnectionState { get; private set; } = SessionConnectionState.Disconnected;
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

        public event EventHandler<string>? ConnectionFailed;

        public Task ConnectAsync() => _connect(this);
        public void ReportConnectionFailure(string message) => ConnectionFailed?.Invoke(this, message);
        public void SetConnectionState(SessionConnectionState connectionState) => ConnectionState = connectionState;
        public void SetActive(bool active) { }
        public void SetTerminalFontSize(double value) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
