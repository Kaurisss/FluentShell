using System.Diagnostics;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpFolderUploadTests
{
    private string _root = null!;

    [TestInitialize]
    public void CreateFixture() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "FluentShell-upload-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void RemoveFixture() => Directory.Delete(_root, recursive: true);

    private SftpUploadDirectory Folder(string name = "资料") => new(name, _root);
    private string WriteFile(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        return path;
    }

    [TestMethod]
    public async Task Mixed_upload_preserves_root_nested_files_empty_folders_and_bytes_on_transfer_channel()
    {
        WriteFile("根.txt", [0, 128, 255]);
        WriteFile(Path.Combine("子目录", "根.txt"), [10, 20]);
        Directory.CreateDirectory(Path.Combine(_root, "空目录"));
        var browse = new MemoryFileService();
        var transfer = new MemoryFileService();
        using var controller = new SftpSessionController(browse, transfer);
        var progress = new List<SftpTransferProgress>();
        controller.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Transfer.Progress is { } value) progress.Add(value);
        };

        await controller.UploadEntriesAsync([Folder(), new SftpUploadFile("独立.bin",
            () => Task.FromResult<Stream>(new MemoryStream([42])))], _ => Task.FromResult(true), "/目标");

        CollectionAssert.AreEquivalent(new[] { "/目标/资料", "/目标/资料/子目录", "/目标/资料/空目录" },
            transfer.CreatedDirectories);
        CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, transfer.Files["/目标/资料/根.txt"]);
        CollectionAssert.AreEqual(new byte[] { 10, 20 }, transfer.Files["/目标/资料/子目录/根.txt"]);
        CollectionAssert.AreEqual(new byte[] { 42 }, transfer.Files["/目标/独立.bin"]);
        Assert.HasCount(0, browse.CreatedDirectories);
        Assert.HasCount(0, browse.Files);
        Assert.AreEqual(SftpTransferState.Completed, controller.Snapshot.Transfer.State);
        Assert.AreEqual(6, controller.Snapshot.Queue.CompletedCount);
        Assert.AreEqual(6L, progress[^1].TotalBytes);
        Assert.AreEqual(6L, progress[^1].BytesTransferred);
    }

    [TestMethod]
    public async Task Empty_folder_is_created_and_has_a_completed_queue_entry()
    {
        var service = new MemoryFileService();
        using var controller = new SftpSessionController(service);

        await controller.UploadEntriesAsync([Folder()], _ => Task.FromResult(true));

        CollectionAssert.AreEqual(new[] { "/资料" }, service.CreatedDirectories);
        Assert.HasCount(0, service.Files);
        Assert.AreEqual(1, controller.Snapshot.Queue.CompletedCount);
    }

    [TestMethod]
    public async Task Existing_directories_merge_and_file_conflicts_use_full_relative_names()
    {
        WriteFile(Path.Combine("子目录", "同名.txt"));
        WriteFile("新文件.txt");
        var service = new MemoryFileService();
        service.Directories.UnionWith(["/资料", "/资料/子目录"]);
        service.DeniedListings.UnionWith(["/资料", "/资料/子目录"]);
        service.Files["/资料/子目录/同名.txt"] = [9];
        var prompts = new List<string>();
        using var controller = new SftpSessionController(service);

        await controller.UploadEntriesAsync([Folder()], name =>
        {
            prompts.Add(name);
            return Task.FromResult(false);
        });

        CollectionAssert.AreEqual(new[] { "资料/子目录/同名.txt" }, prompts);
        Assert.HasCount(0, service.CreatedDirectories);
        CollectionAssert.AreEqual(new byte[] { 9 }, service.Files["/资料/子目录/同名.txt"]);
        Assert.IsTrue(service.Files.ContainsKey("/资料/新文件.txt"));
        Assert.AreEqual(1, controller.Snapshot.Queue.SkippedCount);
    }

    [TestMethod]
    public async Task Remote_file_blocking_a_directory_fails_its_descendants_and_continues_other_roots()
    {
        WriteFile(Path.Combine("子目录", "文件.bin"));
        var service = new MemoryFileService();
        service.Files["/资料"] = [9];
        using var controller = new SftpSessionController(service);

        await controller.UploadEntriesAsync([Folder(), new SftpUploadFile("其他.bin",
            () => Task.FromResult<Stream>(new MemoryStream([1])))], _ => Task.FromResult(true));

        CollectionAssert.AreEqual(new byte[] { 9 }, service.Files["/资料"]);
        Assert.IsTrue(service.Files.ContainsKey("/其他.bin"));
        Assert.HasCount(0, service.CreatedDirectories);
        Assert.AreEqual(3, controller.Snapshot.Queue.FailedCount);
        Assert.AreEqual(1, controller.Snapshot.Queue.CompletedCount);
        Assert.AreEqual(SftpTransferState.Failed, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task One_file_failure_does_not_fail_the_remaining_queue_entries()
    {
        WriteFile("坏.bin");
        WriteFile("好.bin");
        var service = new MemoryFileService { FailUploadPath = "/资料/坏.bin" };
        using var controller = new SftpSessionController(service);

        await controller.UploadEntriesAsync([Folder()], _ => Task.FromResult(true));

        Assert.IsTrue(service.Files.ContainsKey("/资料/好.bin"));
        Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
        Assert.AreEqual(2, controller.Snapshot.Queue.CompletedCount);
        Assert.AreEqual("没有写入权限", controller.Snapshot.Queue.Items.Single(
            row => row.RelativePath == "资料/坏.bin").ErrorMessage);
    }

    [TestMethod]
    public async Task Cancelling_a_conflict_stops_before_any_file_is_written()
    {
        WriteFile("已有.bin");
        WriteFile("新.bin");
        var service = new MemoryFileService();
        service.Files["/资料/已有.bin"] = [9];
        service.Files["/资料/新.bin"] = [9];
        using var controller = new SftpSessionController(service);

        await controller.UploadEntriesAsync([Folder()], _ =>
        {
            controller.CancelTransfer();
            return Task.FromResult(false);
        });

        Assert.HasCount(0, service.UploadedPaths);
        Assert.AreEqual(SftpTransferState.Cancelled, controller.Snapshot.Transfer.State);
        Assert.AreEqual(0, controller.Snapshot.Queue.Items.Count(row => row.State == TransferItemState.Pending));
    }

    [TestMethod]
    public async Task Cancellation_during_statistics_leaves_remote_directory_untouched()
    {
        var service = new MemoryFileService();
        using var controller = new SftpSessionController(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upload = controller.UploadEntriesAsync([Folder(), new SftpUploadFile("统计.bin", async () =>
        {
            entered.SetResult();
            await release.Task;
            return new MemoryStream([1]);
        })], _ => Task.FromResult(true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.CancelTransfer();
        release.SetResult();
        await upload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(0, service.CreatedDirectories);
        Assert.HasCount(0, service.UploadedPaths);
        Assert.AreEqual(SftpTransferState.Cancelled, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task Paused_statistics_waits_until_resumed()
    {
        using var control = new TransferControl();
        control.Pause();
        var service = new MemoryFileService();
        using var controller = new SftpSessionController(service);
        controller.BeginBatch(control);
        var upload = controller.UploadEntriesAsync([Folder()], _ => Task.FromResult(true));
        Assert.IsFalse(upload.IsCompleted);
        Assert.HasCount(0, service.CreatedDirectories);
        control.Resume();
        await upload.WaitAsync(TimeSpan.FromSeconds(5));
        controller.EndBatch();
        Assert.AreEqual(1, controller.Snapshot.Queue.CompletedCount);
    }

    [TestMethod]
    public async Task Browsing_during_upload_does_not_redirect_later_files()
    {
        WriteFile("甲.bin");
        WriteFile("乙.bin");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browse = new MemoryFileService();
        var transfer = new MemoryFileService();
        transfer.OnUpload = async _ => { entered.TrySetResult(); await release.Task; };
        using var controller = new SftpSessionController(browse, transfer);
        var upload = controller.UploadEntriesAsync([Folder()], _ => Task.FromResult(true), "/原目标");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.NavigateToAsync("/其他");
        Assert.IsTrue(controller.Snapshot.Transfer.IsActive);
        release.SetResult();
        await upload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(transfer.Files.Keys.All(path => path.StartsWith("/原目标/资料/", StringComparison.Ordinal)));
        Assert.AreEqual("/其他", controller.Snapshot.DirectoryListing.Path);
    }

    [TestMethod]
    [DataRow("../逃逸")]
    [DataRow("/绝对路径")]
    [DataRow("资料/../逃逸")]
    [DataRow("..")]
    public async Task Invalid_root_names_never_touch_remote_or_open_file_streams(string name)
    {
        var service = new MemoryFileService();
        using var controller = new SftpSessionController(service);
        await controller.UploadEntriesAsync([new SftpUploadFile(name,
            () => throw new AssertFailedException("不得打开无效条目。")), Folder(name)], _ => Task.FromResult(true));
        Assert.HasCount(0, service.CreatedDirectories);
        Assert.HasCount(0, service.UploadedPaths);
        Assert.AreEqual(SftpTransferState.Failed, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task Directory_junction_is_reported_without_traversing_outside_or_looping()
    {
        var link = Path.Combine(_root, "循环联接");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(_root);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            WriteFile("正常.txt");
            var service = new MemoryFileService();
            using var controller = new SftpSessionController(service);
            await controller.UploadEntriesAsync([Folder()], _ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.HasCount(1, service.Files);
            Assert.AreEqual(1, controller.Snapshot.Queue.FailedCount);
            Assert.Contains("联接", controller.Snapshot.Queue.Items.Single(row => row.State == TransferItemState.Failed).ErrorMessage!);
        }
        finally { Directory.Delete(link); }
    }

    private sealed class MemoryFileService : ISftpFileService
    {
        public bool IsConnected => true;
        public HashSet<string> Directories { get; } = ["/"];
        public HashSet<string> DeniedListings { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> CreatedDirectories { get; } = [];
        public List<string> UploadedPaths { get; } = [];
        public string? FailUploadPath { get; init; }
        public Func<CancellationToken, Task>? OnUpload { get; set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path)
        {
            if (DeniedListings.Contains(path)) throw new IOException("没有列目录权限。");
            if (Files.ContainsKey(path)) throw new IOException("同名条目是文件。");
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        }
        public Task<bool> ExistsAsync(string path) => Task.FromResult(Directories.Contains(path) || Files.ContainsKey(path));
        public Task<bool> IsDirectoryAsync(string path) => Task.FromResult(Directories.Contains(path));
        public Task CreateDirectoryAsync(string path)
        {
            Assert.IsFalse(Files.ContainsKey(path));
            Directories.Add(path);
            CreatedDirectories.Add(path);
            return Task.CompletedTask;
        }
        public async Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken)
        {
            if (OnUpload is not null) await OnUpload(cancellationToken);
            if (remotePath == FailUploadPath) throw new IOException("没有写入权限");
            using var output = new MemoryStream();
            await input.CopyToAsync(output, cancellationToken);
            Files[remotePath] = output.ToArray();
            UploadedPaths.Add(remotePath);
        }
        public Task DownloadAsync(string path, Stream output, CancellationToken token) => throw new NotSupportedException();
        public Task RenameAsync(string source, string target) => throw new NotSupportedException();
        public Task DeleteAsync(RemoteFileItem item) => throw new NotSupportedException();
    }
}
