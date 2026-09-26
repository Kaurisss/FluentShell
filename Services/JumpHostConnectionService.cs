using FluentShell.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FluentShell.Services;

/// <summary>
/// 先认证单级 SSH 跳板，再通过仅绑定本机回环地址的转发通道建立目标的三条连接。
/// 每个工作区独占自己的跳板连接，避免跨会话共享连接的关闭竞态。
/// </summary>
public sealed class JumpHostConnectionService : ISshConnection
{
    private readonly ServerProfile _targetProfile;
    private readonly string _targetSecret;
    private readonly ServerProfile _jumpProfile;
    private readonly string _jumpSecret;
    private SshClient? _jumpClient;
    private ForwardedPortLocal? _forward;
    private SshConnectionService? _target;
    private List<PrivateKeyFile>? _jumpPrivateKeys;

    public JumpHostConnectionService(
        ServerProfile targetProfile,
        string targetSecret,
        ServerProfile jumpProfile,
        string jumpSecret)
    {
        _targetProfile = targetProfile;
        _targetSecret = targetSecret;
        _jumpProfile = jumpProfile;
        _jumpSecret = jumpSecret;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
    public event EventHandler? Disconnected;

    public bool IsConnected => _jumpClient?.IsConnected == true &&
        _forward?.IsStarted == true && _target?.IsConnected == true;
    public ISftpClient? SftpClient => _target?.SftpClient;
    public ISftpClient? TransferSftpClient => _target?.TransferSftpClient;
    public string? LastFingerprint => _target?.LastFingerprint;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SshClient? jumpClient = null;
        Task? jumpConnectTask = null;
        Task? forwardStartTask = null;
        ForwardedPortLocal? forward = null;
        SshConnectionService? target = null;
        var jumpPrivateKeys = new List<PrivateKeyFile>();
        var phase = $"连接跳板服务器“{_jumpProfile.Name}”";
        try
        {
            jumpClient = new SshClient(SshConnectionService.CreateConnectionInfo(
                _jumpProfile, _jumpSecret, jumpPrivateKeys, _jumpProfile.Host, _jumpProfile.Port));
            jumpClient.HostKeyReceived += OnJumpHostKeyReceived;
            jumpConnectTask = jumpClient.ConnectAsync(cancellationToken);
            await jumpConnectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!jumpClient.IsConnected)
                throw new SshConnectionException("跳板服务器连接状态检查失败。");

            forward = new ForwardedPortLocal(
                "127.0.0.1", 0, _targetProfile.Host, (uint)_targetProfile.Port);
            jumpClient.AddForwardedPort(forward);
            forwardStartTask = Task.Run(forward.Start);
            await forwardStartTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            target = new SshConnectionService(
                _targetProfile, _targetSecret, "127.0.0.1", checked((int)forward.BoundPort));
            target.OutputReceived += Target_OutputReceived;
            target.HostFingerprintRequired += Target_HostFingerprintRequired;
            target.Disconnected += Target_Disconnected;
            phase = $"经跳板连接目标服务器“{_targetProfile.Name}”";
            await target.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            _jumpClient = jumpClient;
            _forward = forward;
            _target = target;
            _jumpPrivateKeys = jumpPrivateKeys;
        }
        catch (Exception exception)
        {
            if (target is not null)
            {
                target.OutputReceived -= Target_OutputReceived;
                target.HostFingerprintRequired -= Target_HostFingerprintRequired;
                target.Disconnected -= Target_Disconnected;
                try { await target.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            _ = Task.Run(() => CleanupJumpAsync(jumpConnectTask, forwardStartTask,
                forward, jumpClient, jumpPrivateKeys));
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            throw new IOException($"{phase}失败：{exception.Message}", exception);
        }
    }

    private void OnJumpHostKeyReceived(object? sender, HostKeyEventArgs e) =>
        SshConnectionService.VerifyHostKey(_jumpProfile, e, HostFingerprintRequired);

    private void Target_OutputReceived(object? sender, string output) => OutputReceived?.Invoke(this, output);
    private void Target_HostFingerprintRequired(object? sender, HostFingerprintRequiredEventArgs args) =>
        HostFingerprintRequired?.Invoke(this, args);
    private void Target_Disconnected(object? sender, EventArgs args) => Disconnected?.Invoke(this, args);

    private static async Task CleanupJumpAsync(
        Task? connectTask,
        Task? forwardStartTask,
        ForwardedPortLocal? forward,
        SshClient? jumpClient,
        IEnumerable<PrivateKeyFile> privateKeys)
    {
        if (connectTask is not null)
        {
            try { await connectTask.ConfigureAwait(false); } catch { }
        }
        if (forwardStartTask is not null)
        {
            try { await forwardStartTask.ConfigureAwait(false); } catch { }
        }
        try { forward?.Dispose(); } catch { }
        try { jumpClient?.Disconnect(); } catch { }
        try { jumpClient?.Dispose(); } catch { }
        foreach (var key in privateKeys)
        {
            try { key.Dispose(); } catch { }
        }
    }

    public Task SendRawAsync(string input) =>
        _target?.SendRawAsync(input) ?? Task.FromException(new InvalidOperationException("SSH 尚未连接。"));

    public Task ResizeTerminalAsync(int columns, int rows) =>
        _target?.ResizeTerminalAsync(columns, rows) ?? Task.CompletedTask;

    public Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default) =>
        _target?.ReadLinuxMetricsAsync(cancellationToken) ?? Task.FromResult<ServerMetrics?>(null);

    public async ValueTask DisposeAsync()
    {
        var target = _target;
        _target = null;
        try
        {
            if (target is not null)
            {
                target.OutputReceived -= Target_OutputReceived;
                target.HostFingerprintRequired -= Target_HostFingerprintRequired;
                target.Disconnected -= Target_Disconnected;
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            var jumpClient = _jumpClient;
            _jumpClient = null;
            var forward = _forward;
            _forward = null;
            var jumpPrivateKeys = _jumpPrivateKeys;
            _jumpPrivateKeys = null;
            await Task.Run(() => CleanupJumpAsync(null, null, forward, jumpClient,
                jumpPrivateKeys ?? [])).ConfigureAwait(false);
        }
    }
}
