using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpPathValidatorTests
{
    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("nested/file.txt")]
    [DataRow("nested\\file.txt")]
    [DataRow("C:\\temp\\file.txt")]
    [DataRow("CON")]
    [DataRow("LPT1.txt")]
    [DataRow("report. ")]
    [DataRow("COM1 .txt")]
    public void Download_path_rejects_unsafe_remote_file_names(string name)
    {
        var valid = SftpPathValidator.TryResolveDownloadPath(
            Path.GetTempPath(),
            name,
            out _,
            out _);

        Assert.IsFalse(valid);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("parent/child")]
    [DataRow("parent\\child")]
    public void Remote_name_rejects_paths_and_blank_values(string name)
    {
        Assert.IsFalse(SftpPathValidator.TryValidateRemoteName(name, out _));
    }

    [TestMethod]
    public void Download_path_keeps_unicode_file_inside_selected_directory()
    {
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var valid = SftpPathValidator.TryResolveDownloadPath(
            destination,
            "备份-数据.txt",
            out var localPath,
            out _);

        Assert.IsTrue(valid);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(destination, "备份-数据.txt")),
            localPath);
    }

    [TestMethod]
    public void Remote_name_allows_unicode_file_name()
    {
        Assert.IsTrue(SftpPathValidator.TryValidateRemoteName("资料库", out _));
    }
}