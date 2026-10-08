using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpFileServiceTests
{
    [TestMethod]
    public async Task Listing_preserves_symbolic_link_metadata()
    {
        var client = new FakeSftpClient();
        client.Entries.Add(new RemoteDirectoryEntry("链接", "/链接", false, 0, default, IsSymbolicLink: true));
        var service = new SftpFileService(() => client);
        var item = (await service.ListDirectoryAsync("/")).Single();
        Assert.IsTrue(item.IsSymbolicLink);
    }

    [TestMethod]
    public async Task Directory_type_check_uses_metadata_without_listing_contents()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("资料", "/资料");
        client.AddFile("文件.txt", "/文件.txt");
        var service = new SftpFileService(() => client);
        Assert.IsTrue(await service.IsDirectoryAsync("/资料"));
        Assert.IsFalse(await service.IsDirectoryAsync("/文件.txt"));
        Assert.IsNull(client.LastListedPath);
    }

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
        client.AddDirectory("配置", "/配置");
        client.AddFile("日志.txt", "/日志.txt");
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

    [TestMethod]
    public async Task Deleting_non_empty_directory_removes_nested_and_hidden_contents_before_parents()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("Mod", "/Mod");
        client.AddDirectory(".", "/Mod");
        client.AddDirectory("..", "/");
        client.AddFile(".hidden", "/Mod/.hidden");
        client.AddDirectory("nested", "/Mod/nested");
        client.AddFile("file.txt", "/Mod/nested/file.txt");
        client.AddDirectory("empty", "/Mod/empty");
        client.AddFile("keep.txt", "/keep.txt");
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true });

        CollectionAssert.AreEqual(new[]
        {
            (false, "/Mod/.hidden"), (false, "/Mod/nested/file.txt"),
            (true, "/Mod/nested"), (true, "/Mod/empty"), (true, "/Mod")
        }, client.Deletions);
        Assert.IsTrue(client.Entries.Any(entry => entry.FullPath == "/keep.txt"));
        Assert.IsFalse(client.Entries.Any(entry => entry.Name is not "." and not ".." && entry.FullPath.StartsWith("/Mod")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deleting_symbolic_link_removes_only_the_link_even_with_stale_directory_metadata(bool staleMetadata)
    {
        var client = new FakeSftpClient();
        client.Entries.Add(new RemoteDirectoryEntry("link", "/link", true, 0, default, IsSymbolicLink: true));
        client.AddFile("keep.txt", "/link/keep.txt");
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem
        {
            Name = "link", FullPath = "/link", IsDirectory = true, IsSymbolicLink = !staleMetadata
        });

        CollectionAssert.AreEqual(new[] { "/link" }, client.DeletedFiles);
        Assert.IsEmpty(client.DeletedDirectories);
        CollectionAssert.AreEqual(new[] { "/" }, client.ListedPaths);
        Assert.IsEmpty(client.RecursiveDeletes);
        Assert.IsTrue(client.Entries.Any(entry => entry.FullPath == "/link/keep.txt"));
    }

    [TestMethod]
    public async Task Recursive_delete_does_not_traverse_symbolic_links()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("Mod", "/Mod");
        client.Entries.Add(new RemoteDirectoryEntry("link", "/Mod/link", true, 0, default, IsSymbolicLink: true));
        client.Entries.Add(new RemoteDirectoryEntry("file.txt", "/Mod/file.txt", false, 0, default));
        client.AddFile("keep.txt", "/elsewhere/keep.txt");
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true });

        CollectionAssert.AreEqual(new[] { "/Mod/link", "/Mod/file.txt" }, client.DeletedFiles);
        CollectionAssert.AreEqual(new[] { "/Mod" }, client.DeletedDirectories);
        Assert.DoesNotContain("/Mod/link", client.ListedPaths);
        Assert.IsTrue(client.Entries.Any(entry => entry.FullPath == "/elsewhere/keep.txt"));
    }

    [TestMethod]
    public async Task Recursive_delete_constructs_child_paths_from_validated_names()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("Mod", "/Mod");
        client.AddFile("keep.txt", "/elsewhere/keep.txt");
        client.ListingsByPath["/Mod"] =
            [new RemoteDirectoryEntry("file.txt", "/elsewhere/keep.txt", false, 0, default)];
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true });

        CollectionAssert.AreEqual(new[] { "/Mod/file.txt" }, client.DeletedFiles);
        Assert.IsTrue(client.Entries.Any(entry => entry.FullPath == "/elsewhere/keep.txt"));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("nested/file")]
    [DataRow("/escape")]
    public async Task Recursive_delete_rejects_unsafe_child_names_before_deleting_siblings(string name)
    {
        var client = new FakeSftpClient();
        client.AddDirectory("Mod", "/Mod");
        client.ListingsByPath["/Mod"] =
        [
            new RemoteDirectoryEntry("valid.txt", "/Mod/valid.txt", false, 0, default),
            new RemoteDirectoryEntry(name, "/escape", false, 0, default)
        ];
        var service = new SftpFileService(() => client);

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(
            new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true }));

        Assert.IsEmpty(client.Deletions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deleting_a_file_or_link_that_became_a_directory_requires_a_new_confirmation(bool wasLink)
    {
        var client = new FakeSftpClient();
        client.AddDirectory("changed", "/changed");
        client.AddFile("keep.txt", "/changed/keep.txt");
        var service = new SftpFileService(() => client);

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(new RemoteFileItem
        {
            Name = "changed", FullPath = "/changed", IsDirectory = wasLink, IsSymbolicLink = wasLink
        }));

        Assert.IsEmpty(client.Deletions);
        CollectionAssert.AreEqual(new[] { "/" }, client.ListedPaths);
    }

    [TestMethod]
    [DataRow("/", "root")]
    [DataRow("/Mod/..", "..")]
    [DataRow("/Mod/.", ".")]
    [DataRow("/Mod/../keep", "keep")]
    [DataRow("relative", "relative")]
    [DataRow("/Mod", "other")]
    public async Task Unsafe_delete_targets_are_rejected_before_remote_io(string path, string name)
    {
        var client = new FakeSftpClient();
        var service = new SftpFileService(() => client);

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(
            new RemoteFileItem { Name = name, FullPath = path, IsDirectory = true }));

        Assert.IsEmpty(client.ListedPaths);
        Assert.IsEmpty(client.Deletions);
    }

    [TestMethod]
    public async Task Delete_failure_stops_before_removing_parent_or_remaining_siblings()
    {
        var client = new FakeSftpClient();
        client.AddDirectory("Mod", "/Mod");
        client.AddFile("first", "/Mod/first");
        client.AddFile("blocked", "/Mod/blocked");
        client.AddFile("last", "/Mod/last");
        client.DeleteExceptions["/Mod/blocked"] = new IOException("Permission denied");
        var service = new SftpFileService(() => client);

        var error = await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(
            new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true }));

        Assert.AreEqual("Permission denied", error.Message);
        CollectionAssert.AreEqual(new[] { "/Mod/first" }, client.DeletedFiles);
        Assert.IsEmpty(client.DeletedDirectories);
        Assert.IsTrue(client.Entries.Any(entry => entry.FullPath == "/Mod/last"));
    }

    [TestMethod]
    public async Task Server_side_directory_deletion_uses_one_call_without_listing_or_deleting_children()
    {
        var client = new FakeSftpClient { RecursiveDeleteHandler = _ => Task.FromResult(true) };
        client.AddDirectory("Mod", "/Mod");
        for (var i = 0; i < 1000; i++) client.AddFile($"file{i}", $"/Mod/file{i}");
        var service = new SftpFileService(() => client);

        await service.DeleteAsync(new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true });

        CollectionAssert.AreEqual(new[] { "/Mod" }, client.RecursiveDeletes);
        CollectionAssert.AreEqual(new[] { "/" }, client.ListedPaths);
        Assert.IsEmpty(client.Deletions);
    }

    [TestMethod]
    public async Task Failed_server_side_delete_is_not_retried_using_sftp_after_a_potential_partial_deletion()
    {
        var client = new FakeSftpClient
        {
            RecursiveDeleteHandler = _ => Task.FromException<bool>(new IOException("rm: Permission denied"))
        };
        client.AddDirectory("Mod", "/Mod");
        client.AddFile("file", "/Mod/file");
        var service = new SftpFileService(() => client);

        var error = await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(
            new RemoteFileItem { Name = "Mod", FullPath = "/Mod", IsDirectory = true }));

        Assert.AreEqual("rm: Permission denied", error.Message);
        CollectionAssert.AreEqual(new[] { "/Mod" }, client.RecursiveDeletes);
        CollectionAssert.AreEqual(new[] { "/" }, client.ListedPaths);
        Assert.IsEmpty(client.Deletions);
    }

    private static RemoteFileItem Find(IReadOnlyList<RemoteFileItem> items, string name) =>
        items.Single(item => item.Name == name);
}
