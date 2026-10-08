using FluentShell.Models;

namespace FluentShell.Core;

/// <summary>一个待上传的本地条目，名称是远程目标目录下的单个名称。</summary>
public abstract record SftpUploadEntry(string Name);

public sealed record SftpUploadFile(string Name, Func<Task<Stream>> OpenRead) : SftpUploadEntry(Name);

public sealed record SftpUploadDirectory(string Name, string LocalPath) : SftpUploadEntry(Name);

/// <summary>
/// SFTP 工作区的呈现与提示出口：快照往这里渲染，用户的确认与选择从这里取回。
/// </summary>
/// <remarks>
/// 生产适配器是 <c>Views/Session/SftpWorkspaceView</c>；测试适配器记录渲染并回放预设答复。
/// 提示的结果一律以值的形式交给 <see cref="SftpSessionController"/>，因此控制器仍然
/// 不认识 <c>ContentDialog</c>、<c>FolderPicker</c> 与 <c>SfDataGrid</c>。
/// </remarks>
public interface ISftpWorkspaceView
{
    event EventHandler? RefreshRequested;
    event EventHandler<string>? NavigateRequested;
    event EventHandler? NewFolderRequested;
    event EventHandler? UploadRequested;
    event EventHandler? UploadFolderRequested;
    event EventHandler<RemoteFileItem>? DownloadRequested;
    event EventHandler<RemoteFileItem>? RenameRequested;
    event EventHandler<RemoteFileItem>? DeleteRequested;

    void Render(SftpSessionSnapshot snapshot);

    /// <summary>返回空串表示用户取消。<paramref name="initialText"/> 预填在输入框里并被全选。</summary>
    Task<string> PromptTextAsync(string title, string placeholder, string initialText = "");

    Task<bool> ConfirmOverwriteAsync(string name);
    Task<bool> ConfirmDeleteAsync(RemoteFileItem item);
    Task<IReadOnlyList<SftpUploadFile>> PickUploadFilesAsync();
    Task<SftpUploadDirectory?> PickUploadFolderAsync();

    /// <summary>询问用户选择下载目录；返回 <c>null</c> 表示取消。默认目录策略由工作区处理。</summary>
    Task<string?> PickDownloadDirectoryAsync();
}

/// <summary>双列面板传输入口；普通下载沿用默认目录策略，显式指定时使用本地当前目录。</summary>
public interface ISftpPaneTransferView
{
    event EventHandler<IReadOnlyList<SftpUploadEntry>>? UploadSelectionRequested;
    event EventHandler<IReadOnlyList<RemoteFileItem>>? DownloadSelectionRequested;
    event EventHandler<SftpPaneDownload>? DownloadToLocalRequested;
    void RefreshLocalDirectory();
}

public sealed record SftpPaneDownload(IReadOnlyList<RemoteFileItem> Items, string Destination);

/// <summary>属性窗口按需计算目录大小；窗口关闭时由视图取消请求。</summary>
public interface ISftpPropertiesView
{
    void SetDirectorySizeProvider(Func<RemoteFileItem, CancellationToken, Task<long>>? provider);
}
