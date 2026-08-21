using Renci.SshNet;
using Renci.SshNet.Common;
using FluentShell.Models;
using Org.BouncyCastle.Crypto;

namespace FluentShell.Services;

public sealed class HostFingerprintRequiredEventArgs : EventArgs
{
    public string Fingerprint { get; init; } = string.Empty;
    public string KeyType { get; init; } = string.Empty;
    public bool Accepted { get; set; }
}

public sealed class SshConnectionService : ISshConnection
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(12);
    private readonly ServerProfile _profile;
    private readonly string _secret;
    private SshClient? _sshClient;
    private ShellStream? _shell;
    private SftpClient? _sftpClient;
    private SftpClient? _transferSftpClient;
    private ISftpClient? _remoteFileClient;
    private ISftpClient? _transferFileClient;
    private List<PrivateKeyFile>? _privateKeyFiles;
    private CancellationTokenSource? _readCts;
    private readonly SemaphoreSlim _shellWriteGate = new(1, 1);
    private readonly SemaphoreSlim _metricsCommandGate = new(1, 1);
    private readonly LinuxCpuUsageCalculator _cpuUsageCalculator = new();

    public SshConnectionService(ServerProfile profile, string secret)
    {
        _profile = profile;
        _secret = secret;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
    public event EventHandler? Disconnected;

    public bool IsConnected => _sshClient?.IsConnected == true;
    public ISftpClient? SftpClient => _remoteFileClient;
    public ISftpClient? TransferSftpClient => _transferFileClient;
    public string? LastFingerprint { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SshClient? sshClient = null;
        Task? sshConnectTask = null;
        Task<ShellStream>? shellTask = null;
        ShellStream? shell = null;
        SftpClient? sftpClient = null;
        Task? sftpConnectTask = null;
        SftpClient? transferSftpClient = null;
        Task? transferSftpConnectTask = null;
        var privateKeyFiles = new List<PrivateKeyFile>();
        try
        {
            sshClient = new SshClient(CreateConnectionInfo(privateKeyFiles));
            sshClient.HostKeyReceived += OnHostKeyReceived;
            sshConnectTask = sshClient.ConnectAsync(cancellationToken);
            await AwaitOperationAsync(sshConnectTask, cancellationToken).ConfigureAwait(false);

            if (!sshClient.IsConnected)
            {
                throw new SshConnectionException("SSH 客户端报告已连接，但连接状态检查失败。");
            }

            shellTask = Task.Run(
                () => sshClient.CreateShellStream("xterm", 120, 32, 1200, 800, 4096),
                cancellationToken);
            shell = await AwaitOperationAsync(shellTask, cancellationToken).ConfigureAwait(false);

            sftpClient = new SftpClient(CreateConnectionInfo(privateKeyFiles));
            sftpClient.HostKeyReceived += OnHostKeyReceived;
            sftpConnectTask = sftpClient.ConnectAsync(cancellationToken);
            await AwaitOperationAsync(sftpConnectTask, cancellationToken).ConfigureAwait(false);

            // 传输走独立连接：SSH.NET 客户端不保证并发安全，
            // 浏览目录不该排在大文件传输后面。
            transferSftpClient = new SftpClient(CreateConnectionInfo(privateKeyFiles));
            transferSftpClient.HostKeyReceived += OnHostKeyReceived;
            transferSftpConnectTask = transferSftpClient.ConnectAsync(cancellationToken);
            await AwaitOperationAsync(transferSftpConnectTask, cancellationToken).ConfigureAwait(false);

            _sshClient = sshClient;
            _shell = shell;
            _sftpClient = sftpClient;
            _transferSftpClient = transferSftpClient;
            _remoteFileClient = new SshNetSftpClient(sftpClient);
            _transferFileClient = new SshNetSftpClient(transferSftpClient);
            _privateKeyFiles = privateKeyFiles;
            _readCts = new CancellationTokenSource();
            _ = ReadOutputLoopAsync(_readCts.Token);
        }
        catch
        {
            _sshClient = null;
            _shell = null;
            _sftpClient = null;
            _transferSftpClient = null;
            _remoteFileClient = null;
            _transferFileClient = null;
            ScheduleFailedConnectionCleanup(
                transferSftpConnectTask,
                transferSftpClient,
                sftpConnectTask,
                sftpClient,
                shellTask,
                shell,
                sshConnectTask,
                sshClient,
                privateKeyFiles);
            throw;
        }
    }

    private static async Task AwaitOperationAsync(
        Task operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveFault(operation);
            throw;
        }
    }

    private static async Task<T> AwaitOperationAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveFault(operation);
            throw;
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completedTask => { _ = completedTask.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void ScheduleFailedConnectionCleanup(
        Task? transferSftpConnectTask,
        BaseClient? transferSftpClient,
        Task? sftpConnectTask,
        BaseClient? sftpClient,
        Task<ShellStream>? shellTask,
        ShellStream? shell,
        Task? sshConnectTask,
        BaseClient? sshClient,
        IReadOnlyCollection<PrivateKeyFile> privateKeyFiles)
    {
        _ = Task.Run(async () =>
        {
            await CleanupFailedConnectionAsync(
                transferSftpConnectTask,
                transferSftpClient,
                sftpConnectTask,
                sftpClient,
                shellTask,
                shell,
                sshConnectTask,
                sshClient,
                privateKeyFiles).ConfigureAwait(false);
        });
    }

    internal static async Task CleanupFailedConnectionAsync(
        Task? transferSftpConnectTask,
        BaseClient? transferSftpClient,
        Task? sftpConnectTask,
        BaseClient? sftpClient,
        Task<ShellStream>? shellTask,
        ShellStream? shell,
        Task? sshConnectTask,
        BaseClient? sshClient,
        IReadOnlyCollection<PrivateKeyFile> privateKeyFiles)
    {
        if (transferSftpConnectTask is not null)
        {
            await AwaitTaskCompletionSafelyAsync(transferSftpConnectTask).ConfigureAwait(false);
        }
        DisposeClient(transferSftpClient);

        if (sftpConnectTask is not null)
        {
            await AwaitTaskCompletionSafelyAsync(sftpConnectTask).ConfigureAwait(false);
        }
        DisposeClient(sftpClient);

        if (shellTask is not null)
        {
            await AwaitTaskCompletionSafelyAsync(shellTask).ConfigureAwait(false);
            if (shellTask.Status == TaskStatus.RanToCompletion)
            {
                DisposeShell(shellTask.Result);
            }
        }
        else if (shell is not null)
        {
            DisposeShell(shell);
        }

        if (sshConnectTask is not null)
        {
            await AwaitTaskCompletionSafelyAsync(sshConnectTask).ConfigureAwait(false);
        }
        DisposeClient(sshClient);

        DisposePrivateKeyFiles(privateKeyFiles);
    }

    private static async Task AwaitTaskCompletionSafelyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 忽略操作在取消或网络断开时抛出的异常，确保清理继续执行。
        }
    }

    private static void DisposeShell(ShellStream? shell)
    {
        if (shell is null) return;

        try { shell.Dispose(); } catch { }
    }

    private static void DisposeClient(BaseClient? client)
    {
        if (client is null) return;

        try { client.Dispose(); } catch { }
    }

    private static void DisposePrivateKeyFiles(IEnumerable<PrivateKeyFile> privateKeyFiles)
    {
        foreach (var privateKeyFile in privateKeyFiles)
        {
            try { privateKeyFile.Dispose(); } catch { }
        }
    }

    internal static bool RequiresPrivateKeyPassphrase(string privateKeyPath)
    {
        using var privateKeyStream = new FileStream(
            privateKeyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return RequiresPrivateKeyPassphrase(privateKeyStream);
    }

    internal static bool RequiresPrivateKeyPassphrase(Stream privateKeyStream)
    {
        if (!PrivateKeyValidator.HasSupportedPrivateKeyHeader(privateKeyStream))
            throw new SshException(PrivateKeyValidator.InvalidFormatMessage);

        try
        {
            using var privateKeyFile = new PrivateKeyFile(privateKeyStream, null);
            return false;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return true;
        }
        catch (InvalidCipherTextException)
        {
            return true;
        }
    }

    private ConnectionInfo CreateConnectionInfo(ICollection<PrivateKeyFile> privateKeyFiles)
    {
        Renci.SshNet.AuthenticationMethod auth = _profile.Authentication switch
        {
            FluentShell.Models.AuthenticationMethod.PrivateKey =>
                CreatePrivateKeyAuthenticationMethod(privateKeyFiles),
            _ => new PasswordAuthenticationMethod(_profile.Username, _secret)
        };

        return new ConnectionInfo(_profile.Host, _profile.Port, _profile.Username, auth)
        {
            Timeout = ConnectionTimeout
        };
    }

    private PrivateKeyAuthenticationMethod CreatePrivateKeyAuthenticationMethod(
        ICollection<PrivateKeyFile> privateKeyFiles)
    {
        var privateKeyFile = CreatePrivateKeyFile();
        try
        {
            var authenticationMethod = new PrivateKeyAuthenticationMethod(_profile.Username, privateKeyFile);
            privateKeyFiles.Add(privateKeyFile);
            return authenticationMethod;
        }
        catch
        {
            privateKeyFile.Dispose();
            throw;
        }
    }

    private PrivateKeyFile CreatePrivateKeyFile()
    {
        // SSH.NET 在构造期间同步解析私钥，不保留输入流；因此可以立即释放文件句柄。
        // 返回的对象由连接服务在对应客户端停止使用后释放。
        using var privateKeyStream = new FileStream(
            _profile.PrivateKeyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (!PrivateKeyValidator.HasSupportedPrivateKeyHeader(privateKeyStream))
            throw new SshException(PrivateKeyValidator.InvalidFormatMessage);

        return new PrivateKeyFile(
            privateKeyStream,
            string.IsNullOrWhiteSpace(_secret) ? null : _secret);
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var fingerprint = Convert.ToHexString(e.FingerPrint);
        LastFingerprint = fingerprint;
        var storedFingerprint = _profile.HostFingerprint;
        var args = new HostFingerprintRequiredEventArgs
        {
            Fingerprint = fingerprint,
            KeyType = e.HostKeyName,
            Accepted = !string.IsNullOrWhiteSpace(storedFingerprint) && string.Equals(storedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
        };

        if (string.IsNullOrWhiteSpace(storedFingerprint))
        {
            HostFingerprintRequired?.Invoke(this, args);
        }

        e.CanTrust = args.Accepted;
        if (!string.IsNullOrWhiteSpace(storedFingerprint) && !args.Accepted)
        {
            throw new SshConnectionException($"服务器指纹已变化，连接被拒绝。\n存储: {storedFingerprint}\n当前: {fingerprint}");
        }
    }

    private async Task ReadOutputLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _shell is not null && IsConnected)
        {
            try
            {
                if (_shell.DataAvailable)
                {
                    var output = _shell.Read();
                    if (!string.IsNullOrEmpty(output)) OutputReceived?.Invoke(this, output);
                }
                await Task.Delay(60, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OutputReceived?.Invoke(this, $"\r\n[读取终端失败] {ex.Message}\r\n");
                break;
            }
        }
        if (!cancellationToken.IsCancellationRequested) Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public Task SendAsync(string command, bool appendNewLine = true)
        => SendRawAsync(appendNewLine ? command + "\r" : command);

    public async Task SendRawAsync(string input)
    {
        var shell = _shell;
        if (shell is null || !IsConnected) throw new InvalidOperationException("SSH 尚未连接。");
        await _shellWriteGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                shell.Write(input);
                shell.Flush();
            });
        }
        finally
        {
            _shellWriteGate.Release();
        }
    }

    public Task ResizeTerminalAsync(int columns, int rows)
    {
        var shell = _shell;
        if (shell is null || !IsConnected) return Task.CompletedTask;
        var safeColumns = (uint)Math.Clamp(columns, 1, 500);
        var safeRows = (uint)Math.Clamp(rows, 1, 200);
        var pixelWidth = (uint)Math.Clamp(columns * 8, 1, 4000);
        var pixelHeight = (uint)Math.Clamp(rows * 16, 1, 4000);
        return Task.Run(() => shell.ChangeWindowSize(safeColumns, safeRows, pixelWidth, pixelHeight));
    }

    public async Task<ServerMetrics?> ReadLinuxMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return null;
        try
        {
            await _metricsCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsConnected) return null;

                const string metricsCommand = "head -n 1 /proc/stat; cat /proc/loadavg; awk '/MemTotal|MemAvailable|SwapTotal|SwapFree/ {gsub(\":\", \"\", $1); printf \"%s=%s\\n\", $1, $2}' /proc/meminfo; uname -sr; hostname; uptime -p";
                using var command = _sshClient!.CreateCommand(metricsCommand);
                // SSH.NET waits synchronously for channel-open before returning ExecuteAsync's task.
                await Task.Run(
                    () => command.ExecuteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return ParseMetrics(command.Result);
            }
            finally
            {
                _metricsCommandGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private ServerMetrics ParseMetrics(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var cpuPercent = _cpuUsageCalculator.AddSample(lines.FirstOrDefault() ?? string.Empty);
        var load = lines.FirstOrDefault(line =>
            !line.StartsWith("cpu ", StringComparison.Ordinal) &&
            !line.Contains('=') &&
            line.Count(character => character == ' ') >= 2) ?? "";
        var values = lines.Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2))
            .GroupBy(parts => parts[0])
            .ToDictionary(group => group.Key, group => double.TryParse(group.Last().ElementAtOrDefault(1), out var value) ? value : 0);
        var total = values.GetValueOrDefault("MemTotal");
        var available = values.GetValueOrDefault("MemAvailable");
        var swapTotal = values.GetValueOrDefault("SwapTotal");
        var swapFree = values.GetValueOrDefault("SwapFree");
        var memory = total <= 0 ? 0 : (total - available) / total * 100;
        var swap = swapTotal <= 0 ? 0 : (swapTotal - swapFree) / swapTotal * 100;
        return new ServerMetrics
        {
            CpuPercent = cpuPercent,
            MemoryPercent = Math.Clamp(memory, 0, 100),
            SwapPercent = Math.Clamp(swap, 0, 100),
            LoadAverage = load.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "—",
            OperatingSystem = lines.FirstOrDefault(line => line.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)) ?? "Linux",
            Hostname = lines.ElementAtOrDefault(lines.Length - 2) ?? "—",
            Uptime = lines.LastOrDefault(line => line.StartsWith("up ", StringComparison.OrdinalIgnoreCase)) ?? "—"
        };
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        var privateKeyFiles = _privateKeyFiles;
        _privateKeyFiles = null;
        var shell = _shell;
        _shell = null;
        var transferSftpClient = _transferSftpClient;
        _transferSftpClient = null;
        var sftpClient = _sftpClient;
        _sftpClient = null;
        var sshClient = _sshClient;
        _sshClient = null;
        _remoteFileClient = null;
        _transferFileClient = null;

        await _shellWriteGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                try { transferSftpClient?.Disconnect(); } catch { }
                DisposeClient(transferSftpClient);
                try { sftpClient?.Disconnect(); } catch { }
                DisposeClient(sftpClient);
                DisposeShell(shell);
                try { sshClient?.Disconnect(); } catch { }
                DisposeClient(sshClient);
                if (privateKeyFiles is not null)
                    DisposePrivateKeyFiles(privateKeyFiles);
            });
            _readCts?.Dispose();
        }
        finally
        {
            _shellWriteGate.Release();
            _shellWriteGate.Dispose();
        }
    }
}
