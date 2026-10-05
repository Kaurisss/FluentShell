using FluentShell.Models;

namespace FluentShell.Services;

public delegate Task TestServerConnection(
    ServerProfile profile, string enteredSecret,
    Func<HostFingerprintRequiredEventArgs, CancellationToken, Task<bool>> confirmFingerprint,
    CancellationToken cancellationToken);

/// <summary>A short-lived connection, without session registration or persistence.</summary>
public static class ConnectionTest
{
    public static async Task RunAsync(
        ISshConnection connection,
        Func<HostFingerprintRequiredEventArgs, CancellationToken, Task<bool>> confirmFingerprint,
        CancellationToken cancellationToken)
    {
        await using var ownedConnection = connection;
        using var confirmationGate = new SemaphoreSlim(1, 1);
        var trusted = new Dictionary<Guid, string>();
        void Confirm(object? sender, HostFingerprintRequiredEventArgs args)
        {
            confirmationGate.Wait(cancellationToken);
            try
            {
                var id = args.Profile?.Id ?? Guid.Empty;
                if (trusted.TryGetValue(id, out var fingerprint))
                {
                    args.Accepted = string.Equals(fingerprint, args.Fingerprint, StringComparison.OrdinalIgnoreCase);
                    return;
                }
                args.Accepted = confirmFingerprint(args, cancellationToken)
                    .WaitAsync(cancellationToken).GetAwaiter().GetResult();
                if (args.Accepted) trusted[id] = args.Fingerprint;
            }
            finally { confirmationGate.Release(); }
        }
        connection.HostFingerprintRequired += Confirm;
        try
        {
            // Private-key parsing and synchronous host-key callbacks must stay off the UI thread.
            await Task.Run(() => connection.ConnectAsync(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.IsConnected)
                throw new IOException("连接未建立，请检查服务器配置。");
        }
        finally
        {
            connection.HostFingerprintRequired -= Confirm;
        }
    }
}
