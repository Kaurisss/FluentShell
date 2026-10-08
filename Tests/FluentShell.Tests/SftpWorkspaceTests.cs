using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SftpWorkspaceTests
{
    [TestMethod]
    public async Task Workspace_connects_directory_properties_to_browsing_service_and_detaches_on_dispose()
    {
        var service = new FakeSftpFileService();
        var transfer = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView();
        using var workspace = new SftpWorkspace(service, view, transferFileService: transfer);
        Assert.IsNotNull(view.DirectorySizeProvider);

        var result = await view.DirectorySizeProvider(
            new RemoteFileItem { Name = "data", FullPath = "/data", IsDirectory = true }, CancellationToken.None);

        Assert.AreEqual(1024L, result);
        Assert.AreEqual(1, service.DirectorySizeCalls);
        Assert.AreEqual(0, transfer.DirectorySizeCalls);
        workspace.Dispose();
        Assert.IsNull(view.DirectorySizeProvider);
    }

    [TestMethod]
    public async Task Connection_loss_cancels_directory_properties_calculation()
    {
        var service = new FakeSftpFileService
        {
            DirectorySizeHandler = async token => { await Task.Delay(Timeout.Infinite, token); return 0; }
        };
        var view = new RecordingSftpWorkspaceView();
        using var workspace = new SftpWorkspace(service, view);
        var calculation = view.DirectorySizeProvider!(
            new RemoteFileItem { Name = "data", FullPath = "/data", IsDirectory = true }, CancellationToken.None);
        workspace.ConnectionLost();
        await Assert.ThrowsAsync<OperationCanceledException>(() => calculation.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task Pane_upload_uses_selected_files_without_opening_picker()
    {
        var service = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView();
        using var workspace = new SftpWorkspace(service, view);
        await workspace.UploadAsync([CreateUploadFile("one.txt"), CreateUploadFile("two.txt")]);
        Assert.AreEqual(2, service.UploadCallCount);
        Assert.AreEqual(0, view.UploadPickerCalls);
    }

    [TestMethod]
    public async Task Folder_picker_upload_registers_one_task_and_creates_an_empty_folder()
    {
        var path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FluentShell-folder-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var service = new FakeSftpFileService();
            var view = new RecordingSftpWorkspaceView { UploadFolder = new("空文件夹", path) };
            var center = new TransferCenter();
            using var workspace = new SftpWorkspace(service, view, transfers: center);
            await workspace.UploadFolderAsync();
            Assert.AreEqual(1, service.CreateDirectoryCallCount);
            Assert.AreEqual(0, view.UploadPickerCalls);
            Assert.HasCount(1, center.Groups[0]);
            Assert.AreEqual(TransferTaskState.Completed, center.Groups[0][0].State);
            Assert.AreEqual(1, center.Groups[0][0].Queue.CompletedCount);
        }
        finally { Directory.Delete(path); }
    }

    [TestMethod]
    public async Task Cancelling_folder_picker_does_not_register_a_task()
    {
        var service = new FakeSftpFileService();
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(service, new RecordingSftpWorkspaceView(), transfers: center);
        await workspace.UploadFolderAsync();
        Assert.HasCount(0, center.Groups);
        Assert.AreEqual(0, service.CreateDirectoryCallCount);
    }

    [TestMethod]
    public async Task Retrying_folder_upload_rebuilds_the_plan_and_keeps_one_task()
    {
        var path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FluentShell-retry-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(path, "原文件.txt"), "original");
            var service = new FakeSftpFileService
            {
                UploadHandler = _ => Task.FromException(new IOException("临时失败"))
            };
            var center = new TransferCenter();
            using var workspace = new SftpWorkspace(service, new RecordingSftpWorkspaceView(), transfers: center);
            await workspace.UploadAsync([new SftpUploadDirectory("资料", path)]);
            var task = center.Groups[0][0];
            Assert.IsTrue(task.CanRetry);
            service.UploadHandler = null;
            File.WriteAllText(Path.Combine(path, "新文件.txt"), "new");

            await task.RetryAsync();

            Assert.HasCount(1, center.Groups[0]);
            Assert.AreEqual(TransferTaskState.Completed, task.State);
            Assert.AreEqual(3, task.Queue.CompletedCount);
            Assert.AreEqual(3, service.UploadCallCount);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [TestMethod]
    public async Task Pane_download_uses_local_path_without_opening_picker()
    {
        var service = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = null };
        string? writtenPath = null;
        using var workspace = new SftpWorkspace(service, view,
            localFileExists: _ => false,
            createLocalOutput: path => { writtenPath = path; return new MemoryStream(); });
        await workspace.DownloadAsync(new RemoteFileItem { Name = "one.txt", FullPath = "/one.txt" }, Path.GetTempPath());
        Assert.AreEqual(1, service.DownloadCallCount);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "one.txt"), writtenPath);
        Assert.AreEqual(0, view.DownloadPickerCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Ordinary_download_uses_configured_default_directory_without_opening_picker(bool folder)
    {
        var service = new FakeSftpFileService();
        var item = new RemoteFileItem { Name = "资料", FullPath = "/资料", IsDirectory = folder };
        if (folder) service.ListingsByPath["/资料"] = [new() { Name = "文件.txt", FullPath = "/资料/文件.txt" }];
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var root = Path.Combine(Path.GetTempPath(), "FluentShell-default-" + Guid.NewGuid().ToString("N"));
        var outputs = new List<string>();
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(service, view, localFileExists: _ => false,
            createLocalOutput: path => { outputs.Add(path); return new MemoryStream(); },
            createLocalDirectory: _ => { }, transfers: center);
        workspace.SetPreferences(new UserPreferences { UseDefaultDownloadDirectory = true }, root);

        await workspace.DownloadAsync(item);

        Assert.AreEqual(0, view.DownloadPickerCalls);
        Assert.AreEqual(root, center.Groups[0][0].Target);
        CollectionAssert.AreEqual(new[] { folder ? Path.Combine(root, "资料", "文件.txt") : Path.Combine(root, "资料") }, outputs);
    }

    [TestMethod]
    public async Task Mixed_download_uses_updated_default_directory_and_retains_it_for_retry()
    {
        var service = new FakeSftpFileService { DownloadHandler = _ => Task.FromException(new IOException("临时失败")) };
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = null };
        var root = Path.Combine(Path.GetTempPath(), "FluentShell-default-" + Guid.NewGuid().ToString("N"));
        var center = new TransferCenter();
        var outputs = new List<string>();
        using var workspace = new SftpWorkspace(service, view, localFileExists: _ => false,
            createLocalOutput: path => { outputs.Add(path); return new MemoryStream(); },
            deleteLocalFile: _ => { }, transfers: center);
        var preferences = new UserPreferences { UseDefaultDownloadDirectory = true };
        workspace.SetPreferences(preferences, Path.Combine(root, "旧设置"));
        workspace.SetPreferences(preferences, root);
        await workspace.DownloadAsync([new RemoteFileItem { Name = "一.txt", FullPath = "/一.txt" },
            new RemoteFileItem { Name = "二.txt", FullPath = "/二.txt" }]);
        var task = center.Groups[0][0];
        workspace.SetPreferences(preferences, Path.Combine(root, "其他目标"));
        service.DownloadHandler = null;
        await task.RetryAsync();

        Assert.AreEqual(0, view.DownloadPickerCalls);
        Assert.AreEqual(root, task.Target);
        Assert.IsTrue(outputs.All(path => Path.GetDirectoryName(path) == root));
        Assert.AreEqual(TransferTaskState.Completed, task.State);
    }

    [TestMethod]
    public async Task Download_asks_for_a_directory_when_direct_default_download_is_disabled()
    {
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = Path.GetTempPath() };
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(new FakeSftpFileService(), view, localFileExists: _ => false,
            createLocalOutput: _ => new MemoryStream(), transfers: center);
        workspace.SetPreferences(new UserPreferences { UseDefaultDownloadDirectory = false },
            Path.Combine(Path.GetTempPath(), "配置的目录"));
        await workspace.DownloadAsync(new RemoteFileItem { Name = "文件.txt", FullPath = "/文件.txt" });
        Assert.AreEqual(1, view.DownloadPickerCalls);
        Assert.AreEqual(Path.GetTempPath(), center.Groups[0][0].Target);
    }

    [TestMethod]
    public async Task Explicit_local_destination_takes_precedence_over_default_download_settings()
    {
        var view = new RecordingSftpWorkspaceView();
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(new FakeSftpFileService(), view, localFileExists: _ => false,
            createLocalOutput: _ => new MemoryStream(), transfers: center);
        workspace.SetPreferences(new UserPreferences { UseDefaultDownloadDirectory = true },
            Path.Combine(Path.GetTempPath(), "配置的目录"));
        await workspace.DownloadAsync(new RemoteFileItem { Name = "文件.txt", FullPath = "/文件.txt" }, Path.GetTempPath());
        Assert.AreEqual(0, view.DownloadPickerCalls);
        Assert.AreEqual(Path.GetTempPath(), center.Groups[0][0].Target);
    }

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
    public async Task Download_registers_one_global_task()
    {
        var item = new RemoteFileItem { Name = "日志.txt", FullPath = "/日志.txt" };
        var fileService = new FakeSftpFileService();
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = Path.GetTempPath() };
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(
            fileService,
            view,
            localFileExists: _ => false,
            createLocalOutput: _ => new MemoryStream(), transfers: center);

        await workspace.DownloadAsync(item);

        Assert.HasCount(1, center.Groups);
        Assert.HasCount(1, center.Groups[0]);
        Assert.AreEqual(TransferTaskState.Completed, center.Groups[0][0].State);
    }

    [TestMethod]
    public async Task Mixed_folder_download_uses_one_global_task_and_the_chosen_local_destination()
    {
        var service = new FakeSftpFileService();
        var folder = new RemoteFileItem { Name = "资料", FullPath = "/资料", IsDirectory = true };
        service.ListingsByPath["/资料"] = [new() { Name = "文件.txt", FullPath = "/资料/文件.txt" }];
        var center = new TransferCenter();
        var view = new RecordingSftpWorkspaceView();
        var outputs = new List<string>();
        var directories = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "FluentShell-pane-" + Guid.NewGuid().ToString("N"));
        using var workspace = new SftpWorkspace(service, view, localFileExists: _ => false,
            createLocalOutput: path => { outputs.Add(path); return new MemoryStream(); },
            createLocalDirectory: directories.Add, transfers: center);
        await workspace.DownloadAsync([folder, new RemoteFileItem { Name = "独立.txt", FullPath = "/独立.txt" }], root);
        Assert.HasCount(1, center.Groups[0]);
        Assert.AreEqual(3, center.Groups[0][0].Queue.CompletedCount);
        Assert.AreEqual(0, view.DownloadPickerCalls);
        CollectionAssert.AreEqual(new[] { Path.Combine(root, "资料", "文件.txt"), Path.Combine(root, "独立.txt") }, outputs);
        CollectionAssert.AreEqual(new[] { Path.Combine(root, "资料") }, directories);
    }

    [TestMethod]
    public async Task Folder_download_retry_rescans_remote_tree_and_retains_captured_selection_and_destination()
    {
        var service = new FakeSftpFileService();
        service.ListingsByPath["/资料"] = [new() { Name = "旧.txt", FullPath = "/资料/旧.txt" }];
        service.DownloadHandler = _ => Task.FromException(new IOException("临时失败"));
        var center = new TransferCenter();
        var view = new RecordingSftpWorkspaceView { DownloadDirectory = Path.GetTempPath() };
        var destinations = new List<string>();
        using var workspace = new SftpWorkspace(service, view, localFileExists: _ => false,
            createLocalOutput: path => { destinations.Add(path); return new MemoryStream(); },
            createLocalDirectory: _ => { }, deleteLocalFile: _ => { }, transfers: center);
        var selected = new List<RemoteFileItem> { new() { Name = "资料", FullPath = "/资料", IsDirectory = true } };
        await workspace.DownloadAsync(selected);
        var task = center.Groups[0][0];
        Assert.IsTrue(task.CanRetry);
        selected.Clear();
        view.DownloadDirectory = Path.Combine(Path.GetTempPath(), "其他目标");
        await workspace.NavigateToAsync("/其他目录");
        service.ListingsByPath["/资料"] =
        [new() { Name = "旧.txt", FullPath = "/资料/旧.txt" }, new() { Name = "新.txt", FullPath = "/资料/新.txt" }];
        service.DownloadHandler = null;

        await task.RetryAsync();

        Assert.HasCount(1, center.Groups[0]);
        Assert.AreEqual(TransferTaskState.Completed, task.State);
        Assert.AreEqual(3, task.Queue.CompletedCount);
        Assert.AreEqual(1, view.DownloadPickerCalls);
        Assert.IsTrue(destinations.All(path => Path.GetDirectoryName(path) == Path.Combine(Path.GetTempPath(), "资料")));
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
        var center = new TransferCenter();
        using var workspace = new SftpWorkspace(fileService, view, transfers: center);
        fileService.UploadHandler = token =>
        {
            center.Discard(center.Groups.Single().Single());
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await workspace.UploadAsync();

        Assert.AreEqual(1, fileService.UploadCallCount, "用户取消后不应继续上传剩余文件。");
        Assert.AreEqual(SftpTransferState.Cancelled, view.LastSnapshot!.Transfer.State);
        Assert.HasCount(0, center.Groups, "丢弃操作应移除全局任务。");
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
    public async Task Each_view_request_drives_its_flow()
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
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.OnRender = snapshot =>
        {
            if (snapshot.Transfer.State == SftpTransferState.Completed && snapshot.Transfer.Message.StartsWith("已上传"))
                uploaded.TrySetResult();
            if (snapshot.Transfer.State == SftpTransferState.Completed && snapshot.Transfer.Message.StartsWith("已下载"))
                downloaded.TrySetResult();
        };

        view.RaiseRefreshRequested();
        view.RaiseNavigateRequested("/日志");
        view.RaiseNewFolderRequested();
        view.RaiseRenameRequested(item);
        view.RaiseDeleteRequested(item);
        view.RaiseUploadRequested();
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.RaiseDownloadRequested(item);
        await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("/日志", view.LastSnapshot.DirectoryListing.Path);
        Assert.AreEqual(1, fileService.CreateDirectoryCallCount);
        Assert.AreEqual(1, fileService.UploadCallCount);
        Assert.AreEqual(1, fileService.DownloadCallCount);
        Assert.AreEqual(1, fileService.RenameCallCount);
        Assert.AreEqual(1, fileService.DeleteCallCount);
    }

    private static SftpUploadFile CreateUploadFile(string name) =>
        new(name, () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    private sealed class RecordingSftpWorkspaceView : ISftpWorkspaceView, ISftpPropertiesView
    {
        public Func<RemoteFileItem, CancellationToken, Task<long>>? DirectorySizeProvider { get; private set; }
        public void SetDirectorySizeProvider(Func<RemoteFileItem, CancellationToken, Task<long>>? provider) => DirectorySizeProvider = provider;
        public string PromptAnswer { get; set; } = string.Empty;
        public bool OverwriteAnswer { get; set; } = true;
        public bool DeleteAnswer { get; set; } = true;
        public IReadOnlyList<SftpUploadFile> UploadFiles { get; set; } = [];
        public SftpUploadDirectory? UploadFolder { get; set; }
        public string? DownloadDirectory { get; set; } = "C:\\下载";
        public int DeleteConfirmations { get; private set; }
        public int UploadPickerCalls { get; private set; }
        public int DownloadPickerCalls { get; private set; }
        public SftpSessionSnapshot LastSnapshot { get; private set; } = null!;
        public Action<SftpSessionSnapshot>? OnRender { get; set; }

        public event EventHandler? RefreshRequested;
        public event EventHandler<string>? NavigateRequested;
        public event EventHandler? NewFolderRequested;
        public event EventHandler? UploadRequested;
        public event EventHandler? UploadFolderRequested;
        public event EventHandler<RemoteFileItem>? DownloadRequested;
        public event EventHandler<RemoteFileItem>? RenameRequested;
        public event EventHandler<RemoteFileItem>? DeleteRequested;

        public void Render(SftpSessionSnapshot snapshot) { LastSnapshot = snapshot; OnRender?.Invoke(snapshot); }

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

        public Task<IReadOnlyList<SftpUploadFile>> PickUploadFilesAsync() { UploadPickerCalls++; return Task.FromResult(UploadFiles); }
        public Task<SftpUploadDirectory?> PickUploadFolderAsync() => Task.FromResult(UploadFolder);

        public Task<string?> PickDownloadDirectoryAsync() { DownloadPickerCalls++; return Task.FromResult(DownloadDirectory); }

        public void RaiseRefreshRequested() => RefreshRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseNavigateRequested(string path) => NavigateRequested?.Invoke(this, path);
        public void RaiseNewFolderRequested() => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseUploadRequested() => UploadRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseUploadFolderRequested() => UploadFolderRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseDownloadRequested(RemoteFileItem item) => DownloadRequested?.Invoke(this, item);
        public void RaiseRenameRequested(RemoteFileItem item) => RenameRequested?.Invoke(this, item);
        public void RaiseDeleteRequested(RemoteFileItem item) => DeleteRequested?.Invoke(this, item);
    }

    private sealed class FakeSftpFileService : ISftpFileService
    {
        public int DirectorySizeCalls { get; private set; }
        public Func<CancellationToken, Task<long>>? DirectorySizeHandler { get; set; }
        public Task<long> GetDirectorySizeAsync(string path, CancellationToken token)
        {
            DirectorySizeCalls++;
            return DirectorySizeHandler?.Invoke(token) ?? Task.FromResult(1024L);
        }
        public bool IsConnected { get; set; } = true;
        public bool FileExists { get; set; }
        public List<RemoteFileItem> DirectoryItems { get; } = [];
        public Func<CancellationToken, Task>? UploadHandler { get; set; }
        public Func<CancellationToken, Task>? DownloadHandler { get; set; }
        public Dictionary<string, IReadOnlyList<RemoteFileItem>> ListingsByPath { get; } = [];
        public int UploadCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }
        public int RenameCallCount { get; private set; }
        public int CreateDirectoryCallCount { get; private set; }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path) =>
            Task.FromResult(ListingsByPath.GetValueOrDefault(path) ?? (IReadOnlyList<RemoteFileItem>)DirectoryItems.ToList());

        public Task CreateDirectoryAsync(string path)
        {
            CreateDirectoryCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string path) => Task.FromResult(FileExists);
        public Task<bool> IsDirectoryAsync(string path) => Task.FromResult(
            DirectoryItems.Any(item => item.FullPath == path && item.IsDirectory));

        public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken)
        {
            UploadCallCount++;
            return UploadHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            return DownloadHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
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
