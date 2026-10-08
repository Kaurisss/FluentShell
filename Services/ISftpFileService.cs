using FluentShell.Models;

namespace FluentShell.Services;

public interface ISftpFileService
{
    bool IsConnected { get; }

    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path);
    /// <summary>递归统计目录内的文件字节数，包含隐藏文件，不跟随符号链接。</summary>
    Task<long> GetDirectorySizeAsync(string path, CancellationToken cancellationToken);
    Task CreateDirectoryAsync(string path);
    Task<bool> ExistsAsync(string path);
    Task<bool> IsDirectoryAsync(string path);
    Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken);
    Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken);
    Task RenameAsync(string sourcePath, string destinationPath);
    Task DeleteAsync(RemoteFileItem item);
}
