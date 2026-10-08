using System.Text;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class TextFileServiceTests
{
    [TestMethod]
    public async Task Local_save_checks_external_changes_and_truncates_only_after_comparison()
    {
        var path = Path.GetTempFileName();
        try
        {
            var service = new LocalTextFileService();
            var original = Encoding.UTF8.GetBytes("原始文件\nlong content");
            await File.WriteAllBytesAsync(path, original);
            var loaded = await service.ReadTextFileAsync(path, default);
            CollectionAssert.AreEqual(original, loaded);
            var external = Encoding.UTF8.GetBytes("external change");
            await File.WriteAllBytesAsync(path, external);
            await Assert.ThrowsExactlyAsync<IOException>(() => service.SaveTextFileAsync(path, loaded, [0x61], default));
            CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(path));
            await service.SaveTextFileAsync(path, external, [0x61], default);
            CollectionAssert.AreEqual(new byte[] { 0x61 }, await File.ReadAllBytesAsync(path));
            await service.SaveTextFileAsync(path, new byte[] { 0x61 }, [], default);
            Assert.AreEqual(0L, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Local_deleted_file_is_not_recreated_and_cancelled_save_keeps_content()
    {
        var path = Path.GetTempFileName();
        try
        {
            var service = new LocalTextFileService();
            await File.WriteAllBytesAsync(path, [0x61]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.SaveTextFileAsync(path, new byte[] { 0x61 }, [], cancellation.Token));
            CollectionAssert.AreEqual(new byte[] { 0x61 }, await File.ReadAllBytesAsync(path));
            File.Delete(path);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => service.SaveTextFileAsync(path, new byte[] { 0x61 }, [], default));
            Assert.IsFalse(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Local_oversized_file_is_rejected_before_loading()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var file = File.OpenWrite(path)) file.SetLength(TextFileDocument.MaximumBytes + 1L);
            await Assert.ThrowsExactlyAsync<IOException>(() => new LocalTextFileService().ReadTextFileAsync(path, default));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Remote_save_rechecks_content_and_uses_the_captured_path()
    {
        var current = Encoding.UTF8.GetBytes("original");
        var uploaded = false;
        var client = new FakeSftpClient();
        client.AddFile("config.txt", "/etc/config.txt", current.Length);
        client.DownloadHandler = (path, output, token) =>
        {
            Assert.AreEqual("/etc/config.txt", path);
            return output.WriteAsync(current, token).AsTask();
        };
        client.UploadHandler = async (input, path, _) =>
        {
            Assert.AreEqual("/etc/config.txt", path);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            current = buffer.ToArray();
            uploaded = true;
        };
        var service = new SftpFileService(() => client);
        var original = await service.ReadTextFileAsync("/etc/config.txt", default);
        current = Encoding.UTF8.GetBytes("external");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.SaveTextFileAsync("/etc/config.txt", original, [], default));
        Assert.IsFalse(uploaded);
        await service.SaveTextFileAsync("/etc/config.txt", current, Encoding.UTF8.GetBytes("edited"), default);
        Assert.IsTrue(uploaded);
        Assert.AreEqual("edited", Encoding.UTF8.GetString(current));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("relative.txt")]
    [DataRow("/etc/../config.txt")]
    [DataRow("/etc/./config.txt")]
    [DataRow("/etc//config.txt")]
    [DataRow("/etc/config.txt/")]
    public async Task Remote_invalid_paths_are_rejected_before_network_access(string path)
    {
        var client = new FakeSftpClient();
        await Assert.ThrowsExactlyAsync<IOException>(() => new SftpFileService(() => client).ReadTextFileAsync(path, default));
        Assert.IsEmpty(client.ListedPaths);
    }

    [TestMethod]
    public async Task Remote_links_and_changed_types_are_not_downloaded_or_overwritten()
    {
        var client = new FakeSftpClient { DownloadHandler = (_, _, _) => throw new AssertFailedException("Must not download a link/directory.") };
        var service = new SftpFileService(() => client);
        client.Entries.Add(new("config", "/config", false, 1, default, true));
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ReadTextFileAsync("/config", default));
        await Assert.ThrowsExactlyAsync<IOException>(() => service.SaveTextFileAsync("/config", new byte[] { 0x61 }, [], default));
        client.Entries.Clear();
        client.AddDirectory("config", "/config");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ReadTextFileAsync("/config", default));
    }

    [TestMethod]
    public async Task Remote_download_is_bounded_even_when_reported_length_is_incorrect()
    {
        var client = new FakeSftpClient();
        client.AddFile("large", "/large", 1);
        client.DownloadHandler = async (_, output, token) => await output.WriteAsync(new byte[TextFileDocument.MaximumBytes + 1], token);
        await Assert.ThrowsExactlyAsync<IOException>(() => new SftpFileService(() => client).ReadTextFileAsync("/large", default));
    }

    [TestMethod]
    public async Task Remote_comparison_and_save_share_the_client_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeSftpClient();
        client.AddFile("config", "/config", 1);
        client.DownloadHandler = async (_, output, token) =>
        {
            entered.SetResult();
            await finishRead.Task.WaitAsync(token);
            await output.WriteAsync(new byte[] { 0x61 }, token);
        };
        var service = new SftpFileService(() => client);
        var saving = service.SaveTextFileAsync("/config", new byte[] { 0x61 }, [], default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var listing = service.ListDirectoryAsync("/");
        Assert.IsFalse(listing.IsCompleted);
        finishRead.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        await listing.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
