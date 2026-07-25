using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using System.Security.Cryptography;

namespace FluentShell.Tests;

[TestClass]
[DoNotParallelize]
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

    [TestMethod]
    public async Task Selecting_current_session_does_not_restart_metrics_polling()
    {
        FakeShellSession? session = null;
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            session = new FakeShellSession(profile, current =>
            {
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            });
            return session;
        });
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };

        await coordinator.ConnectAsync(profile);
        await coordinator.ConnectAsync(profile);

        Assert.AreEqual(1, session!.MetricsPollingStarts);
    }

    [TestMethod]
    public async Task Closing_selected_session_activates_next_session()
    {
        var sessions = new List<FakeShellSession>();
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            var session = new FakeShellSession(profile, current =>
            {
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            });
            sessions.Add(session);
            return session;
        });
        var firstProfile = new ServerProfile { Name = "第一台", Host = "first", Username = "user" };
        var secondProfile = new ServerProfile { Name = "第二台", Host = "second", Username = "user" };

        await coordinator.ConnectAsync(firstProfile);
        await coordinator.ConnectAsync(secondProfile);
        await coordinator.CloseSessionAsync(sessions[1], _ => Task.FromResult(true));

        Assert.AreSame(sessions[0], coordinator.SelectedSession);
        Assert.AreEqual(2, sessions[0].MetricsPollingStarts);
    }

    [TestMethod]
    public async Task Closing_inactive_session_does_not_restart_active_metrics_polling()
    {
        var sessions = new List<FakeShellSession>();
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            var session = new FakeShellSession(profile, current =>
            {
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            });
            sessions.Add(session);
            return session;
        });
        var firstProfile = new ServerProfile { Name = "第一台", Host = "first", Username = "user" };
        var secondProfile = new ServerProfile { Name = "第二台", Host = "second", Username = "user" };

        await coordinator.ConnectAsync(firstProfile);
        await coordinator.ConnectAsync(secondProfile);
        await coordinator.ConnectAsync(firstProfile);
        await coordinator.CloseSessionAsync(sessions[1], _ => Task.FromResult(true));

        Assert.AreSame(sessions[0], coordinator.SelectedSession);
        Assert.AreEqual(2, sessions[0].MetricsPollingStarts);
    }

    [TestMethod]
    public async Task ConnectAsync_skips_prompt_for_unencrypted_private_key()
    {
        var privateKeyPath = CreateUnencryptedPrivateKey();
        try
        {
            var promptCount = 0;
            string? suppliedSecret = null;
            var profile = new ServerProfile
            {
                Name = "无口令私钥服务器",
                Host = "host",
                Username = "user",
                Authentication = AuthenticationMethod.PrivateKey,
                PrivateKeyPath = privateKeyPath
            };
            var coordinator = CreateCoordinator(
                (currentProfile, secretProvider, _) => new FakeShellSession(currentProfile, async _ =>
                {
                    suppliedSecret = await secretProvider();
                }),
                _ =>
                {
                    promptCount++;
                    return Task.FromResult<string?>("不应请求私钥口令");
                });

            await coordinator.ConnectAsync(profile);

            Assert.AreEqual(0, promptCount);
            Assert.AreEqual(string.Empty, suppliedSecret);
        }
        finally
        {
            File.Delete(privateKeyPath);
        }
    }

    [TestMethod]
    public async Task ConnectAsync_prompts_for_encrypted_private_key_without_saved_passphrase()
    {
        const string passphrase = "test-passphrase";
        var privateKeyPath = CreateEncryptedPrivateKey(passphrase);
        try
        {
            var promptCount = 0;
            string? suppliedSecret = null;
            var profile = new ServerProfile
            {
                Name = "加密私钥服务器",
                Host = "host",
                Username = "user",
                Authentication = AuthenticationMethod.PrivateKey,
                PrivateKeyPath = privateKeyPath
            };
            var coordinator = CreateCoordinator(
                (currentProfile, secretProvider, _) => new FakeShellSession(currentProfile, async _ =>
                {
                    suppliedSecret = await secretProvider();
                }),
                _ =>
                {
                    promptCount++;
                    return Task.FromResult<string?>(passphrase);
                });

            await coordinator.ConnectAsync(profile);

            Assert.AreEqual(1, promptCount);
            Assert.AreEqual(passphrase, suppliedSecret);
        }
        finally
        {
            File.Delete(privateKeyPath);
        }
    }

    private static ShellCoordinator CreateCoordinator(
        Func<ServerProfile, Func<Task<string?>>, Func<HostFingerprintRequiredEventArgs, Task<bool>>, IShellSession> sessionFactory,
        Func<ServerProfile, Task<string?>>? secretPrompt = null) =>
        new(
            new SettingsStore(),
            new CredentialService(),
            new ServerCatalog(new ServerProfileStore(), new CredentialService()),
            sessionFactory,
            secretPrompt ?? (_ => Task.FromResult<string?>(null)),
            _ => Task.FromResult(false));

    private static string CreateUnencryptedPrivateKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fluent-shell-{Guid.NewGuid():N}.pem");
        using var key = RSA.Create(2048);
        File.WriteAllText(path, key.ExportRSAPrivateKeyPem());
        return path;
    }

    private static string CreateEncryptedPrivateKey(string passphrase)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fluent-shell-{Guid.NewGuid():N}.pem");
        using var key = RSA.Create(2048);
        var encryption = new PbeParameters(
            PbeEncryptionAlgorithm.Aes256Cbc,
            HashAlgorithmName.SHA256,
            100_000);
        File.WriteAllText(path, key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, encryption));
        return path;
    }

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
        public int MetricsPollingStarts { get; private set; }
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
        public void SetActive(bool active)
        {
            if (active) MetricsPollingStarts++;
        }
        public void SetTerminalFontSize(double value) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
