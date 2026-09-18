using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SessionConnectionTests
{
    [TestMethod]
    public async Task Connection_failure_returns_to_disconnected_and_reports_the_reason()
    {
        var connection = new FakeSshConnection { ConnectFailure = new IOException("网络不可达") };
        string? failureMessage = null;
        var statuses = new List<string>();
        await using var session = CreateSession(connection);
        session.ConnectionFailed += (_, message) => failureMessage = message;
        session.StatusChanged += (_, status) => statuses.Add(status);

        await session.ConnectAsync();

        Assert.AreEqual(SessionConnectionState.Disconnected, session.State);
        Assert.AreEqual("网络不可达", failureMessage);
        CollectionAssert.AreEqual(new[] { "连接中", "连接失败：网络不可达" }, statuses);
        Assert.AreEqual(1, connection.DisposeCount, "连接失败后必须释放这条已经作废的连接。");
    }

    [TestMethod]
    public async Task Cancelling_the_secret_prompt_leaves_the_session_disconnected()
    {
        var connection = new FakeSshConnection();
        var statuses = new List<string>();
        await using var session = CreateSession(connection, secretProvider: () => Task.FromResult<string?>(null));
        session.StatusChanged += (_, status) => statuses.Add(status);

        await session.ConnectAsync();

        Assert.AreEqual(SessionConnectionState.Disconnected, session.State);
        Assert.AreEqual(0, connection.ConnectCount, "取消输入口令后不应发起连接。");
        CollectionAssert.AreEqual(new[] { "连接中", "连接已取消" }, statuses);
    }

    [TestMethod]
    public async Task Reconnecting_replaces_the_previous_connection_and_releases_it()
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var handedOut = new Queue<FakeSshConnection>([first, second]);
        await using var session = CreateSession(() => handedOut.Dequeue());

        await session.ConnectAsync();
        first.IsConnected = false;
        await session.ConnectAsync();

        Assert.AreEqual(1, first.DisposeCount, "旧连接必须在被替换时释放。");
        Assert.AreEqual(0, first.Subscribers, "旧连接的事件订阅必须解除，否则它的输出仍会流向会话。");
        Assert.AreEqual(1, second.ConnectCount);
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reconnecting_recovers_a_failed_sftp_channel_while_ssh_is_connected(bool transferChannel)
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var connections = new Queue<FakeSshConnection>([first, second]);
        await using var session = CreateSession(() => connections.Dequeue());
        var browse = session.RemoteFiles;
        var transfer = session.TransferRemoteFiles;
        await session.ConnectAsync();
        (transferChannel ? first.TransferFileClient : first.RemoteFileClient).IsConnected = false;

        Assert.IsFalse(session.IsConnected, "A broken SFTP channel must not block reconnect.");
        await session.ConnectAsync();

        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(1, second.ConnectCount);
        Assert.IsTrue(session.IsConnected);
        Assert.IsTrue(browse.IsConnected);
        Assert.IsTrue(transfer.IsConnected);
        Assert.AreSame(browse, session.RemoteFiles);
        Assert.AreSame(transfer, session.TransferRemoteFiles);
    }

    [TestMethod]
    public async Task Disconnect_notification_allows_reconnect_even_if_transport_still_reports_connected()
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var connections = new Queue<FakeSshConnection>([first, second]);
        var cancelled = 0;
        await using var session = CreateSession(() => connections.Dequeue(), cancelTransfers: () => cancelled++);
        await session.ConnectAsync();
        session.SetActive(true);
        first.RaiseDisconnected();

        Assert.IsFalse(session.IsConnected);
        Assert.IsTrue(first.LastMetricsToken.IsCancellationRequested);
        Assert.AreEqual(1, cancelled);
        await session.ConnectAsync();

        Assert.AreEqual(1, second.ConnectCount);
        Assert.AreEqual(SessionConnectionState.Connected, session.State);
    }

    [TestMethod]
    public async Task Queued_disconnect_from_replaced_connection_does_not_disconnect_new_session()
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var connections = new Queue<FakeSshConnection>([first, second]);
        var posted = new Queue<Action>();
        await using var session = CreateSession(() => connections.Dequeue(), post: posted.Enqueue);
        await session.ConnectAsync();
        first.IsConnected = false;
        first.RaiseDisconnected();
        await session.ConnectAsync();

        posted.Dequeue()();

        Assert.AreEqual(SessionConnectionState.Connected, session.State);
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task Old_disconnect_while_prompting_does_not_clear_reconnecting_state()
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var connections = new Queue<FakeSshConnection>([first, second]);
        var secret = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        await using var session = CreateSession(() => connections.Dequeue(),
            secretProvider: () => ++prompts == 1 ? Task.FromResult<string?>("secret") : secret.Task);
        await session.ConnectAsync();
        first.IsConnected = false;
        var reconnect = session.ConnectAsync();
        try
        {
            first.RaiseDisconnected();
            Assert.AreEqual(SessionConnectionState.Connecting, session.State);
            await session.ConnectAsync();
            Assert.AreEqual(2, prompts);
        }
        finally
        {
            secret.TrySetResult("secret");
            await reconnect;
        }
        Assert.AreEqual(1, second.ConnectCount);
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task Connection_that_is_already_broken_after_connect_is_not_reported_as_successful()
    {
        var connection = new FakeSshConnection { DisconnectOnConnect = true };
        await using var session = CreateSession(connection);
        var connectedEvents = 0;
        session.Connected += (_, _) => connectedEvents++;

        await session.ConnectAsync();

        Assert.AreEqual(SessionConnectionState.Disconnected, session.State);
        Assert.AreEqual(0, connectedEvents);
        Assert.AreEqual(1, connection.DisposeCount);
    }

    [TestMethod]
    public async Task Already_connected_session_does_not_reconnect()
    {
        var connection = new FakeSshConnection();
        await using var session = CreateSession(connection);

        await session.ConnectAsync();
        await session.ConnectAsync();

        Assert.AreEqual(1, connection.ConnectCount);
        Assert.AreEqual(0, connection.DisposeCount);
    }

    [TestMethod]
    public async Task Deactivating_the_session_cancels_metrics_polling()
    {
        var connection = new FakeSshConnection();
        await using var session = CreateSession(connection);
        await session.ConnectAsync();

        session.SetActive(true);
        await connection.MetricsRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.SetActive(false);

        Assert.IsTrue(
            connection.LastMetricsToken.IsCancellationRequested,
            "取消激活后指标轮询必须停止。");
    }

    [TestMethod]
    public async Task Disposing_cancels_polling_cancels_transfers_and_releases_the_connection()
    {
        var connection = new FakeSshConnection();
        var transfersCancelled = 0;
        var session = CreateSession(connection, cancelTransfers: () => transfersCancelled++);
        await session.ConnectAsync();
        session.SetActive(true);
        await connection.MetricsRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        Assert.IsTrue(connection.LastMetricsToken.IsCancellationRequested, "释放时必须取消指标轮询。");
        Assert.AreEqual(1, transfersCancelled, "释放时必须取消正在进行的传输。");
        Assert.AreEqual(1, connection.DisposeCount, "释放时必须释放连接。");
        Assert.AreEqual(0, connection.Subscribers);
        Assert.IsFalse(session.IsConnected);
    }

    [TestMethod]
    public async Task Accepted_host_fingerprint_is_remembered_on_the_profile()
    {
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };
        var connection = new FakeSshConnection();
        await using var session = CreateSession(
            connection,
            profile: profile,
            confirmFingerprint: _ => Task.FromResult(true));
        await session.ConnectAsync();

        connection.RaiseHostFingerprintRequired(new HostFingerprintRequiredEventArgs
        {
            Fingerprint = "AA:BB",
            KeyType = "ssh-ed25519"
        });

        Assert.AreEqual("AA:BB", profile.HostFingerprint);
    }

    [TestMethod]
    public async Task Rejected_host_fingerprint_is_not_remembered()
    {
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };
        var connection = new FakeSshConnection();
        await using var session = CreateSession(
            connection,
            profile: profile,
            confirmFingerprint: _ => Task.FromResult(false));
        await session.ConnectAsync();

        var request = new HostFingerprintRequiredEventArgs { Fingerprint = "AA:BB", KeyType = "ssh-rsa" };
        connection.RaiseHostFingerprintRequired(request);

        Assert.IsFalse(request.Accepted);
        Assert.AreEqual(string.Empty, profile.HostFingerprint);
    }

    [TestMethod]
    public async Task Losing_the_connection_reports_a_disconnected_status()
    {
        var connection = new FakeSshConnection();
        var statuses = new List<string>();
        await using var session = CreateSession(connection);
        await session.ConnectAsync();
        session.StatusChanged += (_, status) => statuses.Add(status);

        connection.IsConnected = false;
        connection.RaiseDisconnected();

        Assert.AreEqual(SessionConnectionState.Disconnected, session.State);
        CollectionAssert.AreEqual(new[] { "连接已断开" }, statuses);
    }

    [TestMethod]
    public async Task Remote_files_follow_the_connection_that_replaced_the_previous_one()
    {
        var first = new FakeSshConnection();
        var second = new FakeSshConnection();
        var handedOut = new Queue<FakeSshConnection>([first, second]);
        first.RemoteFileClient.AddFile("旧连接.txt", "/旧连接.txt");
        second.RemoteFileClient.AddFile("新连接.txt", "/新连接.txt");
        await using var session = CreateSession(() => handedOut.Dequeue());
        var remoteFiles = session.RemoteFiles;

        await session.ConnectAsync();
        first.IsConnected = false;
        await session.ConnectAsync();
        var items = await remoteFiles.ListDirectoryAsync("/");

        Assert.AreSame(
            remoteFiles,
            session.RemoteFiles,
            "远程文件入口在重连后必须仍是同一个实例，调用方不需要重新取用。");
        Assert.AreEqual(
            "新连接.txt",
            items.Single().Name,
            "重连后远程文件入口必须读到新连接，而不是被替换掉的那条。");
    }

    private static SessionConnection CreateSession(
        FakeSshConnection connection,
        ServerProfile? profile = null,
        Func<Task<string?>>? secretProvider = null,
        Func<HostFingerprintRequiredEventArgs, Task<bool>>? confirmFingerprint = null,
        Action? cancelTransfers = null) =>
        CreateSession(
            () => connection,
            profile,
            secretProvider,
            confirmFingerprint,
            cancelTransfers);

    private static SessionConnection CreateSession(
        Func<FakeSshConnection> connectionFactory,
        ServerProfile? profile = null,
        Func<Task<string?>>? secretProvider = null,
        Func<HostFingerprintRequiredEventArgs, Task<bool>>? confirmFingerprint = null,
        Action? cancelTransfers = null,
        Action<Action>? post = null) =>
        new(
            profile ?? new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" },
            _ => connectionFactory(),
            secretProvider ?? (() => Task.FromResult<string?>("secret")),
            confirmFingerprint ?? (_ => Task.FromResult(false)),
            post ?? (work => work()),
            cancelTransfers ?? (() => { }));

    private sealed class FakeSshConnection : ISshConnection
    {
        public bool IsConnected { get; set; }
        public ISftpClient? SftpClient => IsConnected ? RemoteFileClient : null;
        public ISftpClient? TransferSftpClient => IsConnected ? TransferFileClient : null;
        public FakeSftpClient RemoteFileClient { get; } = new();
        public FakeSftpClient TransferFileClient { get; } = new();
        public Exception? ConnectFailure { get; init; }
        public bool DisconnectOnConnect { get; init; }
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CancellationToken LastMetricsToken { get; private set; }
        public TaskCompletionSource MetricsRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Subscribers =>
            (OutputReceived?.GetInvocationList().Length ?? 0) +
            (HostFingerprintRequired?.GetInvocationList().Length ?? 0) +
            (Disconnected?.GetInvocationList().Length ?? 0);

        public event EventHandler<string>? OutputReceived;
        public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
        public event EventHandler? Disconnected;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            if (ConnectFailure is not null) return Task.FromException(ConnectFailure);
            IsConnected = !DisconnectOnConnect;
            return Task.CompletedTask;
        }

        public Task SendRawAsync(string input) => Task.CompletedTask;

        public Task ResizeTerminalAsync(int columns, int rows) => Task.CompletedTask;

        public Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default)
        {
            LastMetricsToken = cancellationToken;
            MetricsRequested.TrySetResult();
            return Task.FromResult<ServerMetrics?>(null);
        }

        public void RaiseHostFingerprintRequired(HostFingerprintRequiredEventArgs args) =>
            HostFingerprintRequired?.Invoke(this, args);

        public void RaiseDisconnected() => Disconnected?.Invoke(this, EventArgs.Empty);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
