using System.Diagnostics;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpFolderDownloadTests
{
    private string _root = null!;
    private static readonly Func<string, Task<bool>> Overwrite = _ => Task.FromResult(true);
    private static RemoteFileItem Folder(string path = "/资料") => new()
    {
        Name = path.Split('/').Last(), FullPath = path, IsDirectory = true
    };
    private static RemoteFileItem FileItem(string path, long size = 3) => new()
    {
        Name = path.Split('/').Last(), FullPath = path, SizeBytes = size
    };
    private static DownloadDestination Destination() => new(File.Exists, File.Create,
        path => Directory.CreateDirectory(path), File.Delete);

    [TestInitialize]
    public void CreateFixture() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "FluentShell-download-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void RemoveFixture() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task Mixed_download_preserves_roots_nested_bytes_empty_folders_and_transfer_channel()
    {
        var browse = new RemoteTree();
        var transfer = new RemoteTree();
        transfer.Listings["/资料"] = [FileItem("/资料/根.txt"), Folder("/资料/深层"), Folder("/资料/空目录")];
        transfer.Listings["/资料/深层"] = [FileItem("/资料/深层/根.txt", 2)];
        transfer.Bytes["/资料/根.txt"] = [0, 128, 255];
        transfer.Bytes["/资料/深层/根.txt"] = [10, 20];
        transfer.Bytes["/独立.txt"] = [40, 50, 60];
        using var controller = new SftpSessionController(browse, transfer);
        var progress = new List<SftpTransferProgress>();
        controller.SnapshotChanged += (_, s) => { if (s.Transfer.Progress is { } p) progress.Add(p); };

        await controller.DownloadEntriesAsync([Folder(), FileItem("/独立.txt")], _root, Destination(), Overwrite);

        CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, File.ReadAllBytes(Path.Combine(_root, "资料", "根.txt")));
        CollectionAssert.AreEqual(new byte[] { 10, 20 }, File.ReadAllBytes(Path.Combine(_root, "资料", "深层", "根.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "资料", "空目录")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "独立.txt")));
        Assert.HasCount(0, browse.Listed);
        Assert.HasCount(0, browse.Downloaded);
        Assert.AreEqual(6, controller.Snapshot.Queue.CompletedCount);
        Assert.AreEqual(8, progress[^1].BytesTransferred);
        Assert.IsTrue(progress.All(p => p.TotalBytes == 8));
    }

    [TestMethod]
    public async Task Existing_directories_merge_and_conflicts_use_full_relative_paths()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/跳过.txt"), FileItem("/资料/覆盖.txt")];
        var local = Directory.CreateDirectory(Path.Combine(_root, "资料")).FullName;
        File.WriteAllText(Path.Combine(local, "跳过.txt"), "keep");
        File.WriteAllText(Path.Combine(local, "覆盖.txt"), "old");
        File.WriteAllText(Path.Combine(local, "本地.txt"), "unrelated");
        var prompted = new List<string>();
        using var controller = new SftpSessionController(service);

        await controller.DownloadAsync(Folder(), _root, Destination(), name =>
        {
            prompted.Add(name);
            return Task.FromResult(name.EndsWith("覆盖.txt"));
        });

        CollectionAssert.AreEqual(new[] { "资料/跳过.txt", "资料/覆盖.txt" }, prompted);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(local, "跳过.txt")));
        Assert.AreEqual("unrelated", File.ReadAllText(Path.Combine(local, "本地.txt")));
        Assert.HasCount(3, File.ReadAllBytes(Path.Combine(local, "覆盖.txt")));
        Assert.AreEqual(1, controller.Snapshot.Queue.SkippedCount);
    }

    [TestMethod]
    public async Task Denied_directory_listing_is_a_failed_row_and_other_branches_continue()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [Folder("/资料/无权限"), Folder("/资料/正常")];
        service.Listings["/资料/正常"] = [FileItem("/资料/正常/文件.txt")];
        service.Denied.Add("/资料/无权限");
        using var controller = new SftpSessionController(service);

        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);

        Assert.IsTrue(File.Exists(Path.Combine(_root, "资料", "正常", "文件.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "资料", "无权限")));
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
        Assert.AreEqual(SftpTransferState.Failed, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task Local_file_blocking_a_directory_fails_its_descendants_and_keeps_other_roots()
    {
        File.WriteAllText(Path.Combine(_root, "资料"), "keep");
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/文件.txt")];
        using var controller = new SftpSessionController(service);

        await controller.DownloadEntriesAsync([Folder(), FileItem("/其他.txt")], _root, Destination(), Overwrite);

        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(_root, "资料")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "其他.txt")));
        Assert.AreEqual(2, controller.Snapshot.Queue.FailedCount);
        CollectionAssert.AreEqual(new[] { "/其他.txt" }, service.Downloaded);
    }

    [TestMethod]
    [DataRow("../逃逸.txt")]
    [DataRow("CON.txt")]
    [DataRow("数据:stream")]
    [DataRow("C:\\逃逸.txt")]
    public async Task Unsafe_remote_names_fail_without_stopping_valid_siblings(string name)
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [new() { Name = name, FullPath = "/outside" }, FileItem("/资料/正常.txt")];
        using var controller = new SftpSessionController(service);

        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);

        CollectionAssert.AreEqual(new[] { "/资料/正常.txt" }, service.Downloaded);
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
    }

    [TestMethod]
    public async Task Child_source_is_constructed_from_parent_instead_of_untrusted_full_path()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [new() { Name = "正常.txt", FullPath = "/other/private.txt" }];
        using var controller = new SftpSessionController(service);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        CollectionAssert.AreEqual(new[] { "/资料/正常.txt" }, service.Downloaded);
    }

    [TestMethod]
    public async Task Case_collisions_are_reported_without_overwriting_either_remote_entry()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/A.txt"), FileItem("/资料/a.txt")];
        using var controller = new SftpSessionController(service);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        Assert.HasCount(0, service.Downloaded);
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
        Assert.Contains("同一个名称", controller.Snapshot.Queue.Items.Single(i => i.State == TransferItemState.Failed).ErrorMessage!);
    }

    [TestMethod]
    public async Task Remote_symbolic_links_are_failed_without_listing_or_downloading_their_targets()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] =
        [
            new() { Name = "循环", FullPath = "/资料/循环", IsDirectory = true, IsSymbolicLink = true },
            new() { Name = "文件链接", FullPath = "/资料/文件链接", IsSymbolicLink = true },
            FileItem("/资料/正常.txt")
        ];
        using var controller = new SftpSessionController(service);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        CollectionAssert.AreEqual(new[] { "/资料" }, service.Listed);
        CollectionAssert.AreEqual(new[] { "/资料/正常.txt" }, service.Downloaded);
        Assert.AreEqual(2, controller.Snapshot.Queue.FailedCount);
    }

    [TestMethod]
    public async Task Existing_local_junction_cannot_redirect_downloads()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "真实目录")).FullName;
        var link = Path.Combine(_root, "资料");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            var service = new RemoteTree();
            service.Listings["/资料"] = [FileItem("/资料/文件.txt")];
            using var controller = new SftpSessionController(service);
            await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
            Assert.HasCount(0, service.Downloaded);
            Assert.HasCount(0, Directory.GetFiles(target));
            Assert.AreEqual(2, controller.Snapshot.Queue.FailedCount);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task Failed_open_does_not_delete_an_existing_file()
    {
        var path = Path.Combine(_root, "文件.txt");
        File.WriteAllText(path, "keep");
        var service = new RemoteTree();
        using var controller = new SftpSessionController(service);
        var destination = new DownloadDestination(File.Exists, _ => throw new IOException("无法打开"),
            _ => { }, File.Delete);
        await controller.DownloadAsync(FileItem("/文件.txt"), _root, destination, Overwrite);
        Assert.AreEqual("keep", File.ReadAllText(path));
        Assert.HasCount(0, service.Downloaded);
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
    }

    [TestMethod]
    public async Task Cancel_during_scan_does_not_create_local_directories_or_files()
    {
        var service = new RemoteTree();
        using var control = new TransferControl();
        service.OnList = _ => control.Cancel();
        using var controller = new SftpSessionController(service);
        controller.BeginBatch(control);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        Assert.HasCount(0, Directory.GetFileSystemEntries(_root));
        Assert.AreEqual(SftpTransferState.Cancelled, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancel_during_file_stops_remaining_entries_and_obeys_partial_file_policy(bool preserve)
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/第一.txt"), FileItem("/资料/第二.txt")];
        using var control = new TransferControl();
        service.OnDownload = async (_, stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 99 }, token);
            control.Cancel(preserve);
            token.ThrowIfCancellationRequested();
        };
        using var controller = new SftpSessionController(service);
        controller.BeginBatch(control);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        Assert.HasCount(1, service.Downloaded);
        Assert.AreEqual(preserve, File.Exists(Path.Combine(_root, "资料", "第一.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "资料", "第二.txt")));
        Assert.AreEqual(SftpTransferState.Cancelled, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task Pause_after_listing_prevents_further_scan_and_local_io_until_resumed()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/文件.txt")];
        using var control = new TransferControl();
        service.OnList = _ => control.Pause();
        using var controller = new SftpSessionController(service);
        controller.BeginBatch(control);
        var download = controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        Assert.IsFalse(download.IsCompleted);
        Assert.HasCount(0, Directory.GetFileSystemEntries(_root));
        Assert.HasCount(0, service.Downloaded);
        control.Resume();
        await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "资料", "文件.txt")));
    }

    [TestMethod]
    public async Task File_transfer_failure_cleans_partial_file_and_continues_other_files()
    {
        var service = new RemoteTree();
        service.Listings["/资料"] = [FileItem("/资料/失败.txt"), FileItem("/资料/成功.txt")];
        service.OnDownload = async (path, output, token) =>
        {
            await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
            if (path.EndsWith("失败.txt")) throw new IOException("Failure");
        };
        using var controller = new SftpSessionController(service);
        await controller.DownloadAsync(Folder(), _root, Destination(), Overwrite);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "资料", "失败.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "资料", "成功.txt")));
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
        Assert.Contains("远程主机拒绝", controller.Snapshot.Transfer.Message);
    }

    private sealed class RemoteTree : ISftpFileService
    {
        public Task<long> GetDirectorySizeAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public bool IsConnected => true;
        public Dictionary<string, IReadOnlyList<RemoteFileItem>> Listings { get; } = [];
        public Dictionary<string, byte[]> Bytes { get; } = [];
        public HashSet<string> Denied { get; } = [];
        public List<string> Listed { get; } = [];
        public List<string> Downloaded { get; } = [];
        public Action<string>? OnList { get; set; }
        public Func<string, Stream, CancellationToken, Task>? OnDownload { get; set; }
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path)
        {
            Listed.Add(path);
            OnList?.Invoke(path);
            if (Denied.Contains(path)) throw new IOException("没有读取权限。");
            return Task.FromResult(Listings.GetValueOrDefault(path) ?? (IReadOnlyList<RemoteFileItem>)[]);
        }
        public async Task DownloadAsync(string path, Stream output, CancellationToken token)
        {
            Downloaded.Add(path);
            if (OnDownload is not null) await OnDownload(path, output, token);
            else await output.WriteAsync(Bytes.GetValueOrDefault(path) ?? new byte[] { 1, 2, 3 }, token);
        }
        public Task<bool> ExistsAsync(string path) => Task.FromResult(false);
        public Task<bool> IsDirectoryAsync(string path) => Task.FromResult(false);
        public Task CreateDirectoryAsync(string path) => throw new NotSupportedException();
        public Task UploadAsync(Stream input, string path, CancellationToken token) => throw new NotSupportedException();
        public Task RenameAsync(string source, string destination) => throw new NotSupportedException();
        public Task DeleteAsync(RemoteFileItem item) => throw new NotSupportedException();
    }
}
