using Renci.SshNet;
using Renci.SshNet.Common;
using FluentShell.Models;

namespace FluentShell.Services;

public sealed class HostFingerprintRequiredEventArgs : EventArgs
{
    public string Fingerprint { get; init; } = string.Empty;
    public string KeyType { get; init; } = string.Empty;
    public bool Accepted { get; set; }
}

public sealed class SshConnectionService : IAsyncDisposable
{
    private readonly ServerProfile _profile;
    private readonly string _secret;
    private SshClient? _sshClient;
    private ShellStream? _shell;
    private SftpClient? _sftpClient;
    private CancellationTokenSource? _readCts;
    private readonly SemaphoreSlim _shellWriteGate = new(1, 1);

    public SshConnectionService(ServerProfile profile, string secret)
    {
        _profile = profile;
        _secret = secret;
    }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<HostFingerprintRequiredEventArgs>? HostFingerprintRequired;
    public event EventHandler? Disconnected;

    public bool IsConnected => _sshClient?.IsConnected == true;
    public SftpClient? SftpClient => _sftpClient;
    public string? LastFingerprint { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => ConnectCore(cancellationToken), cancellationToken);
    }

    private void ConnectCore(CancellationToken cancellationToken)
    {
        var sshConnectionInfo = CreateConnectionInfo();
        _sshClient = new SshClient(sshConnectionInfo);
        _sshClient.HostKeyReceived += OnHostKeyReceived;
        _sshClient.Connect();
        cancellationToken.ThrowIfCancellationRequested();

        _shell = _sshClient.CreateShellStream("xterm", 120, 32, 1200, 800, 4096);
        _sftpClient = new SftpClient(CreateConnectionInfo());
        _sftpClient.HostKeyReceived += OnHostKeyReceived;
        _sftpClient.Connect();
        _readCts = new CancellationTokenSource();
        _ = ReadOutputLoopAsync(_readCts.Token);
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        Renci.SshNet.AuthenticationMethod auth = _profile.Authentication switch
        {
            FluentShell.Models.AuthenticationMethod.PrivateKey => new PrivateKeyAuthenticationMethod(_profile.Username, new PrivateKeyFile(_profile.PrivateKeyPath, string.IsNullOrWhiteSpace(_secret) ? null : _secret)),
            _ => new PasswordAuthenticationMethod(_profile.Username, _secret)
        };

        return new ConnectionInfo(_profile.Host, _profile.Port, _profile.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var fingerprint = Convert.ToHexString(e.FingerPrint);
        LastFingerprint = fingerprint;
        var args = new HostFingerprintRequiredEventArgs
        {
            Fingerprint = fingerprint,
            KeyType = e.HostKeyName,
            Accepted = !string.IsNullOrWhiteSpace(_profile.HostFingerprint) && string.Equals(_profile.HostFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
        };

        if (string.IsNullOrWhiteSpace(_profile.HostFingerprint))
        {
            HostFingerprintRequired?.Invoke(this, args);
        }

        e.CanTrust = args.Accepted;
        if (!string.IsNullOrWhiteSpace(_profile.HostFingerprint) && !args.Accepted)
        {
            throw new SshConnectionException("服务器指纹已变化，连接被拒绝。");
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
            const string metricsCommand = "LC_ALL=C top -bn1 | awk '/Cpu\\(s\\)/ {print \"CpuUsage=\" $2+$4}'; cat /proc/loadavg; awk '/MemTotal|MemAvailable|SwapTotal|SwapFree/ {gsub(\":\", \"\", $1); printf \"%s=%s\\n\", $1, $2}' /proc/meminfo; uname -sr; hostname; uptime -p";
            var command = await Task.Run(() => _sshClient!.RunCommand(metricsCommand).Result, cancellationToken);
            return ParseMetrics(command);
        }
        catch { return null; }
    }

    private static ServerMetrics ParseMetrics(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var load = lines.FirstOrDefault(line => !line.Contains('=') && line.Count(character => character == ' ') >= 2) ?? "";
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
            CpuPercent = Math.Clamp(values.GetValueOrDefault("CpuUsage"), 0, 100),
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
        await _shellWriteGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                try { _sftpClient?.Disconnect(); } catch { }
                try { _sftpClient?.Dispose(); } catch { }
                try { _sshClient?.Disconnect(); } catch { }
                try { _sshClient?.Dispose(); } catch { }
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
