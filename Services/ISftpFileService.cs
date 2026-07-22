using FluentShell.Models;

namespace FluentShell.Services;

public interface ISftpFileService
{
    bool IsConnected { get; }

    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, bool showHiddenFiles);
    Task CreateDirectoryAsync(string path);
    Task<bool> ExistsAsync(string path);
    Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken);
    Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken);
    Task RenameAsync(string sourcePath, string destinationPath);
    Task DeleteAsync(RemoteFileItem item);
}