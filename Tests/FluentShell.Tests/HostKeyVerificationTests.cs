using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using FluentShell.Models;
using FluentShell.Services;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace FluentShell.Tests;

[TestClass]
public sealed class HostKeyVerificationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task New_key_requires_explicit_confirmation_for_direct_and_jump_hosts(bool jump, bool accept)
    {
        var profile = new ServerProfile { Host = "offline.invalid" };
        await using var connection = CreateConnection(profile, jump);
        var key = CreateHostKey();
        HostFingerprintRequiredEventArgs? request = null;
        connection.HostFingerprintRequired += (_, args) => { request = args; args.Accepted = accept; };

        ReceiveHostKey(connection, key);

        Assert.IsNotNull(request);
        Assert.AreSame(profile, request.Profile);
        Assert.AreEqual(Convert.ToHexString(key.FingerPrint), request.Fingerprint);
        Assert.AreEqual("ssh-rsa", request.KeyType);
        Assert.AreEqual(accept, key.CanTrust);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Saved_matching_key_is_trusted_without_prompting(bool jump)
    {
        var key = CreateHostKey();
        var profile = new ServerProfile { HostFingerprint = Convert.ToHexString(key.FingerPrint).ToLowerInvariant() };
        await using var connection = CreateConnection(profile, jump);
        var prompted = false;
        connection.HostFingerprintRequired += (_, _) => prompted = true;

        ReceiveHostKey(connection, key);

        Assert.IsTrue(key.CanTrust);
        Assert.IsFalse(prompted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Changed_saved_key_is_rejected_without_confirmation_bypass(bool jump)
    {
        var key = CreateHostKey();
        var profile = new ServerProfile { HostFingerprint = "DIFFERENT-SYNTHETIC-KEY" };
        await using var connection = CreateConnection(profile, jump);
        var prompted = false;
        connection.HostFingerprintRequired += (_, args) => { prompted = true; args.Accepted = true; };

        Assert.Throws<SshConnectionException>(() => ReceiveHostKey(connection, key));

        Assert.IsFalse(key.CanTrust);
        Assert.IsFalse(prompted);
        Assert.AreEqual("DIFFERENT-SYNTHETIC-KEY", profile.HostFingerprint);
    }

    private static ISshConnection CreateConnection(ServerProfile profile, bool jump) => jump
        ? new JumpHostConnectionService(new ServerProfile { Host = "target.invalid" }, "fixture", profile, "fixture")
        : new SshConnectionService(profile, "fixture");

    private static HostKeyEventArgs CreateHostKey()
    {
        using var rsa = RSA.Create(1024);
        using var key = new RsaKey(rsa.ExportRSAPrivateKey());
        return new HostKeyEventArgs(new KeyHostAlgorithm("ssh-rsa", key));
    }

    private static void ReceiveHostKey(ISshConnection connection, HostKeyEventArgs args)
    {
        // Exercise the handlers registered on SSH.NET, without opening a socket.
        var method = connection.GetType().GetMethod(connection is JumpHostConnectionService
            ? "OnJumpHostKeyReceived" : "OnHostKeyReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try { method.Invoke(connection, [connection, args]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }
}
