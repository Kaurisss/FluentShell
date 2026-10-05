using FluentFTP;
using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>File-only FTP session with independent browsing and transfer control/data connections.</summary>
public sealed class FtpConnectionService : ISshConnection
{
    private readonly AsyncFtpClient _browse;
    private readonly AsyncFtpClient _transfer;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _monitor;
    private bool _disposed;

    public FtpConnectionService(ServerProfile profile, string secret, UserPreferences? preferences = null)
    {
        if (profile.Protocol != ConnectionProtocol.Ftp || profile.Authentication != AuthenticationMethod.Password || profile.JumpProfileId is not null)
            throw new ArgumentException("FTP 仅支持直连和密码认证。");
        FluentFtpClient.ValidateArgument(profile.Host);
        FluentFtpClient.ValidateArgument(profile.Username);
        if (secret.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("FTP 密码不能包含换行符或空字符。");
        var timeout = (preferences ?? new()).Normalize().ConnectionTimeoutSeconds * 1000;
        _browse = CreateClient(profile, secret, timeout);
        _transfer = CreateClient(profile, secret, timeout);
        SftpClient = new FluentFtpClient(_browse);
        TransferSftpClient = new FluentFtpClient(_transfer);
    }

    private static AsyncFtpClient CreateClient(ServerProfile profile, string secret, int timeout) =>
        new(profile.Host, profile.Username, secret, profile.Port, new FtpConfig
        {
            EncryptionMode = FtpEncryptionMode.None,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = timeout,
            ReadTimeout = timeout,
            DataConnectionConnectTimeout = timeout,
            DataConnectionReadTimeout = timeout,
            LogPassword = false,
            LogUserName = false
        });

    public bool IsConnected => !_disposed && _browse.IsConnected && _transfer.IsConnected;
    public ISftpClient? SftpClient { get; }
    public ISftpClient? TransferSftpClient { get; }
    public event EventHandler<string>? OutputReceived { add { } remove { } }
    public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired { add { } remove { } }
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _browse.Connect(linked.Token).ConfigureAwait(false);
            await _transfer.Connect(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            _monitor = MonitorAsync(_lifetime.Token);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                if (!IsConnected)
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public Task SendRawAsync(string input) => Task.CompletedTask;
    public Task ResizeTerminalAsync(int columns, int rows) => Task.CompletedTask;
    public Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult<ServerMetrics?>(null);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_monitor is not null) await _monitor.ConfigureAwait(false);
        await Task.Run(() =>
        {
            try { _transfer.Dispose(); }
            finally { _browse.Dispose(); }
        }).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
