using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentShell.Models;
using FluentShell.Services;
using Renci.SshNet;

namespace FluentShell.Tests;

[TestClass]
public sealed class SshConnectionServiceTests
{
    [TestMethod]
    public async Task ConnectAsync_honors_cancellation_when_server_does_not_respond()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellationSource = new CancellationTokenSource();
        await using var service = new SshConnectionService(
            new ServerProfile
            {
                Name = "无响应服务器",
                Host = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Username = "user"
            },
            "secret");

        var acceptedClientTask = listener.AcceptTcpClientAsync();
        var connectTask = service.ConnectAsync(cancellationSource.Token);
        using var acceptedClient = await acceptedClientTask.WaitAsync(TimeSpan.FromSeconds(2));

        cancellationSource.Cancel();
        var completedTask = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.AreSame(connectTask, completedTask, "连接取消后不应继续等待网络超时。");
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await connectTask);
    }

    [TestMethod]
    public async Task ConnectAsync_honors_cancellation_when_key_exchange_stalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverCancellationSource = new CancellationTokenSource();
        using var connectionCancellationSource = new CancellationTokenSource();
        var keyExchangeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = StallDuringKeyExchangeAsync(
            listener,
            keyExchangeStarted,
            serverCancellationSource.Token);
        await using var service = new SshConnectionService(
            new ServerProfile
            {
                Name = "密钥交换无响应服务器",
                Host = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Username = "user"
            },
            "secret");

        try
        {
            var connectTask = service.ConnectAsync(connectionCancellationSource.Token);
            await keyExchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            connectionCancellationSource.Cancel();
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.AreSame(connectTask, completedTask, "密钥交换取消后不应继续等待连接超时。");
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await connectTask);
        }
        finally
        {
            serverCancellationSource.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task StallDuringKeyExchangeAsync(
        TcpListener listener,
        TaskCompletionSource keyExchangeStarted,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            await ReadIdentificationAsync(stream, cancellationToken);
            var serverIdentification = Encoding.ASCII.GetBytes("SSH-2.0-test-server\r\n");
            await stream.WriteAsync(serverIdentification, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer, cancellationToken);
            keyExchangeStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (Exception exception)
        {
            keyExchangeStarted.TrySetException(exception);
            throw;
        }
    }

    private static async Task ReadIdentificationAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        do
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                throw new InvalidOperationException("客户端在发送 SSH 标识前断开连接。");
        }
        while (buffer[0] != '\n');
    }

    [TestMethod]
    public async Task CleanupFailedConnectionAsync_waits_for_shell_task_before_completing()
    {
        var shellTcs = new TaskCompletionSource<ShellStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTask = SshConnectionService.CleanupFailedConnectionAsync(
            transferSftpConnectTask: null,
            transferSftpClient: null,
            sftpConnectTask: null,
            sftpClient: null,
            shellTask: shellTcs.Task,
            shell: null,
            sshConnectTask: null,
            sshClient: null,
            privateKeyFiles: Array.Empty<Renci.SshNet.PrivateKeyFile>());

        Assert.IsFalse(cleanupTask.IsCompleted, "清理逻辑必须等待正在执行的 shellTask 完成。");

        shellTcs.SetCanceled();
        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(cleanupTask.IsCompletedSuccessfully, "shellTask 结束后清理应成功结束。");
    }

    [TestMethod]
    public async Task CleanupFailedConnectionAsync_handles_faulted_shell_task_without_throwing()
    {
        var shellTcs = new TaskCompletionSource<ShellStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTask = SshConnectionService.CleanupFailedConnectionAsync(
            transferSftpConnectTask: null,
            transferSftpClient: null,
            sftpConnectTask: null,
            sftpClient: null,
            shellTask: shellTcs.Task,
            shell: null,
            sshConnectTask: null,
            sshClient: null,
            privateKeyFiles: Array.Empty<Renci.SshNet.PrivateKeyFile>());

        shellTcs.SetException(new Renci.SshNet.Common.SshConnectionException("Client not connected."));
        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(cleanupTask.IsCompletedSuccessfully, "清理逻辑应当吞掉底层任务异常并安全完成。");
    }

    [TestMethod]
    public async Task CleanupFailedConnectionAsync_waits_for_connect_tasks_before_completing()
    {
        var sftpConnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sshConnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanupTask = SshConnectionService.CleanupFailedConnectionAsync(
            transferSftpConnectTask: null,
            transferSftpClient: null,
            sftpConnectTask: sftpConnectTcs.Task,
            sftpClient: null,
            shellTask: null,
            shell: null,
            sshConnectTask: sshConnectTcs.Task,
            sshClient: null,
            privateKeyFiles: Array.Empty<Renci.SshNet.PrivateKeyFile>());

        Assert.IsFalse(cleanupTask.IsCompleted, "清理逻辑必须等待正在执行的连接任务完成。");

        sftpConnectTcs.SetResult();
        sshConnectTcs.SetResult();
        await cleanupTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(cleanupTask.IsCompletedSuccessfully);
    }
}
