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
        Assert.HasCount(1, controller.Snapshot.DirectoryListing.Items);
        Assert.AreSame(initialItem, controller.Snapshot.DirectoryListing.Items[0]);

        fileService.ListException = null;
        await controller.RefreshAsync();

        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State);
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
        Assert.AreEqual(SftpSessionState.Cancelled, controller.Snapshot.State);
        Assert.IsTrue(controller.Snapshot.CanNavigate);
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
            _ => Task.FromResult(true));

        Assert.AreEqual(0, localFileChecks);
        Assert.AreEqual(0, localOutputsCreated);
        Assert.AreEqual(0, fileService.DownloadCallCount);
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
        public Exception? ListException { get; set; }
        public Func<CancellationToken, Task>? UploadHandler { get; set; }
        public int RenameCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path)
        {
            if (ListException is not null) return Task.FromException<IReadOnlyList<RemoteFileItem>>(ListException);
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

        public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
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
