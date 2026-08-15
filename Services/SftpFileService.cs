using FluentShell.Models;

namespace FluentShell.Services;

public sealed class SftpFileService : ISftpFileService
{
    private readonly Func<ISftpClient?> _clientProvider;

    public SftpFileService(Func<ISftpClient?> clientProvider)
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
                .Where(entry => entry.Name is not "." and not "..")
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToRemoteFileItem)
                .ToList();

            // 非根目录合成一个指向父目录的条目，它不来自远程列表，因此排在过滤与排序之后。
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
        GetConnectedClient().UploadAsync(input, remotePath, cancellationToken);

    public Task DownloadAsync(
        string remotePath,
        Stream output,
        CancellationToken cancellationToken) =>
        GetConnectedClient().DownloadAsync(remotePath, output, cancellationToken);

    public Task RenameAsync(string sourcePath, string destinationPath) =>
        GetConnectedClient().RenameAsync(sourcePath, destinationPath, CancellationToken.None);

    public Task DeleteAsync(RemoteFileItem item) => Task.Run(() =>
    {
        var client = GetConnectedClient();
        if (item.IsDirectory) client.DeleteDirectory(item.FullPath);
        else client.DeleteFile(item.FullPath);
    });

    private static RemoteFileItem ToRemoteFileItem(RemoteDirectoryEntry entry)
    {
        var modifiedAt = entry.LastWriteTime.ToLocalTime();
        return new RemoteFileItem
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            FullPath = entry.FullPath,
            TypeLabel = entry.IsDirectory ? "目录" : "文件",
            SizeBytes = entry.IsDirectory ? -1 : entry.Length,
            SizeLabel = entry.IsDirectory ? "—" : FormatSize(entry.Length),
            ModifiedAt = modifiedAt,
            ModifiedLabel = modifiedAt.ToString("yyyy-MM-dd HH:mm")
        };
    }

    private static string FormatSize(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{length / 1024d / 1024d:0.0} MB",
        _ => $"{length / 1024d / 1024d / 1024d:0.0} GB"
    };

    private ISftpClient GetConnectedClient()
    {
        var client = _clientProvider();
        return client?.IsConnected == true
            ? client
            : throw new InvalidOperationException("SFTP 尚未连接。");
    }
}
