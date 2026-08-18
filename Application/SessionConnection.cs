using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

/// <summary>
/// 一个会话的连接生命周期：状态机、重连、指标轮询与释放顺序。
/// </summary>
/// <remarks>
/// <para>
/// 远程文件 I/O 由 <see cref="RemoteFiles"/> 交付，同一个实例始终指向当前这条连接：
/// 重连换掉底层连接后调用方无需重新取用，也不必知道换过。
/// </para>
/// <para>
/// 本模块不假设自己在 UI 线程上。<c>post</c> 负责把需要回到 UI 线程的两处工作
/// （指纹确认对话框、断开通知）送过去；其余事件在触发它们的线程上原样抛出，
/// 由调用方决定是否编组。
/// </para>
/// </remarks>
public sealed class SessionConnection : IAsyncDisposable
{
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FingerprintConfirmationTimeout = TimeSpan.FromMinutes(2);

    private readonly ServerProfile _profile;
    private readonly Func<string, ISshConnection> _connectionFactory;
    private readonly Func<Task<string?>> _secretProvider;
    private readonly Func<HostFingerprintRequiredEventArgs, Task<bool>> _confirmFingerprint;
    private readonly Action<Action> _post;
    private readonly Action _cancelTransfers;
    private readonly ISftpFileService _remoteFiles;
    private readonly ISftpFileService _transferRemoteFiles;
    private ISshConnection? _active;
    private CancellationTokenSource? _metricsCts;
    private SessionConnectionState _state = SessionConnectionState.Disconnected;
    private bool _isActive;

    public SessionConnection(
        ServerProfile profile,
        Func<string, ISshConnection> connectionFactory,
        Func<Task<string?>> secretProvider,
        Func<HostFingerprintRequiredEventArgs, Task<bool>> confirmFingerprint,
        Action<Action> post,
        Action cancelTransfers)
    {
        _profile = profile;
        _connectionFactory = connectionFactory;
        _secretProvider = secretProvider;
        _confirmFingerprint = confirmFingerprint;
        _post = post;
        _cancelTransfers = cancelTransfers;
        _remoteFiles = new SftpFileService(() => _active?.SftpClient);
        _transferRemoteFiles = new SftpFileService(() => _active?.TransferSftpClient);
    }

    public SessionConnectionState State => _state;
    public bool IsConnected => _active?.IsConnected == true;
    public ISftpFileService RemoteFiles => _remoteFiles;

    /// <summary>传输专用通道上的远程文件 I/O，浏览与传输互不排队。</summary>
    public ISftpFileService TransferRemoteFiles => _transferRemoteFiles;

    /// <summary>写往终端的文本：主机输出与本模块产生的连接提示。</summary>
    public event EventHandler<string>? Output;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ConnectionFailed;
    public event EventHandler<ServerMetrics?>? MetricsUpdated;

    /// <summary>连接建立后触发一次，供调用方做首次远程目录读取一类的后续工作。</summary>
    public event EventHandler? Connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == SessionConnectionState.Connecting || IsConnected) return;
        _state = SessionConnectionState.Connecting;
        StatusChanged?.Invoke(this, "连接中");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var secret = await _secretProvider();
            cancellationToken.ThrowIfCancellationRequested();
            if (secret is null)
            {
                _state = SessionConnectionState.Disconnected;
                StatusChanged?.Invoke(this, "连接已取消");
                return;
            }
            await ConnectWithSecretAsync(secret, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _state = SessionConnectionState.Disconnected;
            StatusChanged?.Invoke(this, "连接已取消");
            throw;
        }
        catch (Exception exception)
        {
            _state = SessionConnectionState.Disconnected;
            StatusChanged?.Invoke(this, $"连接失败：{exception.Message}");
            ConnectionFailed?.Invoke(this, exception.Message);
            Output?.Invoke(this, $"\r\n[连接失败] {exception.Message}\r\n");
        }
    }

    public void SetActive(bool active)
    {
        if (_isActive == active) return;

        _isActive = active;
        if (!active)
        {
            _metricsCts?.Cancel();
            return;
        }

        if (_active is { IsConnected: true } connection) _ = RefreshMetricsLoopAsync(connection);
    }

    public async Task SendAsync(string input)
    {
        var connection = _active;
        if (connection is null || !connection.IsConnected) return;
        try
        {
            await connection.SendRawAsync(input);
        }
        catch (Exception exception)
        {
            Output?.Invoke(this, $"\r\n[发送失败] {exception.Message}\r\n");
        }
    }

    public async Task ResizeTerminalAsync(int columns, int rows)
    {
        var connection = _active;
        if (connection is null || !connection.IsConnected || columns <= 0 || rows <= 0) return;
        try
        {
            await connection.ResizeTerminalAsync(columns, rows);
        }
        catch
        {
        }
    }

    private async Task ConnectWithSecretAsync(string secret, CancellationToken cancellationToken)
    {
        await ReleaseActiveConnectionAsync();

        var connection = _connectionFactory(secret);
        _active = connection;
        Subscribe(connection);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _active = null;
            Unsubscribe(connection);
            await connection.DisposeAsync();
            throw;
        }

        _state = SessionConnectionState.Connected;
        StatusChanged?.Invoke(this, "已连接");
        Output?.Invoke(this, "连接主机成功。\r\n");
        Connected?.Invoke(this, EventArgs.Empty);
        if (_isActive) _ = RefreshMetricsLoopAsync(connection);
    }

    private async Task ReleaseActiveConnectionAsync()
    {
        var previous = _active;
        if (previous is null) return;

        _active = null;
        Unsubscribe(previous);
        await previous.DisposeAsync();
    }

    private async Task RefreshMetricsLoopAsync(ISshConnection connection)
    {
        var previous = _metricsCts;
        var current = new CancellationTokenSource();
        _metricsCts = current;
        previous?.Cancel();
        previous?.Dispose();

        while (!current.IsCancellationRequested && connection.IsConnected)
        {
            var metrics = await connection.ReadLinuxMetricsAsync(current.Token);
            MetricsUpdated?.Invoke(this, metrics);
            try
            {
                await Task.Delay(MetricsInterval, current.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Subscribe(ISshConnection connection)
    {
        connection.OutputReceived += Connection_OutputReceived;
        connection.HostFingerprintRequired += Connection_HostFingerprintRequired;
        connection.Disconnected += Connection_Disconnected;
    }

    private void Unsubscribe(ISshConnection connection)
    {
        connection.OutputReceived -= Connection_OutputReceived;
        connection.HostFingerprintRequired -= Connection_HostFingerprintRequired;
        connection.Disconnected -= Connection_Disconnected;
    }

    private void Connection_OutputReceived(object? sender, string data) => Output?.Invoke(this, data);

    private void Connection_HostFingerprintRequired(
        object? sender,
        HostFingerprintRequiredEventArgs e)
    {
        // SSH.NET 在协议线程上同步等待这个答复，所以这里必须阻塞到对话框返回。
        var signal = new ManualResetEventSlim(false);
        _post(async () =>
        {
            try
            {
                e.Accepted = await _confirmFingerprint(e);
                if (e.Accepted) _profile.HostFingerprint = e.Fingerprint;
            }
            finally
            {
                signal.Set();
            }
        });
        signal.Wait(FingerprintConfirmationTimeout);
    }

    private void Connection_Disconnected(object? sender, EventArgs e) => _post(() =>
    {
        _state = SessionConnectionState.Disconnected;
        StatusChanged?.Invoke(this, "连接已断开");
        Output?.Invoke(this, "\r\n[连接已断开]\r\n");
    });

    public async ValueTask DisposeAsync()
    {
        _metricsCts?.Cancel();
        _cancelTransfers();
        await ReleaseActiveConnectionAsync();
        _metricsCts?.Dispose();
        _metricsCts = null;
    }
}
