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
    bool IsConnected { get; }
    SessionConnectionState ConnectionState { get; }
    bool IsTransferActive { get; }

    event EventHandler<ServerMetrics?> MetricsUpdated;
    event EventHandler<string> StatusChanged;
    event EventHandler<string> ConnectionFailed;

    Task ConnectAsync();
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
    private readonly SettingsStore _settingsStore;
    private readonly CredentialService _credentialService;
    private readonly ServerCatalog _serverCatalog;
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

    public ShellCoordinator(
        SettingsStore settingsStore,
        CredentialService credentialService,
        ServerCatalog serverCatalog,
        Func<ServerProfile, Func<Task<string?>>, Func<HostFingerprintRequiredEventArgs, Task<bool>>, IShellSession> sessionFactory,
        Func<ServerProfile, Task<string?>> secretPrompt,
        Func<HostFingerprintRequiredEventArgs, Task<bool>> fingerprintConfirmation)
    {
        _settingsStore = settingsStore;
        _credentialService = credentialService;
        _serverCatalog = serverCatalog;
        _sessionFactory = sessionFactory;
        _secretPrompt = secretPrompt;
        _fingerprintConfirmation = fingerprintConfirmation;
    }

    public IReadOnlyList<ServerProfile> Profiles => _serverCatalog.Profiles;
    public string DataFolder => _serverCatalog.DataFolder;
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
        await _serverCatalog.LoadAsync();
        _settings = await _settingsStore.LoadAsync();
        NotifyStateChanged();
    }

    public bool HasSavedCredential(ServerProfile profile) => _credentialService.TryGet(profile) is not null;

    public async Task SaveProfileAsync(ServerProfileUpdate update)
    {
        if (update.CredentialIdentityChanged)
            _credentialService.Remove(update.Profile.Id, update.OriginalUsername);
        if (update.SaveCredential)
        {
            if (!string.IsNullOrEmpty(update.EnteredSecret))
                _credentialService.Save(update.Profile, update.EnteredSecret);
        }
        else
        {
            _credentialService.Remove(update.Profile);
        }

        await _serverCatalog.AddOrUpdateAsync(update.Profile);
        NotifyStateChanged();
        if (!update.ConnectAfterSave) return;

        _credentialPersistenceOverrides[update.Profile.Id] = update.SaveCredential;
        if (!string.IsNullOrEmpty(update.EnteredSecret))
            _sessionSecrets[update.Profile.Id] = update.EnteredSecret;
        await ConnectAsync(update.Profile);
    }

    public async Task CopyProfileAsync(ServerProfile profile)
    {
        await _serverCatalog.CopyAsync(profile);
        NotifyStateChanged();
    }

    public async Task DeleteProfileAsync(ServerProfile profile)
    {
        await _serverCatalog.DeleteAsync(profile);
        NotifyStateChanged();
    }

    public void ClearLocalData()
    {
        _serverCatalog.Clear();
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
            _settings.DownloadDirectory = update.DownloadDirectory;
        if (update.RememberCredentials is not null)
        {
            _settings.RememberCredentials = update.RememberCredentials.Value;
            if (!update.RememberCredentials.Value) _credentialService.ClearAll();
        }

        await _settingsStore.SaveAsync(_settings);
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
            await session.ConnectAsync();
        }
        finally
        {
            _sessions.EndConnection(profile.Id);
            ConnectionProgressChanged?.Invoke(this, new ConnectionProgressChangedEventArgs(false, null));
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
            _credentialService.Save(profile, secret);
        else if (!shouldPersistCredential)
            _credentialService.Remove(profile);
        _sessionSecrets.Remove(profile.Id);
        await _serverCatalog.SaveAsync();
        NotifyStateChanged();
    }

    public Task ReconnectSelectedSessionAsync() =>
        SelectedSession is null ? Task.CompletedTask : ReconnectAsync(SelectedSession);

    private async Task ReconnectAsync(IShellSession session)
    {
        if (!_sessions.Contains(session) || session.ConnectionState == SessionConnectionState.Connecting || session.IsConnected)
            return;
        if (!_sessions.TryBeginConnection(session.Profile.Id)) return;

        ConnectionProgressChanged?.Invoke(
            this,
            new ConnectionProgressChangedEventArgs(true, $"正在重新连接 {session.Profile.Name}…"));
        try
        {
            await session.ConnectAsync();
        }
        finally
        {
            _sessions.EndConnection(session.Profile.Id);
            ConnectionProgressChanged?.Invoke(this, new ConnectionProgressChangedEventArgs(false, null));
            NotifyStateChanged();
        }
    }

    public async Task<bool> CloseSessionAsync(
        IShellSession session,
        Func<IShellSession, Task<bool>> confirmClose)
    {
        if (!_sessions.Contains(session)) return false;
        if (session.IsTransferActive && !await confirmClose(session)) return false;

        var nextSession = _sessions.Remove(session);
        UnsubscribeSession(session);
        await session.DisposeAsync();
        SessionRemoved?.Invoke(this, session);
        if (nextSession is not null) SelectSession(nextSession);
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
        if (_credentialService.TryGet(profile) is string saved)
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

    private void SelectSession(IShellSession session)
    {
        if (!_sessions.Contains(session)) return;
        _sessions.Select(session);
        foreach (var candidate in _sessions.Sessions)
            candidate.SetActive(ReferenceEquals(candidate, session));
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
