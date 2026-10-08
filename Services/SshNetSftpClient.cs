using Renci.SshNet;

namespace FluentShell.Services;

/// <summary>SSH.NET 的 <see cref="Renci.SshNet.SftpClient"/> 到 <see cref="ISftpClient"/> 的适配器。</summary>
public sealed class SshNetSftpClient : ISftpClient
{
    private readonly Renci.SshNet.ISftpClient _client;
    private readonly Func<string, Task>? _deleteDirectoryRecursively;
    private string? _listedPath;
    private Dictionary<string, Renci.SshNet.Sftp.ISftpFile> _listedEntries = new(StringComparer.Ordinal);

    public SshNetSftpClient(Renci.SshNet.ISftpClient client, Func<string, Task>? deleteDirectoryRecursively = null)
    {
        _client = client;
        _deleteDirectoryRecursively = deleteDirectoryRecursively;
    }

    public bool IsConnected => _client.IsConnected;

    public IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path) =>
        ReadDirectoryEntries(path).Values
            .Select(item => new RemoteDirectoryEntry(
                item.Name,
                item.FullName,
                item.IsDirectory,
                item.Length,
                item.LastWriteTime,
                item.IsSymbolicLink))
            .ToList();

    private Dictionary<string, Renci.SshNet.Sftp.ISftpFile> ReadDirectoryEntries(string path)
    {
        // 只保留最近一次列表，删除同一目录的多个文件无需反复读取整个目录。
        _listedEntries = _client.ListDirectory(path).ToDictionary(item => item.Name, StringComparer.Ordinal);
        _listedPath = path;
        return _listedEntries;
    }

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public bool Exists(string path) => _client.Exists(path);

    public bool IsDirectory(string path) => _client.GetAttributes(path).IsDirectory;

    public void DeleteDirectory(string path) => DeleteEntry(path);

    public void DeleteFile(string path) => DeleteEntry(path);

    private void DeleteEntry(string path)
    {
        // SftpClient.DeleteFile/DeleteDirectory 会先 REALPATH，可能删除链接目标。
        // 列表返回的 ISftpFile.Delete 使用原始 FullName，直接删除条目自身。
        var name = path[(path.LastIndexOf('/') + 1)..];
        var parent = RemotePath.Parent(path);
        var entries = _listedPath == parent ? _listedEntries : ReadDirectoryEntries(parent);
        if (!entries.TryGetValue(name, out var entry))
            throw new Renci.SshNet.Common.SftpPathNotFoundException("待删除的远程项目已不存在。");
        entry.Delete();
        entries.Remove(name);
    }

    public async Task<bool> TryDeleteDirectoryRecursivelyAsync(string path)
    {
        if (_deleteDirectoryRecursively is null) return false;
        try
        {
            await _deleteDirectoryRecursively(path).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // 失败也可能已删除部分内容，不保留旧目录条目。
            _listedEntries.Clear();
            _listedPath = null;
        }
    }

    public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken) =>
        _client.UploadFileAsync(input, remotePath, cancellationToken);

    public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken) =>
        _client.DownloadFileAsync(remotePath, output, cancellationToken);

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        _client.RenameFileAsync(sourcePath, destinationPath, cancellationToken);
}
