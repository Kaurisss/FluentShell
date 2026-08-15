using FluentShell.Models;

namespace FluentShell.Core;

/// <summary>一个待上传的本地文件：远程侧使用的文件名，以及按需打开的读取流。</summary>
public sealed record SftpUploadFile(string Name, Func<Task<Stream>> OpenRead);

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
    event EventHandler<RemoteFileItem>? DownloadRequested;
    event EventHandler<RemoteFileItem>? RenameRequested;
    event EventHandler<RemoteFileItem>? DeleteRequested;
    event EventHandler? CancelTransferRequested;

    void Render(SftpSessionSnapshot snapshot);

    /// <summary>
    /// 打开传输状态面板。由工作区在一批传输真正开始时调用一次——
    /// 视图无法从快照区分"新的一批"与"批内的下一个文件"，这个时机只有流程知道。
    /// </summary>
    void ShowTransferStatus();

    /// <summary>返回空串表示用户取消。<paramref name="initialText"/> 预填在输入框里并被全选。</summary>
    Task<string> PromptTextAsync(string title, string placeholder, string initialText = "");

    Task<bool> ConfirmOverwriteAsync(string name);
    Task<bool> ConfirmDeleteAsync(RemoteFileItem item);
    Task<IReadOnlyList<SftpUploadFile>> PickUploadFilesAsync();

    /// <summary>返回 <c>null</c> 表示用户没有选择目录。</summary>
    Task<string?> PickDownloadDirectoryAsync();
}
