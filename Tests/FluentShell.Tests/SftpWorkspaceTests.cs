using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpWorkspaceTests
{
    [TestMethod]
    public async Task Declining_the_overwrite_prompt_skips_the_upload()
    {
        var fileService = new FakeSftpFileService { FileExists = true };
        var view = new RecordingSftpWorkspaceView
        {
            UploadFiles = [CreateUploadFile("报表.xlsx")],
            OverwriteAnswer = false
        };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.UploadAsync();

        Assert.AreEqual(0, fileService.UploadCallCount, "用户拒绝覆盖后不应发起传输。");
        Assert.AreEqual("已跳过现有文件。", view.LastSnapshot.StatusMessage);
    }

    [TestMethod]
    public async Task Declining_the_delete_prompt_leaves_the_file_in_place()
    {
        var item = new RemoteFileItem { Name = "重要.txt", FullPath = "/重要.txt" };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(item);
        var view = new RecordingSftpWorkspaceView { DeleteAnswer = false };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.DeleteAsync(item);

        Assert.AreEqual(1, view.DeleteConfirmations);
        Assert.AreEqual(0, fileService.DeleteCallCount, "用户拒绝删除后不应发起删除。");
    }

    [TestMethod]
    public async Task Empty_folder_name_does_not_reach_the_remote_host()
    {
        var fileService = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView { PromptAnswer = "   " };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.CreateFolderAsync();

        Assert.AreEqual(0, fileService.CreateDirectoryCallCount, "空名称不应发起远程请求。");
    }

    [TestMethod]
    public async Task Empty_new_name_does_not_reach_the_remote_host()
    {
        var item = new RemoteFileItem { Name = "旧名.txt", FullPath = "/旧名.txt" };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(item);
        var view = new RecordingSftpWorkspaceView { PromptAnswer = string.Empty };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.RenameAsync(item);

        Assert.AreEqual(0, fileService.RenameCallCount, "取消重命名对话框后不应发起远程请求。");
    }

    [TestMethod]
    public async Task Rename_prompt_is_prefilled_with_the_current_name()
    {
        var item = new RemoteFileItem { Name = "旧名.txt", FullPath = "/旧名.txt" };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(item);
        var view = new RecordingSftpWorkspaceView { PromptAnswer = string.Empty };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.RenameAsync(item);

        Assert.AreEqual("旧名.txt", view.LastPromptInitialText, "重命名对话框应预填当前名称。");
    }

    [TestMethod]
    public async Task Download_without_a_chosen_directory_does_not_transfer()
    {
        var item = new RemoteFileItem { Name = "日志.txt", FullPath = "/日志.txt" };
        var fileService = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = null };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.DownloadAsync(item);

        Assert.AreEqual(0, fileService.DownloadCallCount, "未选择目录时不应发起传输。");
    }

    [TestMethod]
    public async Task Cancelling_one_upload_stops_the_remaining_files()
    {
        var fileService = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView
        {
            UploadFiles =
            [
                CreateUploadFile("第一个.bin"),
                CreateUploadFile("第二个.bin"),
                CreateUploadFile("第三个.bin")
            ]
        };
        using var workspace = new SftpWorkspace(fileService, view);
        fileService.UploadHandler = _ =>
        {
            workspace.CancelTransfer();
            return Task.FromException(new OperationCanceledException());
        };

        await workspace.UploadAsync();

        Assert.AreEqual(1, fileService.UploadCallCount, "用户取消后不应继续上传剩余文件。");
        Assert.AreEqual(SftpSessionState.Cancelled, view.LastSnapshot.State);
    }

    [TestMethod]
    public async Task Uploading_several_files_continues_when_nothing_is_cancelled()
    {
        var fileService = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView
        {
            UploadFiles = [CreateUploadFile("甲.bin"), CreateUploadFile("乙.bin")]
        };
        using var workspace = new SftpWorkspace(fileService, view);

        await workspace.UploadAsync();

        Assert.AreEqual(2, fileService.UploadCallCount);
    }

    [TestMethod]
    public void Each_view_request_drives_its_flow()
    {
        var item = new RemoteFileItem { Name = "文件.txt", FullPath = "/文件.txt" };
        var fileService = new FakeSftpFileService();
        fileService.DirectoryItems.Add(item);
        var view = new RecordingSftpWorkspaceView
        {
            PromptAnswer = "新名称",
            UploadFiles = [CreateUploadFile("上传.bin")]
        };
        using var workspace = new SftpWorkspace(
            fileService,
            view,
            localFileExists: _ => false,
            createLocalOutput: _ => new MemoryStream());

        view.RaiseRefreshRequested();
        view.RaiseNavigateRequested("/日志");
        view.RaiseNewFolderRequested();
        view.RaiseUploadRequested();
        view.RaiseDownloadRequested(item);
        view.RaiseRenameRequested(item);
        view.RaiseDeleteRequested(item);
        view.RaiseCancelTransferRequested();

        Assert.AreEqual("/日志", view.LastSnapshot.CurrentPath);
        Assert.AreEqual(1, fileService.CreateDirectoryCallCount);
        Assert.AreEqual(1, fileService.UploadCallCount);
        Assert.AreEqual(1, fileService.DownloadCallCount);
        Assert.AreEqual(1, fileService.RenameCallCount);
        Assert.AreEqual(1, fileService.DeleteCallCount);
    }

    private static SftpUploadFile CreateUploadFile(string name) =>
        new(name, () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    private sealed class RecordingSftpWorkspaceView : ISftpWorkspaceView
    {
        public string PromptAnswer { get; set; } = string.Empty;
        public bool OverwriteAnswer { get; set; } = true;
        public bool DeleteAnswer { get; set; } = true;
        public IReadOnlyList<SftpUploadFile> UploadFiles { get; set; } = [];
        public string? DownloadDirectory { get; set; } = "C:\\下载";
        public int DeleteConfirmations { get; private set; }
        public SftpSessionSnapshot LastSnapshot { get; private set; } = null!;

        public event EventHandler? RefreshRequested;
        public event EventHandler<string>? NavigateRequested;
        public event EventHandler? NewFolderRequested;
        public event EventHandler? UploadRequested;
        public event EventHandler<RemoteFileItem>? DownloadRequested;
        public event EventHandler<RemoteFileItem>? RenameRequested;
        public event EventHandler<RemoteFileItem>? DeleteRequested;
        public event EventHandler? CancelTransferRequested;

        public void Render(SftpSessionSnapshot snapshot) => LastSnapshot = snapshot;

        public string? LastPromptInitialText { get; private set; }

        public Task<string> PromptTextAsync(string title, string placeholder, string initialText = "")
        {
            LastPromptInitialText = initialText;
            return Task.FromResult(PromptAnswer);
        }

        public Task<bool> ConfirmOverwriteAsync(string name) => Task.FromResult(OverwriteAnswer);

        public Task<bool> ConfirmDeleteAsync(RemoteFileItem item)
        {
            DeleteConfirmations++;
            return Task.FromResult(DeleteAnswer);
        }

        public Task<IReadOnlyList<SftpUploadFile>> PickUploadFilesAsync() =>
            Task.FromResult(UploadFiles);

        public Task<string?> PickDownloadDirectoryAsync() => Task.FromResult(DownloadDirectory);

        public void RaiseRefreshRequested() => RefreshRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseNavigateRequested(string path) => NavigateRequested?.Invoke(this, path);
        public void RaiseNewFolderRequested() => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseUploadRequested() => UploadRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseDownloadRequested(RemoteFileItem item) => DownloadRequested?.Invoke(this, item);
        public void RaiseRenameRequested(RemoteFileItem item) => RenameRequested?.Invoke(this, item);
        public void RaiseDeleteRequested(RemoteFileItem item) => DeleteRequested?.Invoke(this, item);
        public void RaiseCancelTransferRequested() => CancelTransferRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSftpFileService : ISftpFileService
    {
        public bool IsConnected { get; set; } = true;
        public bool FileExists { get; set; }
        public List<RemoteFileItem> DirectoryItems { get; } = [];
        public Func<CancellationToken, Task>? UploadHandler { get; set; }
        public int UploadCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }
        public int RenameCallCount { get; private set; }
        public int CreateDirectoryCallCount { get; private set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path) =>
            Task.FromResult<IReadOnlyList<RemoteFileItem>>(DirectoryItems.ToList());

        public Task CreateDirectoryAsync(string path)
        {
            CreateDirectoryCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path) => Task.FromResult(FileExists);

        public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken)
        {
            UploadCallCount++;
            return UploadHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

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

        public Task DeleteAsync(RemoteFileItem item)
        {
            DeleteCallCount++;
            return Task.CompletedTask;
        }
    }
}
