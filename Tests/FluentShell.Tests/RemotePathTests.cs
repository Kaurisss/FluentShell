using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class RemotePathTests
{
    [DataRow("/home/user", "docs", "/home/user/docs")]
    [DataRow("/home/user", "../logs", "/home/logs")]
    [DataRow("/", "../../etc", "/etc")]
    [DataRow("/home/user", "\\var\\log", "/var/log")]
    [TestMethod]
    public void Normalize_resolves_relative_and_parent_segments(
        string currentPath,
        string input,
        string expected)
    {
        Assert.AreEqual(expected, RemotePath.Normalize(currentPath, input));
    }

    [DataRow("/", "/")]
    [DataRow("/home", "/")]
    [DataRow("/home/user/", "/home")]
    [TestMethod]
    public void Parent_stays_within_remote_root(string path, string expected)
    {
        Assert.AreEqual(expected, RemotePath.Parent(path));
    }
}