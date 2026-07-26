using FluentShell.Models;
using Renci.SshNet;

namespace FluentShell.Services;

/// <summary>
/// 一条已建立或待建立的 SSH 传输：终端读写、终端尺寸、指标采集与 SFTP 客户端。
/// 生产适配器是 <see cref="SshConnectionService"/>；重连时整条连接被替换而不是复用。
/// </summary>
public interface ISshConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>未连接时为 <c>null</c>。连接被替换后旧客户端不再可用。</summary>
    SftpClient? SftpClient { get; }

    event EventHandler<string>? OutputReceived;
    event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
    event EventHandler? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendRawAsync(string input);
    Task ResizeTerminalAsync(int columns, int rows);
    Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default);
}
