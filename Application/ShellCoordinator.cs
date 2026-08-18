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
    bool? RememberCredentials = null);

public sealed class ShellCoordinator
{
    private readonly ILocalStore _localStore;
    private readonly SessionCoordinator<IShellSession> _sessions = new(session => session.Profile.Id);
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
    private string _lastResult = "准备就绪";
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
    public string LastResult => _lastResult;
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
        _settings = await _localStore.LoadAsync();
        NotifyStateChanged();
    }

    public void CancelConnection()
    {
        _connectionCancellation?.Cancel();
    }

    public bool HasSavedCredential(ServerProfile profile) => _localStore.TryGetSecret(profile) is not null;

    public async Task SaveProfileAsync(ServerProfileUpdate update)
    {
        if (update.CredentialIdentityChanged)
            _localStore.RemoveSecret(update.Profile.Id, update.OriginalUsername);
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
        if (update.Theme is not null) _settings.Theme = update.Theme;
        if (update.BackdropMaterial is not null) _settings.BackdropMaterial = update.BackdropMaterial;
        if (update.TerminalFontSize is not null)
        {
            _settings.TerminalFontSize = update.TerminalFontSize.Value;
            foreach (var session in _sessions.Sessions)
                session.SetTerminalFontSize(update.TerminalFontSize.Value);
        }
        if (update.DownloadDirectory is not null)
        {
            _settings.DownloadDirectory = update.DownloadDirectory;
            _settings.HasCustomDownloadDirectory = true;
        }
        if (update.RememberCredentials is not null)
        {
            _settings.RememberCredentials = update.RememberCredentials.Value;
            if (!update.RememberCredentials.Value) _localStore.ClearSecrets();
        }

        await _localStore.SaveSettingsAsync(_settings);
        NotifyStateChanged();
    }

    public async Task ConnectAsync(ServerProfile profile)
    {
        if (_sessions.TryGet(profile.Id, out var existing))
        {
            SelectSession(existing);
            return;
        }
        if (!_sessions.TryBeginConnection(profile.Id)) return;

        using var connectionCancellation = new CancellationTokenSource();
        _connectionCancellation = connectionCancellation;
        var session = _sessionFactory(
            profile,
            () => ResolveSecretAsync(profile),
            _fingerprintConfirmation);
        session.SetTerminalFontSize(_settings.TerminalFontSize);
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
            : _settings.RememberCredentials;
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
        if (!_sessions.TryBeginConnection(session.Profile.Id)) return;

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
            _sessions.EndConnection(session.Profile.Id);
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
        if (_localStore.TryGetSecret(profile) is string saved)
        {
            _sessionSecrets[profile.Id] = saved;
            return saved;
        }
        if (profile.Authentication == AuthenticationMethod.PrivateKey &&
            !SshConnectionService.RequiresPrivateKeyPassphrase(profile.PrivateKeyPath))
        {
            return string.Empty;
        }

        var secret = await _secretPrompt(profile);
        if (secret is not null) _sessionSecrets[profile.Id] = secret;
        return secret;
    }

    private void SelectSession(IShellSession session, bool forceActivation = false)
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
        _lastResult = status;
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
