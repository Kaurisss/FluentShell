using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;
using System.Security.Cryptography;

namespace FluentShell.Tests;

[TestClass]
public sealed class ShellCoordinatorTests
{
    [TestMethod]
    public void Test_credentials_use_entered_or_matching_saved_secret_without_saving()
    {
        var original = new ServerProfile { Username = "fixture", Host = "offline.invalid" };
        var store = new InMemoryLocalStore([original]);
        store.SaveSecret(original, "synthetic-saved");
        var coordinator = CreateCoordinator((profile, _, _) => new FakeShellSession(profile), store: store);
        var draft = new ServerProfile { Id = original.Id, Username = original.Username };
        Assert.AreEqual("synthetic-entered", coordinator.ResolveTestSecret(draft, "synthetic-entered"));
        Assert.AreEqual("synthetic-saved", coordinator.ResolveTestSecret(draft, ""));
        draft.Protocol = ConnectionProtocol.Ftp;
        Assert.Throws<InvalidOperationException>(() => coordinator.ResolveTestSecret(draft, ""));
        draft.Protocol = original.Protocol;
        draft.Username = "different-user";
        Assert.Throws<InvalidOperationException>(() => coordinator.ResolveTestSecret(draft, ""));
        Assert.AreEqual("synthetic-saved", store.TryGetSecret(original));
        Assert.IsNull(original.LastConnectedAt);
        Assert.AreEqual(0, coordinator.SessionCount);
    }

    [TestMethod]
    public async Task Connection_factory_routes_file_protocols_and_rejects_invalid_ftp_configuration()
    {
        var coordinator = CreateCoordinator((profile, _, _) => new FakeShellSession(profile));
        var profile = new ServerProfile { Host = "127.0.0.1", Username = "fixture", Protocol = ConnectionProtocol.Ftp };
        await using (var ftp = await coordinator.CreateConnectionAsync(profile, "synthetic", CancellationToken.None))
            Assert.IsInstanceOfType<FtpConnectionService>(ftp);
        profile.Authentication = AuthenticationMethod.PrivateKey;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateConnectionAsync(profile, "", CancellationToken.None));
        profile.Authentication = AuthenticationMethod.Password;
        profile.JumpProfileId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateConnectionAsync(profile, "", CancellationToken.None));
        profile.JumpProfileId = null;
        profile.Protocol = ConnectionProtocol.Sftp;
        await using var sftp = await coordinator.CreateConnectionAsync(profile, "synthetic", CancellationToken.None);
        Assert.IsInstanceOfType<SshConnectionService>(sftp);
    }
    [TestMethod]
    public async Task Terminal_colors_apply_to_existing_and_new_sessions_and_reset()
    {
        var store = new InMemoryLocalStore();
        var coordinator = CreateCoordinator((profile, _, _) => new FakeShellSession(profile, current =>
        {
            current.SetConnectionState(SessionConnectionState.Connected);
            return Task.CompletedTask;
        }), store: store);
        await coordinator.LoadAsync();
        await coordinator.ConnectAsync(new ServerProfile { Host = "first" });
        await coordinator.UpdateSettingsAsync(new AppSettingsUpdate(TerminalColors: new() { Dark = new() { ["red"] = "#abcdef" } }));
        await coordinator.ConnectAsync(new ServerProfile { Host = "second" });
        Assert.AreEqual("#ABCDEF", store.PersistedSettings.TerminalColors.Dark["red"]);
        foreach (var session in coordinator.Sessions.Cast<FakeShellSession>())
            Assert.AreEqual("#ABCDEF", session.TerminalColors.Dark["red"]);
        await coordinator.UpdateSettingsAsync(new AppSettingsUpdate(TerminalColors: new()));
        foreach (var session in coordinator.Sessions.Cast<FakeShellSession>())
            Assert.IsEmpty(session.TerminalColors.Dark);
    }
    [TestMethod]
    public async Task Connection_factory_uses_saved_jump_and_cancellation_of_jump_prompt()
    {
        var jump = new ServerProfile { Name = "跳板", Host = "jump", Username = "jump" };
        var target = new ServerProfile
        {
            Name = "目标", Host = "target", Username = "user", JumpProfileId = jump.Id
        };
        var store = new InMemoryLocalStore([jump, target]);
        var coordinator = CreateCoordinator(
            (profile, _, _) => new FakeShellSession(profile),
            _ => Task.FromResult<string?>(null), store);

        Assert.IsNull(await coordinator.CreateConnectionAsync(target, "secret", CancellationToken.None));

        store.SaveSecret(jump, "jump-secret");
        await using var connection = await coordinator.CreateConnectionAsync(target, "secret", CancellationToken.None);
        Assert.IsInstanceOfType<JumpHostConnectionService>(connection);
    }

    [TestMethod]
    public async Task Connecting_does_not_clear_saved_profiles()
    {
        var first = new ServerProfile { Name = "第一台", Host = "first", Username = "user" };
        var second = new ServerProfile { Name = "第二台", Host = "second", Username = "user" };
        var store = new InMemoryLocalStore([first, second]);
        var coordinator = CreateCoordinator(
            (profile, _, _) => new FakeShellSession(profile, current =>
            {
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            }),
            store: store);
        await coordinator.LoadAsync();

        await coordinator.ConnectAsync(first);

        CollectionAssert.AreEquivalent(
            new[] { first, second },
            store.PersistedProfiles.ToArray(),
            "连接成功后写回的已保存服务器列表不得丢失条目。");
    }

    [TestMethod]
    public async Task Updating_settings_persists_through_the_store()
    {
        var store = new InMemoryLocalStore();
        var coordinator = CreateCoordinator((profile, _, _) => new FakeShellSession(profile), store: store);
        await coordinator.LoadAsync();

        await coordinator.UpdateSettingsAsync(new AppSettingsUpdate(TerminalFontSize: 18));

        Assert.AreEqual(18, store.PersistedSettings.TerminalFontSize);
    }

    [TestMethod]
    public async Task Connecting_records_last_connected_at_through_the_store()
    {
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };
        var store = new InMemoryLocalStore([profile]);
        var coordinator = CreateCoordinator(
            (current, _, _) => new FakeShellSession(current, session =>
            {
                session.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            }),
            store: store);
        await coordinator.LoadAsync();

        await coordinator.ConnectAsync(profile);

        Assert.IsNotNull(profile.LastConnectedAt);
        Assert.AreEqual(1, store.SaveProfilesCallCount);
    }

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
    public async Task CancelConnection_cancels_the_active_connection_token()
    {
        var connectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeShellSession? session = null;
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            session = new FakeShellSession(profile, async _ =>
            {
                connectionStarted.SetResult();
                await releaseConnection.Task;
            });
            return session;
        });
        var profile = new ServerProfile { Name = "测试服务器", Host = "host", Username = "user" };

        var connecting = coordinator.ConnectAsync(profile);
        await connectionStarted.Task;
        coordinator.CancelConnection();

        Assert.IsTrue(
            session!.LastConnectionCancellationToken.IsCancellationRequested,
            "取消连接必须把取消信号传递到会话层。");
        releaseConnection.SetResult();
        await connecting;
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
    public async Task Connecting_same_server_opens_independent_sessions()
    {
        var sessions = new List<FakeShellSession>();
        var store = new InMemoryLocalStore();
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            var session = FakeShellSession.Connectable(profile);
            sessions.Add(session);
            return session;
        }, store: store);
        var profile = new ServerProfile { Name = "生产机", Host = "offline.invalid", Username = "fixture" };

        await coordinator.ConnectAsync(profile);
        await coordinator.ConnectAsync(profile);

        Assert.AreEqual(2, coordinator.SessionCount);
        Assert.AreNotEqual(sessions[0].Id, sessions[1].Id);
        Assert.AreSame(profile, sessions[0].Profile);
        Assert.AreSame(profile, sessions[1].Profile);
        Assert.AreNotSame(sessions[0].ContentElement, sessions[1].ContentElement);
        Assert.AreSame(sessions[1], coordinator.SelectedSession);
        Assert.IsFalse(sessions[0].IsActive);
        Assert.IsTrue(sessions[1].IsActive);
        Assert.AreEqual(2, store.SaveProfilesCallCount);

        coordinator.SelectSession(sessions[0]);

        Assert.HasCount(2, sessions, "切换标签页不能创建新连接。");
        Assert.AreSame(sessions[0], coordinator.SelectedSession);
        Assert.IsTrue(sessions[0].IsActive);
        Assert.IsFalse(sessions[1].IsActive);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Closing_one_of_same_server_sessions_preserves_the_other(bool closeSelected)
    {
        var coordinator = CreateCoordinator((profile, _, _) => FakeShellSession.Connectable(profile));
        var profile = new ServerProfile { Host = "offline.invalid", Username = "fixture" };
        await coordinator.ConnectAsync(profile);
        var first = (FakeShellSession)coordinator.SelectedSession!;
        await coordinator.ConnectAsync(profile);
        var second = (FakeShellSession)coordinator.SelectedSession!;
        var closed = closeSelected ? second : first;
        var remaining = closeSelected ? first : second;

        Assert.IsTrue(await coordinator.CloseSessionAsync(closed, _ => Task.FromResult(true)));

        Assert.IsTrue(closed.IsDisposed);
        Assert.IsFalse(remaining.IsDisposed);
        Assert.IsTrue(remaining.IsConnected);
        Assert.IsTrue(remaining.IsActive);
        Assert.AreSame(remaining, coordinator.SelectedSession);
        CollectionAssert.AreEqual(new[] { remaining }, coordinator.Sessions.ToArray());
        Assert.AreEqual(closeSelected ? 2 : 1, remaining.MetricsPollingStarts);
    }

    [TestMethod]
    public async Task Reconnecting_one_of_same_server_sessions_preserves_the_other()
    {
        var sessions = new List<FakeShellSession>();
        var connectCounts = new Dictionary<Guid, int>();
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            var session = new FakeShellSession(profile, current =>
            {
                connectCounts[current.Id] = connectCounts.GetValueOrDefault(current.Id) + 1;
                current.SetConnectionState(SessionConnectionState.Connected);
                return Task.CompletedTask;
            });
            sessions.Add(session);
            return session;
        });
        var profile = new ServerProfile { Host = "offline.invalid", Username = "fixture" };
        await coordinator.ConnectAsync(profile);
        await coordinator.ConnectAsync(profile);
        sessions[0].SetConnectionState(SessionConnectionState.Disconnected);
        coordinator.SelectSession(sessions[0]);

        await coordinator.ReconnectSelectedSessionAsync();

        Assert.HasCount(2, sessions);
        Assert.AreEqual(2, connectCounts[sessions[0].Id]);
        Assert.AreEqual(1, connectCounts[sessions[1].Id]);
        Assert.IsTrue(sessions.All(session => session.IsConnected && !session.IsDisposed));
        Assert.AreSame(sessions[0], coordinator.SelectedSession);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unsuccessful_additional_connection_preserves_existing_session(bool cancel)
    {
        var sessions = new List<FakeShellSession>();
        var coordinator = CreateCoordinator((profile, _, _) =>
        {
            var session = sessions.Count == 0
                ? FakeShellSession.Connectable(profile)
                : new FakeShellSession(profile, current =>
                {
                    if (cancel) throw new OperationCanceledException();
                    current.ReportConnectionFailure("Synthetic connection failure");
                    return Task.CompletedTask;
                });
            sessions.Add(session);
            return session;
        });
        var profile = new ServerProfile { Host = "offline.invalid", Username = "fixture" };
        await coordinator.ConnectAsync(profile);

        await coordinator.ConnectAsync(profile);

        Assert.AreEqual(1, coordinator.SessionCount);
        Assert.AreSame(sessions[0], coordinator.SelectedSession);
        Assert.IsTrue(sessions[0].IsConnected);
        Assert.IsTrue(sessions[0].IsActive);
        Assert.IsFalse(sessions[0].IsDisposed);
        Assert.IsTrue(sessions[1].IsDisposed);
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
        coordinator.SelectSession(session!);

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
        coordinator.SelectSession(sessions[0]);
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
        Func<ServerProfile, Task<string?>>? secretPrompt = null,
        ILocalStore? store = null) =>
        new(
            store ?? new InMemoryLocalStore(),
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

}
