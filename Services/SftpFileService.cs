using FluentShell.Models;
using Renci.SshNet;

namespace FluentShell.Services;

public sealed class SftpFileService : ISftpFileService
{
    private readonly Func<SftpClient?> _clientProvider;

    public SftpFileService(Func<SftpClient?> clientProvider)
    {
        _clientProvider = clientProvider;
    }

    public bool IsConnected => _clientProvider()?.IsConnected == true;

    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path)
    {
        var client = GetConnectedClient();
        return await Task.Run(() =>
        {
            var items = client.ListDirectory(path)
                .Where(item => SftpDirectoryEntryPolicy.ShouldDisplay(item.Name))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new RemoteFileItem
                {
                    Name = item.Name,
                    IsDirectory = item.IsDirectory,
                    FullPath = item.FullName,
                    TypeLabel = item.IsDirectory ? "目录" : "文件",
                    SizeBytes = item.IsDirectory ? -1 : item.Length,
                    SizeLabel = item.IsDirectory ? "—" : FileSizeFormatter.Format(item.Length),
                    ModifiedAt = item.LastWriteTime.ToLocalTime(),
                    ModifiedLabel = item.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                })
                .ToList();

            if (path != "/")
            {
                items.Insert(0, new RemoteFileItem
                {
                    Name = "..",
                    IsDirectory = true,
                    FullPath = RemotePath.Parent(path),
                    TypeLabel = "目录",
                    SizeBytes = -1,
                    SizeLabel = "—",
                    ModifiedLabel = string.Empty
                });
            }

            return (IReadOnlyList<RemoteFileItem>)items;
        });
    }

    public Task CreateDirectoryAsync(string path) =>
        Task.Run(() => GetConnectedClient().CreateDirectory(path));

    public Task<bool> ExistsAsync(string path) =>
        Task.Run(() => GetConnectedClient().Exists(path));

    public Task UploadAsync(
        Stream input,
        string remotePath,
        CancellationToken cancellationToken) =>
        GetConnectedClient().UploadFileAsync(input, remotePath, cancellationToken);

    public Task DownloadAsync(
        string remotePath,
        Stream output,
        CancellationToken cancellationToken) =>
        GetConnectedClient().DownloadFileAsync(remotePath, output, cancellationToken);

    public Task RenameAsync(string sourcePath, string destinationPath) =>
        GetConnectedClient().RenameFileAsync(sourcePath, destinationPath, CancellationToken.None);

    public Task DeleteAsync(RemoteFileItem item) => Task.Run(() =>
    {
        var client = GetConnectedClient();
        if (item.IsDirectory) client.DeleteDirectory(item.FullPath);
        else client.DeleteFile(item.FullPath);
    });

    private SftpClient GetConnectedClient()
    {
        var client = _clientProvider();
        return client?.IsConnected == true
            ? client
            : throw new InvalidOperationException("SFTP 尚未连接。");
    }
}