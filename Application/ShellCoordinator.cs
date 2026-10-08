using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

public enum SessionConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

public interface IShellSession : IAsyncDisposable
{
    /// <summary>每个标签页独立的会话标识，同一服务器可创建多个会话。</summary>
    Guid Id { get; }
    ServerProfile Profile { get; }

    /// <summary>标签栏上显示的会话名称。</summary>
    string DisplayTitle { get; }

    /// <summary>会话在内容区呈现的元素，交给外壳的内容宿主显示。</summary>
    object ContentElement { get; }

    bool IsConnected { get; }
    SessionConnectionState ConnectionState { get; }
    bool IsTransferActive { get; }

    event EventHandler<ServerMetrics?> MetricsUpdated;
    event EventHandler<string> StatusChanged;
    event EventHandler<string> ConnectionFailed;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    void SetActive(bool active);
    void SetTerminalFontSize(double value);
    void SetTerminalColors(TerminalColors colors);
    void SetPreferences(UserPreferences preferences, string downloadDirectory);
}

public sealed record ServerProfileUpdate(
    ServerProfile Profile,
    bool SaveCredential,
    bool CredentialIdentityChanged,
    string OriginalUsername,
    bool ConnectAfterSave,
    string EnteredSecret);

public sealed record AppSettingsUpdate(
    string? Theme = null,
    string? BackdropMaterial = null,
    double? TerminalFontSize = null,
    string? DownloadDirectory = null,
    TerminalColors? TerminalColors = null,
    UserPreferences? Preferences = null,
    AppSettings? Replacement = null);
public sealed class ShellCoordinator
{
    private readonly ILocalStore _localStore;
    private readonly SessionCoordinator<IShellSession> _sessions = new(session => session.Id);
    private readonly Func<
        ServerProfile,
        Func<Task<string?>>,
        Func<HostFingerprintRequiredEventArgs, Task<bool>>,
        IShellSession> _sessionFactory;
    private readonly Func<ServerProfile, Task<string?>> _secretPrompt;
    private readonly Func<HostFingerprintRequiredEventArgs, Task<bool>> _fingerprintConfirmation;
    private readonly Dictionary<Guid, string> _sessionSecrets = [];
    private readonly Dictionary<Guid, bool> _credentialPersistenceOverrides = [];
    private AppSettings _settings = new();
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);
    private CancellationTokenSource? _connectionCancellation;

    public ShellCoordinator(
        ILocalStore localStore,
        Func<ServerProfile, Func<Task<string?>>, Func<HostFingerprintRequiredEventArgs, Task<bool>>, IShellSession> sessionFactory,
        Func<ServerProfile, Task<string?>> secretPrompt,
        Func<HostFingerprintRequiredEventArgs, Task<bool>> fingerprintConfirmation)
    {
        _localStore = localStore;
        _sessionFactory = sessionFactory;
        _secretPrompt = secretPrompt;
        _fingerprintConfirmation = fingerprintConfirmation;
    }

    public IReadOnlyList<ServerProfile> Profiles => _localStore.Profiles;
    public string DataFolder => _localStore.DataFolder;
    public AppSettings Settings => _settings;
    public int SessionCount => _sessions.Count;
    public IShellSession? SelectedSession => _sessions.Selected;
    public IReadOnlyCollection<IShellSession> Sessions => _sessions.Sessions;

    public event EventHandler? StateChanged;
    public event EventHandler<ConnectionProgressChangedEventArgs>? ConnectionProgressChanged;
    public event EventHandler<ConnectionFailureEventArgs>? ConnectionFailed;
    public event EventHandler<IShellSession>? SessionAdded;
    public event EventHandler<IShellSession>? SessionRemoved;
    public event EventHandler<IShellSession?>? SessionSelected;
    public event EventHandler<SessionMetricsUpdatedEventArgs>? MetricsUpdated;

    public async Task LoadAsync()
    {
        _settings = SettingsBackup.Normalize(await _localStore.LoadAsync());
        _settings.TerminalColors = (_settings.TerminalColors ?? new()).Normalize();
        _settings.Preferences = (_settings.Preferences ?? new()).Normalize();
        NotifyStateChanged();
    }

    public void CancelConnection()
    {
        _connectionCancellation?.Cancel();
    }

    public bool HasSavedCredential(ServerProfile profile) => _localStore.TryGetSecret(profile) is not null;

    internal string ResolveTestSecret(ServerProfile profile, string enteredSecret)
    {
        if (!string.IsNullOrEmpty(enteredSecret)) return enteredSecret;
        var original = Profiles.FirstOrDefault(candidate => candidate.Id == profile.Id);
        if (original is not null && original.Protocol == profile.Protocol &&
            original.Authentication == profile.Authentication && original.Username == profile.Username &&
            _localStore.TryGetSecret(profile) is string saved)
            return saved;
        if (profile.Authentication == AuthenticationMethod.PrivateKey &&
            !SshConnectionService.RequiresPrivateKeyPassphrase(profile.PrivateKeyPath))
            return string.Empty;
        throw new InvalidOperationException($"请填写“{profile.Name}”的密码或私钥口令；跳板服务器需先保存凭据。");
    }

    public async Task TestConnectionAsync(
        ServerProfile profile, string enteredSecret,
        Func<HostFingerprintRequiredEventArgs, CancellationToken, Task<bool>> confirmFingerprint,
        CancellationToken cancellationToken)
    {
        var secret = await Task.Run(() => ResolveTestSecret(profile, enteredSecret), cancellationToken);
        var connection = await CreateConnectionAsync(profile, secret, cancellationToken,
            jump => Task.Run<string?>(() => ResolveTestSecret(jump, string.Empty), cancellationToken));
        if (connection is null) throw new OperationCanceledException(cancellationToken);
        await ConnectionTest.RunAsync(connection, confirmFingerprint, cancellationToken);
    }

    public async Task<ISshConnection?> CreateConnectionAsync(
        ServerProfile profile,
        string secret,
        CancellationToken cancellationToken,
        Func<ServerProfile, Task<string?>>? resolveJumpSecret = null)
    {
        if (!Enum.IsDefined(profile.Protocol))
            throw new InvalidOperationException("不支持的连接协议，请编辑服务器配置。");
        if (profile.Protocol == ConnectionProtocol.Ftp)
        {
            if (profile.JumpProfileId is not null || profile.Authentication != AuthenticationMethod.Password)
                throw new InvalidOperationException("FTP 仅支持直连和密码认证。");
            return new FtpConnectionService(profile, secret, _settings.Preferences);
        }
        var jump = JumpHostResolver.Resolve(profile, Profiles);
        if (jump is null) return new SshConnectionService(profile, secret, _settings.Preferences);

        cancellationToken.ThrowIfCancellationRequested();
        var jumpSecret = await (resolveJumpSecret ?? ResolveHopSecretAsync)(jump);
        cancellationToken.ThrowIfCancellationRequested();
        return jumpSecret is null
            ? null
            : new JumpHostConnectionService(profile, secret, jump, jumpSecret, _settings.Preferences);
    }

    public async Task SaveProfileAsync(ServerProfileUpdate update)
    {
        if (update.CredentialIdentityChanged)
        {
            _localStore.RemoveSecret(update.Profile.Id, update.OriginalUsername);
            _sessionSecrets.Remove(update.Profile.Id);
        }
        if (update.SaveCredential)
        {
            if (!string.IsNullOrEmpty(update.EnteredSecret))
                _localStore.SaveSecret(update.Profile, update.EnteredSecret);
        }
        else
        {
            _localStore.RemoveSecret(update.Profile);
        }

        await _localStore.AddOrUpdateProfileAsync(update.Profile);
        NotifyStateChanged();
        if (!update.ConnectAfterSave) return;

        _credentialPersistenceOverrides[update.Profile.Id] = update.SaveCredential;
        if (!string.IsNullOrEmpty(update.EnteredSecret))
            _sessionSecrets[update.Profile.Id] = update.EnteredSecret;
        await ConnectAsync(update.Profile);
    }

    public async Task CopyProfileAsync(ServerProfile profile)
    {
        await _localStore.CopyProfileAsync(profile);
        NotifyStateChanged();
    }

    public async Task DeleteProfileAsync(ServerProfile profile)
    {
        await _localStore.DeleteProfileAsync(profile);
        NotifyStateChanged();
    }

    public void ClearLocalData()
    {
        _localStore.ClearAll();
        NotifyStateChanged();
    }

    public async Task UpdateSettingsAsync(AppSettingsUpdate update)
    {
        await _settingsUpdateGate.WaitAsync();
        try
        {
            var source = update.Replacement ?? _settings;
            var next = new AppSettings
            {
                Theme = source.Theme, BackdropMaterial = source.BackdropMaterial,
                TerminalFontSize = source.TerminalFontSize, TerminalColors = source.TerminalColors,
                Preferences = source.Preferences, DownloadDirectory = source.DownloadDirectory,
                HasCustomDownloadDirectory = source.HasCustomDownloadDirectory
            };
            if (update.Replacement is not null) next = SettingsBackup.Normalize(next);
            if (update.Preferences is not null) next.Preferences = update.Preferences.Normalize();
            if (!next.Preferences.HasUniqueShortcuts) throw new ArgumentException("快捷键重复。");
            if (update.TerminalColors is not null) next.TerminalColors = update.TerminalColors.Normalize();
            if (update.Theme is not null) next.Theme = update.Theme;
            if (update.BackdropMaterial is not null) next.BackdropMaterial = update.BackdropMaterial;
            if (update.TerminalFontSize is not null) next.TerminalFontSize = Math.Clamp(update.TerminalFontSize.Value, 11, 24);
            if (update.DownloadDirectory is not null)
            {
                next.DownloadDirectory = update.DownloadDirectory;
                next.HasCustomDownloadDirectory = true;
            }
            await _localStore.SaveSettingsAsync(next);
            _settings = next;
            foreach (var session in _sessions.Sessions)
            {
                session.SetTerminalFontSize(next.TerminalFontSize);
                session.SetTerminalColors(next.TerminalColors);
                session.SetPreferences(next.Preferences, next.DownloadDirectory);
            }
            NotifyStateChanged();
        }
        finally { _settingsUpdateGate.Release(); }
    }
    public async Task ConnectAsync(ServerProfile profile)
    {
        if (!_sessions.TryBeginConnection(profile.Id)) return;

        using var connectionCancellation = new CancellationTokenSource();
        _connectionCancellation = connectionCancellation;
        var session = _sessionFactory(
            profile,
            () => ResolveSecretAsync(profile),
            _fingerprintConfirmation);
        session.SetTerminalFontSize(_settings.TerminalFontSize);
        session.SetTerminalColors(_settings.TerminalColors);
        session.SetPreferences(_settings.Preferences, _settings.DownloadDirectory);
        SubscribeSession(session);
        ConnectionProgressChanged?.Invoke(
            this,
            new ConnectionProgressChangedEventArgs(true, $"正在连接 {profile.Name}…"));
        try
        {
            await session.ConnectAsync(connectionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Connection was cancelled by user
        }
        finally
        {
            _sessions.EndConnection(profile.Id);
            ConnectionProgressChanged?.Invoke(this, new ConnectionProgressChangedEventArgs(false, null));
            if (ReferenceEquals(_connectionCancellation, connectionCancellation))
                _connectionCancellation = null;
        }

        if (!session.IsConnected)
        {
            _sessionSecrets.Remove(profile.Id);
            _credentialPersistenceOverrides.Remove(profile.Id);
            UnsubscribeSession(session);
            await session.DisposeAsync();
            return;
        }

        _sessions.Add(session);
        SessionAdded?.Invoke(this, session);
        SelectSession(session);
        profile.LastConnectedAt = DateTimeOffset.Now;
        var shouldPersistCredential = _credentialPersistenceOverrides.Remove(
            profile.Id,
            out var persistenceOverride)
            ? persistenceOverride
            : true;
        if (shouldPersistCredential && _sessionSecrets.TryGetValue(profile.Id, out var secret))
            _localStore.SaveSecret(profile, secret);
        else if (!shouldPersistCredential)
            _localStore.RemoveSecret(profile);
        _sessionSecrets.Remove(profile.Id);
        await _localStore.SaveProfilesAsync();
        NotifyStateChanged();
    }

    public Task ReconnectSelectedSessionAsync() =>
        SelectedSession is null ? Task.CompletedTask : ReconnectAsync(SelectedSession);

    private async Task ReconnectAsync(IShellSession session)
    {
        if (!_sessions.Contains(session) || session.ConnectionState == SessionConnectionState.Connecting || session.IsConnected)
            return;
        if (!_sessions.TryBeginConnection(session.Id)) return;

        using var connectionCancellation = new CancellationTokenSource();
        _connectionCancellation = connectionCancellation;
        ConnectionProgressChanged?.Invoke(
            this,
            new ConnectionProgressChangedEventArgs(true, $"正在重新连接 {session.Profile.Name}…"));
        try
        {
            await session.ConnectAsync(connectionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Connection was cancelled by user
        }
        finally
        {
            _sessions.EndConnection(session.Id);
            ConnectionProgressChanged?.Invoke(this, new ConnectionProgressChangedEventArgs(false, null));
            if (ReferenceEquals(_connectionCancellation, connectionCancellation))
                _connectionCancellation = null;
            NotifyStateChanged();
        }
    }

    public async Task<bool> CloseSessionAsync(
        IShellSession session,
        Func<IShellSession, Task<bool>> confirmClose)
    {
        if (!_sessions.Contains(session)) return false;
        if (session.IsTransferActive && !await confirmClose(session)) return false;
        if (session is IShellSessionCloseGuard guard && !guard.TryPrepareClose()) return false;

        var wasSelected = ReferenceEquals(_sessions.Selected, session);
        var nextSession = _sessions.Remove(session);
        UnsubscribeSession(session);
        await session.DisposeAsync();
        SessionRemoved?.Invoke(this, session);
        if (nextSession is not null) SelectSession(nextSession, forceActivation: wasSelected);
        else
        {
            _sessions.ClearSelection();
            SessionSelected?.Invoke(this, null);
        }
        NotifyStateChanged();
        return true;
    }

    private async Task<string?> ResolveSecretAsync(ServerProfile profile)
    {
        if (_sessionSecrets.TryGetValue(profile.Id, out var provided)) return provided;
        var secret = await ResolveHopSecretAsync(profile);
        if (secret is not null) _sessionSecrets[profile.Id] = secret;
        return secret;
    }

    private async Task<string?> ResolveHopSecretAsync(ServerProfile profile)
    {
        if (_localStore.TryGetSecret(profile) is string saved)
            return saved;
        if (profile.Authentication == AuthenticationMethod.PrivateKey &&
            !SshConnectionService.RequiresPrivateKeyPassphrase(profile.PrivateKeyPath))
        {
            return string.Empty;
        }

        return await _secretPrompt(profile);
    }

    public void SelectSession(IShellSession session) => SelectSession(session, forceActivation: false);

    private void SelectSession(IShellSession session, bool forceActivation)
    {
        if (!_sessions.Contains(session)) return;
        var activationChanged = forceActivation || !ReferenceEquals(_sessions.Selected, session);
        _sessions.Select(session);
        if (activationChanged)
        {
            foreach (var candidate in _sessions.Sessions)
                candidate.SetActive(ReferenceEquals(candidate, session));
        }
        session.Profile.LastConnectedAt ??= DateTimeOffset.Now;
        SessionSelected?.Invoke(this, session);
        NotifyStateChanged();
    }

    private void SubscribeSession(IShellSession session)
    {
        session.StatusChanged += Session_StatusChanged;
        session.ConnectionFailed += Session_ConnectionFailed;
        session.MetricsUpdated += Session_MetricsUpdated;
    }

    private void UnsubscribeSession(IShellSession session)
    {
        session.StatusChanged -= Session_StatusChanged;
        session.ConnectionFailed -= Session_ConnectionFailed;
        session.MetricsUpdated -= Session_MetricsUpdated;
    }

    private void Session_StatusChanged(object? sender, string status)
    {
        NotifyStateChanged();
    }

    private void Session_ConnectionFailed(object? sender, string message)
    {
        if (sender is IShellSession session)
            ConnectionFailed?.Invoke(this, new ConnectionFailureEventArgs(session.Profile, message));
    }

    private void Session_MetricsUpdated(object? sender, ServerMetrics? metrics)
    {
        if (sender is IShellSession session && metrics is not null)
            MetricsUpdated?.Invoke(this, new SessionMetricsUpdatedEventArgs(session, metrics));
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record ConnectionProgressChangedEventArgs(bool IsActive, string? Message);
public sealed record ConnectionFailureEventArgs(ServerProfile Profile, string Message);
public sealed record SessionMetricsUpdatedEventArgs(IShellSession Session, ServerMetrics Metrics);
