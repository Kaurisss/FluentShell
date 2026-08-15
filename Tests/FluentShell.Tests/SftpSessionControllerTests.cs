using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpSessionControllerTests
{
    [TestMethod]
    public async Task Refresh_publishes_directory_listing_with_listing_and_idle_states()
    {
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(new RemoteFileItem
        {
            Name = "日志",
            IsDirectory = true,
            FullPath = "/日志"
        });
        using var controller = new SftpSessionController(fileService);
        var snapshots = new List<SftpSessionSnapshot>();
        controller.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

        await controller.RefreshAsync();

        CollectionAssert.AreEqual(
            new[] { SftpSessionState.ListingDirectory, SftpSessionState.Idle },
            snapshots.Select(snapshot => snapshot.State).ToList());
        Assert.AreEqual("/", controller.Snapshot.DirectoryListing.Path);
        Assert.HasCount(1, controller.Snapshot.DirectoryListing.Items);
        Assert.AreEqual("日志", controller.Snapshot.DirectoryListing.Items[0].Name);
    }

    [TestMethod]
    public async Task Failed_directory_read_retains_previous_directory_listing_and_recovers()
    {
        var initialItem = new RemoteFileItem
        {
            Name = "可见",
            IsDirectory = true,
            FullPath = "/可见"
        };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(initialItem);
        using var controller = new SftpSessionController(fileService);

        await controller.RefreshAsync();
        fileService.ListException = new InvalidOperationException("连接中断");
        await controller.RefreshAsync();

        Assert.AreEqual(SftpSessionState.Failed, controller.Snapshot.State);
        Assert.AreEqual(
            SftpFailureKind.DirectoryRead,
            controller.Snapshot.FailureKind,
            "目录读取失败应标记为 DirectoryRead，视图据此只做内联提示、不弹窗。");
        Assert.HasCount(1, controller.Snapshot.DirectoryListing.Items);
        Assert.AreSame(initialItem, controller.Snapshot.DirectoryListing.Items[0]);

        fileService.ListException = null;
        await controller.RefreshAsync();

        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State);
    }

    [TestMethod]
    public async Task Failed_transfer_surfaces_on_the_transfer_axis_without_touching_browse_state()
    {
        var fileService = new FakeSftpFileService
        {
            UploadHandler = _ => Task.FromException(new InvalidOperationException("磁盘已满"))
        };
        using var controller = new SftpSessionController(fileService);

        await controller.UploadAsync(
            "上传.bin",
            () => Task.FromResult<Stream>(new MemoryStream()),
            _ => Task.FromResult(true));

        Assert.AreEqual(SftpTransferState.Failed, controller.Snapshot.Transfer.State, "传输失败落在传输轴上，视图据此弹窗。");
        Assert.Contains("磁盘已满", controller.Snapshot.Transfer.Message);
        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State, "浏览轴不受传输失败影响。");
    }

    [TestMethod]
    public async Task Create_directory_refreshes_directory_listing()
    {
        var fileService = new FakeSftpFileService();
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.CreateDirectoryAsync("备份");

        var published = snapshots[^1];
        Assert.AreEqual(SftpSessionState.Idle, published.State);
        Assert.IsTrue(published.DirectoryListing.Items.Any(item => item.Name == "备份"));
    }

    [TestMethod]
    public async Task Rename_refreshes_directory_listing()
    {
        var original = new RemoteFileItem
        {
            Name = "旧名称.txt",
            FullPath = "/旧名称.txt"
        };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(original);
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.RenameAsync(original, "新名称.txt");

        var published = snapshots[^1];
        Assert.IsFalse(published.DirectoryListing.Items.Any(item => item.Name == "旧名称.txt"));
        Assert.IsTrue(published.DirectoryListing.Items.Any(item => item.Name == "新名称.txt"));
    }

    [TestMethod]
    public async Task Delete_refreshes_directory_listing()
    {
        var item = new RemoteFileItem
        {
            Name = "删除我.txt",
            FullPath = "/删除我.txt"
        };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(item);
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.DeleteAsync(item);

        Assert.IsFalse(snapshots[^1].DirectoryListing.Items.Any(current => current.Name == "删除我.txt"));
    }

    [TestMethod]
    public async Task Upload_refreshes_directory_listing()
    {
        var fileService = new FakeSftpFileService();
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.UploadAsync(
            "上传.txt",
            () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            _ => Task.FromResult(true));

        Assert.IsTrue(snapshots[^1].DirectoryListing.Items.Any(item => item.Name == "上传.txt"));
    }

    [TestMethod]
    public async Task Cancelled_transfer_returns_cancelled_and_releases_actions()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileService = new FakeSftpFileService
        {
            UploadHandler = async cancellationToken =>
            {
                uploadStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        using var controller = new SftpSessionController(fileService);

        var upload = controller.UploadAsync(
            "日志.txt",
            () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            _ => Task.FromResult(true));
        await uploadStarted.Task;
        controller.CancelTransfer();

        await upload;

        Assert.AreEqual("上传已取消。", controller.Snapshot.StatusMessage);
        Assert.AreEqual(SftpTransferState.Cancelled, controller.Snapshot.Transfer.State);
        Assert.IsTrue(controller.Snapshot.CanNavigate);
        Assert.IsTrue(controller.Snapshot.CanTransfer, "取消后应允许开始下一次传输。");
    }

    [TestMethod]
    public async Task Browsing_stays_available_while_a_transfer_is_running()
    {
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transferService = new FakeSftpFileService
        {
            UploadHandler = async _ =>
            {
                uploadStarted.SetResult();
                await uploadRelease.Task;
            }
        };
        var browseService = new FakeSftpFileService();
        browseService.DirectoryItems.Add(new RemoteFileItem { Name = "文档", IsDirectory = true, FullPath = "/文档" });
        using var controller = new SftpSessionController(browseService, transferService);

        var upload = controller.UploadAsync(
            "大文件.bin",
            () => Task.FromResult<Stream>(new MemoryStream([1])),
            _ => Task.FromResult(true));
        await uploadStarted.Task;

        Assert.IsTrue(controller.Snapshot.CanNavigate, "传输进行中必须仍可浏览目录。");
        Assert.IsTrue(controller.Snapshot.CanModifyRemoteFiles, "传输进行中仍可重命名/删除/新建。");
        Assert.IsFalse(controller.Snapshot.CanTransfer, "同时只允许一个传输。");

        await controller.RefreshAsync();

        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State, "浏览在传输期间照常完成。");
        Assert.HasCount(1, controller.Snapshot.DirectoryListing.Items);
        Assert.IsTrue(controller.Snapshot.Transfer.IsActive, "浏览不打断传输。");

        uploadRelease.SetResult();
        await upload;
        Assert.AreEqual(SftpTransferState.Completed, controller.Snapshot.Transfer.State);
    }

    [TestMethod]
    public async Task Directory_download_runs_entirely_on_the_transfer_channel()
    {
        var browseService = new FakeSftpFileService();
        var transferService = new FakeSftpFileService();
        transferService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "甲.txt", FullPath = "/备份/甲.txt" }
        ];
        using var controller = new SftpSessionController(browseService, transferService);

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            Path.GetTempPath(),
            new DownloadDestination(_ => false, _ => new MemoryStream(), _ => { }, _ => { }),
            _ => Task.FromResult(true));

        Assert.AreEqual(1, transferService.DownloadCallCount, "统计与传输都走传输通道。");
        Assert.AreEqual(0, browseService.DownloadCallCount, "浏览通道不承担传输。");
    }

    [TestMethod]
    public async Task Rename_rejects_invalid_name_before_remote_io()
    {
        var fileService = new FakeSftpFileService();
        using var controller = new SftpSessionController(fileService);
        var item = new RemoteFileItem { Name = "safe.txt", FullPath = "/safe.txt" };

        await controller.RenameAsync(item, "../escape");

        Assert.AreEqual(0, fileService.RenameCallCount);
    }

    [TestMethod]
    public async Task Download_rejects_unsafe_name_before_local_or_remote_io()
    {
        var fileService = new FakeSftpFileService();
        using var controller = new SftpSessionController(fileService);
        var localFileChecks = 0;
        var localOutputsCreated = 0;
        var item = new RemoteFileItem { Name = "..", FullPath = "/sensitive" };

        await controller.DownloadAsync(
            item,
            Path.GetTempPath(),
            new DownloadDestination(
                _ =>
                {
                    localFileChecks++;
                    return false;
                },
                _ =>
                {
                    localOutputsCreated++;
                    return new MemoryStream();
                },
                _ => { },
                _ => { }),
            _ => Task.FromResult(true));

        Assert.AreEqual(0, localFileChecks);
        Assert.AreEqual(0, localOutputsCreated);
        Assert.AreEqual(0, fileService.DownloadCallCount);
    }

    [TestMethod]
    public async Task Directory_download_recreates_structure_and_downloads_nested_files()
    {
        var fileService = new FakeSftpFileService();
        fileService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "..", IsDirectory = true, FullPath = "/" },
            new RemoteFileItem { Name = "甲.txt", FullPath = "/备份/甲.txt" },
            new RemoteFileItem { Name = "深层", IsDirectory = true, FullPath = "/备份/深层" }
        ];
        fileService.ListingsByPath["/备份/深层"] =
        [
            new RemoteFileItem { Name = "乙.txt", FullPath = "/备份/深层/乙.txt" }
        ];
        using var controller = new SftpSessionController(fileService);
        var createdDirectories = new List<string>();
        var downloadedTo = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "sftp下载");

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            root,
            new DownloadDestination(
                _ => false,
                path =>
                {
                    downloadedTo.Add(path);
                    return new MemoryStream();
                },
                createdDirectories.Add,
                _ => { }),
            _ => Task.FromResult(true));

        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State);
        CollectionAssert.AreEqual(
            new[] { Path.Combine(root, "备份"), Path.Combine(root, "备份", "深层") },
            createdDirectories,
            "应先建父目录再建子目录。");
        CollectionAssert.AreEqual(
            new[] { Path.Combine(root, "备份", "甲.txt"), Path.Combine(root, "备份", "深层", "乙.txt") },
            downloadedTo,
            "两个文件都应落到对应的本地目录，合成的 .. 条目不参与。");
        Assert.Contains("2 个文件", controller.Snapshot.StatusMessage);
    }

    [TestMethod]
    public async Task Directory_download_skips_declined_overwrites_but_continues()
    {
        var fileService = new FakeSftpFileService();
        fileService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "已有.txt", FullPath = "/备份/已有.txt" },
            new RemoteFileItem { Name = "新增.txt", FullPath = "/备份/新增.txt" }
        ];
        using var controller = new SftpSessionController(fileService);
        var downloadedTo = new List<string>();

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            Path.GetTempPath(),
            new DownloadDestination(
                path => path.EndsWith("已有.txt", StringComparison.Ordinal),
                path =>
                {
                    downloadedTo.Add(path);
                    return new MemoryStream();
                },
                _ => { },
                _ => { }),
            _ => Task.FromResult(false));

        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State);
        Assert.HasCount(1, downloadedTo, "被拒绝覆盖的文件跳过，其余继续。");
        Assert.Contains("跳过 1 个", controller.Snapshot.StatusMessage);
    }

    [TestMethod]
    public async Task File_download_publishes_determinate_progress_and_clears_it_on_completion()
    {
        var fileService = new FakeSftpFileService();
        fileService.DownloadSizesByPath["/日志.txt"] = 200;
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "日志.txt", FullPath = "/日志.txt", SizeBytes = 200 },
            Path.GetTempPath(),
            new DownloadDestination(_ => false, _ => new MemoryStream(), _ => { }, _ => { }),
            _ => Task.FromResult(true));

        var progressed = snapshots.Where(s => s.Transfer.Progress is not null).ToList();
        Assert.IsGreaterThan(0, progressed.Count, "传输过程中应发布确定进度。");
        Assert.AreEqual(200, progressed[^1].Transfer.Progress!.BytesTransferred);
        Assert.AreEqual(200, progressed[^1].Transfer.Progress!.TotalBytes);
        Assert.IsNull(snapshots[^1].Transfer.Progress, "传输结束后的快照不应再携带进度。");
    }

    [TestMethod]
    public async Task Directory_download_reports_progress_against_the_whole_tree()
    {
        var fileService = new FakeSftpFileService();
        fileService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "甲.txt", FullPath = "/备份/甲.txt", SizeBytes = 100 },
            new RemoteFileItem { Name = "深层", IsDirectory = true, FullPath = "/备份/深层" }
        ];
        fileService.ListingsByPath["/备份/深层"] =
        [
            new RemoteFileItem { Name = "乙.txt", FullPath = "/备份/深层/乙.txt", SizeBytes = 300 }
        ];
        fileService.DownloadSizesByPath["/备份/甲.txt"] = 100;
        fileService.DownloadSizesByPath["/备份/深层/乙.txt"] = 300;
        using var controller = new SftpSessionController(fileService);
        var snapshots = CaptureSnapshots(controller);

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            Path.GetTempPath(),
            new DownloadDestination(_ => false, _ => new MemoryStream(), _ => { }, _ => { }),
            _ => Task.FromResult(true));

        var progressed = snapshots.Where(s => s.Transfer.Progress is not null).ToList();
        Assert.IsGreaterThan(0, progressed.Count);
        Assert.IsTrue(
            progressed.All(s => s.Transfer.Progress!.TotalBytes == 400),
            "进度总量应是整棵目录树的文件字节和。");
        Assert.AreEqual(400, progressed[^1].Transfer.Progress!.BytesTransferred);
    }

    [TestMethod]
    public async Task Directory_download_survives_single_entry_failures_and_reports_them()
    {
        var fileService = new FakeSftpFileService();
        fileService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "好.txt", FullPath = "/备份/好.txt" },
            new RemoteFileItem { Name = "坏链接", FullPath = "/备份/坏链接" },
            new RemoteFileItem { Name = "另一个.txt", FullPath = "/备份/另一个.txt" }
        ];
        // SFTP 对符号链接、特殊文件这类拒绝只报一句 "Failure"。
        fileService.DownloadExceptionsByPath["/备份/坏链接"] = new InvalidOperationException("Failure");
        using var controller = new SftpSessionController(fileService);
        var downloadedTo = new List<string>();
        var deleted = new List<string>();

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            Path.GetTempPath(),
            new DownloadDestination(
                _ => false,
                path =>
                {
                    downloadedTo.Add(path);
                    return new MemoryStream();
                },
                _ => { },
                deleted.Add),
            _ => Task.FromResult(true));

        Assert.HasCount(3, downloadedTo, "一个条目失败不拖垮整批，后续条目照常尝试。");
        Assert.AreEqual(SftpTransferState.Failed, controller.Snapshot.Transfer.State, "部分失败要弹窗解释，不能装成功。");
        Assert.Contains("已下载 2 个文件", controller.Snapshot.StatusMessage);
        Assert.Contains("1 个失败", controller.Snapshot.StatusMessage);
        Assert.Contains("坏链接", controller.Snapshot.StatusMessage);
        Assert.Contains("远程主机拒绝", controller.Snapshot.StatusMessage, "原始的 Failure 要翻译成能行动的提示。");
        Assert.HasCount(1, deleted, "失败留下的半截文件要清掉。");
        Assert.EndsWith("坏链接", deleted[0]);
    }

    [TestMethod]
    public async Task Directory_download_rejects_unsafe_entry_names_from_the_remote_host()
    {
        var fileService = new FakeSftpFileService();
        fileService.ListingsByPath["/备份"] =
        [
            new RemoteFileItem { Name = "../逃逸.txt", FullPath = "/逃逸.txt" }
        ];
        using var controller = new SftpSessionController(fileService);
        var localOutputsCreated = 0;

        await controller.DownloadAsync(
            new RemoteFileItem { Name = "备份", IsDirectory = true, FullPath = "/备份" },
            Path.GetTempPath(),
            new DownloadDestination(
                _ => false,
                _ =>
                {
                    localOutputsCreated++;
                    return new MemoryStream();
                },
                _ => { },
                _ => { }),
            _ => Task.FromResult(true));

        Assert.AreEqual(0, localOutputsCreated, "远程条目名不可信，越界名不得触达本地文件系统。");
        Assert.AreEqual(0, fileService.DownloadCallCount);
        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State, "校验失败按未完成操作收尾，不是异常。");
    }

    private static List<SftpSessionSnapshot> CaptureSnapshots(SftpSessionController controller)
    {
        var snapshots = new List<SftpSessionSnapshot>();
        controller.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);
        return snapshots;
    }

    private sealed class FakeSftpFileService : ISftpFileService
    {
        public bool IsConnected { get; set; } = true;
        public List<RemoteFileItem> DirectoryItems { get; } = [];
        public Dictionary<string, List<RemoteFileItem>> ListingsByPath { get; } = [];
        public Exception? ListException { get; set; }
        public Func<CancellationToken, Task>? UploadHandler { get; set; }
        public int RenameCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path)
        {
            if (ListException is not null) return Task.FromException<IReadOnlyList<RemoteFileItem>>(ListException);
            if (ListingsByPath.TryGetValue(path, out var listing))
                return Task.FromResult<IReadOnlyList<RemoteFileItem>>(listing.ToList());
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(DirectoryItems.ToList());
        }

        public Task CreateDirectoryAsync(string path)
        {
            DirectoryItems.Add(CreateItem(path, isDirectory: true));
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path) => Task.FromResult(false);

        public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken)
        {
            if (UploadHandler is not null) return UploadHandler(cancellationToken);

            DirectoryItems.Add(CreateItem(remotePath, isDirectory: false));
            return Task.CompletedTask;
        }

        public Dictionary<string, int> DownloadSizesByPath { get; } = [];
        public Dictionary<string, Exception> DownloadExceptionsByPath { get; } = [];

        public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            if (DownloadExceptionsByPath.TryGetValue(remotePath, out var exception))
                return Task.FromException(exception);
            if (DownloadSizesByPath.TryGetValue(remotePath, out var size))
                output.Write(new byte[size]);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string sourcePath, string destinationPath)
        {
            RenameCallCount++;
            var item = DirectoryItems.Single(current => current.FullPath == sourcePath);
            DirectoryItems.Remove(item);
            DirectoryItems.Add(CreateItem(destinationPath, item.IsDirectory));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(RemoteFileItem item)
        {
            DirectoryItems.RemoveAll(current => current.FullPath == item.FullPath);
            return Task.CompletedTask;
        }

        private static RemoteFileItem CreateItem(string fullPath, bool isDirectory) => new()
        {
            Name = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last(),
            IsDirectory = isDirectory,
            FullPath = fullPath
        };
    }
}
