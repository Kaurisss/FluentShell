using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpSessionControllerTests
{
    [TestMethod]
    public async Task Refresh_transitions_through_listing_to_idle()
    {
        var fileService = new FakeSftpFileService
        {
            DirectoryItems = [new RemoteFileItem { Name = "日志", IsDirectory = true, FullPath = "/日志" }]
        };
        using var controller = new SftpSessionController(fileService);
        var states = new List<SftpSessionState>();
        controller.StateChanged += (_, snapshot) => states.Add(snapshot.State);

        var listing = await controller.RefreshAsync(showHiddenFiles: false);

        Assert.IsTrue(listing.Succeeded);
        CollectionAssert.AreEqual(
            new[] { SftpSessionState.ListingDirectory, SftpSessionState.Idle },
            states);
        Assert.AreEqual("/", listing.Path);
        Assert.HasCount(1, listing.Items);
    }

    [TestMethod]
    public async Task Failed_directory_read_enters_failed_state_and_recovers()
    {
        var fileService = new FakeSftpFileService { ListException = new InvalidOperationException("连接中断") };
        using var controller = new SftpSessionController(fileService);

        var failed = await controller.RefreshAsync(showHiddenFiles: false);

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual(SftpSessionState.Failed, controller.Snapshot.State);

        fileService.ListException = null;
        var recovered = await controller.RefreshAsync(showHiddenFiles: false);

        Assert.IsTrue(recovered.Succeeded);
        Assert.AreEqual(SftpSessionState.Idle, controller.Snapshot.State);
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
            _ => Task.FromResult(true),
            showHiddenFiles: false);
        await uploadStarted.Task;
        controller.CancelTransfer();

        var result = await upload;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("上传已取消。", result.Message);
        Assert.AreEqual(SftpSessionState.Cancelled, controller.Snapshot.State);
        Assert.IsTrue(controller.Snapshot.CanNavigate);
    }

    [TestMethod]
    public async Task Rename_rejects_invalid_name_before_remote_io()
    {
        var fileService = new FakeSftpFileService();
        using var controller = new SftpSessionController(fileService);
        var item = new RemoteFileItem { Name = "safe.txt", FullPath = "/safe.txt" };

        var result = await controller.RenameAsync(item, "../escape", showHiddenFiles: false);

        Assert.IsFalse(result.Succeeded);
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

        var result = await controller.DownloadAsync(
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

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, localFileChecks);
        Assert.AreEqual(0, localOutputsCreated);
        Assert.AreEqual(0, fileService.DownloadCallCount);
    }

    private sealed class FakeSftpFileService : ISftpFileService
    {
        public bool IsConnected { get; set; } = true;
        public IReadOnlyList<RemoteFileItem> DirectoryItems { get; set; } = [];
        public Exception? ListException { get; set; }
        public Func<CancellationToken, Task>? UploadHandler { get; set; }
        public int RenameCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, bool showHiddenFiles)
        {
            if (ListException is not null) return Task.FromException<IReadOnlyList<RemoteFileItem>>(ListException);
            return Task.FromResult(DirectoryItems);
        }

        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string path) => Task.FromResult(false);

        public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken) =>
            UploadHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;

        public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            return Task.CompletedTask;
        }

        public Task RenameAsync(string sourcePath, string destinationPath)
        {
            RenameCallCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(RemoteFileItem item) => Task.CompletedTask;
    }
}