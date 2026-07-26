using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpFileServiceTests
{
    [TestMethod]
    public async Task Non_root_directory_gets_a_parent_entry_pointing_at_the_parent_path()
    {
        var client = new FakeSftpClient();
        client.AddFile("日志.txt", "/var/log/日志.txt");
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/var/log");

        var parent = items[0];
        Assert.AreEqual("..", parent.Name);
        Assert.AreEqual("/var", parent.FullPath);
        Assert.IsTrue(parent.IsDirectory);
        Assert.AreEqual("目录", parent.TypeLabel);
        Assert.AreEqual("—", parent.SizeLabel);
        Assert.AreEqual(string.Empty, parent.ModifiedLabel);
    }

    [TestMethod]
    public async Task Root_directory_gets_no_parent_entry()
    {
        var client = new FakeSftpClient();
        client.AddFile("初始化.log", "/初始化.log");
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/");

        Assert.AreEqual("初始化.log", items.Single().Name);
    }

    [TestMethod]
    public async Task Directories_sort_before_files_and_each_group_sorts_case_insensitively()
    {
        var client = new FakeSftpClient();
        client.AddFile("beta.txt", "/beta.txt");
        client.AddDirectory("Zulu", "/Zulu");
        client.AddFile("Alpha.txt", "/Alpha.txt");
        client.AddDirectory("apex", "/apex");
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/");

        CollectionAssert.AreEqual(
            new[] { "apex", "Zulu", "Alpha.txt", "beta.txt" },
            items.Select(item => item.Name).ToArray());
    }

    [TestMethod]
    public async Task Dot_and_dot_dot_entries_from_the_host_are_filtered_out()
    {
        var client = new FakeSftpClient();
        client.AddDirectory(".", "/var/log");
        client.AddDirectory("..", "/var");
        client.AddDirectory(".ssh", "/var/log/.ssh");
        client.AddFile("日志.txt", "/var/log/日志.txt");
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/var/log");

        CollectionAssert.AreEqual(
            new[] { "..", ".ssh", "日志.txt" },
            items.Select(item => item.Name).ToArray(),
            "远程返回的 . 与 .. 必须被过滤，列表里的 .. 是合成条目；点开头的普通名称要保留。");
        Assert.AreEqual("/var", items[0].FullPath, "保留下来的 .. 必须是指向父路径的合成条目。");
    }

    [TestMethod]
    public async Task Directories_show_a_dash_for_size_and_files_show_a_formatted_size()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("配置", "/配置");
        client.AddFile("小.bin", "/小.bin", 512);
        client.AddFile("中.bin", "/中.bin", 2048);
        client.AddFile("大.bin", "/大.bin", 5 * 1024 * 1024);
        client.AddFile("巨.bin", "/巨.bin", 3L * 1024 * 1024 * 1024);
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/");

        Assert.AreEqual("—", Find(items, "配置").SizeLabel);
        Assert.AreEqual(-1, Find(items, "配置").SizeBytes);
        Assert.AreEqual("512 B", Find(items, "小.bin").SizeLabel);
        Assert.AreEqual("2.0 KB", Find(items, "中.bin").SizeLabel);
        Assert.AreEqual("5.0 MB", Find(items, "大.bin").SizeLabel);
        Assert.AreEqual("3.0 GB", Find(items, "巨.bin").SizeLabel);
        Assert.AreEqual(2048, Find(items, "中.bin").SizeBytes);
    }

    [TestMethod]
    public async Task Modified_time_is_converted_to_local_time()
    {
        var utcWriteTime = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);
        var expected = utcWriteTime.ToLocalTime();
        var client = new FakeSftpClient();
        client.AddFile("日志.txt", "/日志.txt", 10, utcWriteTime);
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/");

        Assert.AreEqual(expected, items.Single().ModifiedAt);
        Assert.AreEqual(expected.ToString("yyyy-MM-dd HH:mm"), items.Single().ModifiedLabel);
    }

    [TestMethod]
    public async Task Files_are_labelled_as_files_and_directories_as_directories()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("配置", "/配置");
        client.AddFile("日志.txt", "/日志.txt");
        var service = new SftpFileService(() => client);

        var items = await service.ListDirectoryAsync("/");

        Assert.AreEqual("目录", Find(items, "配置").TypeLabel);
        Assert.AreEqual("文件", Find(items, "日志.txt").TypeLabel);
    }

    [TestMethod]
    public async Task Operations_fail_when_the_client_is_disconnected()
    {
        var client = new FakeSftpClient { IsConnected = false };
        var service = new SftpFileService(() => client);

        Assert.IsFalse(service.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ListDirectoryAsync("/"));
    }

    [TestMethod]
    public async Task Operations_fail_when_there_is_no_client_at_all()
    {
        var service = new SftpFileService(() => null);

        Assert.IsFalse(service.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ListDirectoryAsync("/"));
    }

    [TestMethod]
    public async Task Deleting_routes_directories_and_files_to_different_remote_calls()
    {
        var client = new FakeSftpClient();
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem
        {
            Name = "配置",
            IsDirectory = true,
            FullPath = "/配置"
        });
        await service.DeleteAsync(new RemoteFileItem { Name = "日志.txt", FullPath = "/日志.txt" });

        CollectionAssert.AreEqual(new[] { "/配置" }, client.DeletedDirectories);
        CollectionAssert.AreEqual(new[] { "/日志.txt" }, client.DeletedFiles);
    }

    private static RemoteFileItem Find(IReadOnlyList<RemoteFileItem> items, string name) =>
        items.Single(item => item.Name == name);
}
