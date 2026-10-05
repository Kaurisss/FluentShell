using System.Text.Json;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class ConnectionProtocolTests
{
    [TestMethod]
    public void Legacy_profiles_remain_ssh_and_protocol_roundtrips()
    {
        Assert.AreEqual(ConnectionProtocol.Ssh, JsonSerializer.Deserialize<ServerProfile>("{\"Host\":\"test\"}")!.Protocol);
        foreach (var protocol in Enum.GetValues<ConnectionProtocol>())
        {
            var profile = new ServerProfile { Protocol = protocol };
            Assert.AreEqual(protocol, JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(profile))!.Protocol);
            Assert.AreEqual(protocol, new ServerProfileList().AddCopyOf(profile).Protocol);
            Assert.AreEqual(protocol == ConnectionProtocol.Ssh, profile.SupportsTerminal);
            Assert.AreEqual(protocol == ConnectionProtocol.Ftp ? 21 : 22, ServerProfile.DefaultPort(protocol));
        }
    }

    [TestMethod]
    public void Protocol_is_part_of_duplicate_identity()
    {
        var first = new ServerProfile { Host = "host", Username = "user" };
        var second = new ServerProfile { Host = "host", Username = "user", Protocol = ConnectionProtocol.Sftp };
        Assert.IsFalse(new ServerProfileValidator().CheckForDuplicate([first], second).IsDuplicate);
        second.Protocol = first.Protocol;
        Assert.IsTrue(new ServerProfileValidator().CheckForDuplicate([first], second).IsDuplicate);
    }

    [TestMethod]
    public void Only_ssh_profiles_can_be_jump_hosts_and_ftp_cannot_use_a_jump()
    {
        var target = new ServerProfile { Protocol = ConnectionProtocol.Sftp };
        var hop = new ServerProfile();
        Assert.IsTrue(JumpHostResolver.IsEligible(target, hop));
        hop.Protocol = ConnectionProtocol.Sftp;
        Assert.IsFalse(JumpHostResolver.IsEligible(target, hop));
        hop.Protocol = ConnectionProtocol.Ssh;
        target.Protocol = ConnectionProtocol.Ftp;
        Assert.IsFalse(JumpHostResolver.IsEligible(target, hop));
    }

    [TestMethod]
    [DataRow("/file\r\nDELE /other")]
    [DataRow("/file\0")]
    [DataRow("")]
    public void Ftp_rejects_command_delimiters(string path) =>
        Assert.Throws<ArgumentException>(() => FluentFtpClient.ValidateArgument(path));

    [TestMethod]
    public async Task Cancelled_ftp_connection_releases_both_channels()
    {
        await using var connection = new FtpConnectionService(new ServerProfile
        {
            Protocol = ConnectionProtocol.Ftp, Host = "127.0.0.1", Username = "fixture", Port = 21
        }, "synthetic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ConnectAsync(cancellation.Token));
        Assert.IsFalse(connection.IsConnected);
    }
}
