using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpDirectoryEntryPolicyTests
{
    [TestMethod]
    [DataRow(".env")]
    [DataRow(".ssh")]
    [DataRow("普通文件")]
    public void Includes_hidden_and_regular_directory_entries(string name)
    {
        Assert.IsTrue(SftpDirectoryEntryPolicy.ShouldDisplay(name));
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    public void Excludes_sftp_navigation_entries(string name)
    {
        Assert.IsFalse(SftpDirectoryEntryPolicy.ShouldDisplay(name));
    }
}