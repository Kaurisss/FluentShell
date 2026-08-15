using Renci.SshNet;

namespace FluentShell.Services;

/// <summary>SSH.NET 的 <see cref="Renci.SshNet.SftpClient"/> 到 <see cref="ISftpClient"/> 的适配器。</summary>
public sealed class SshNetSftpClient : ISftpClient
{
    private readonly SftpClient _client;

    public SshNetSftpClient(SftpClient client)
    {
        _client = client;
    }

    public bool IsConnected => _client.IsConnected;

    public IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path) =>
        _client.ListDirectory(path)
            .Select(item => new RemoteDirectoryEntry(
                item.Name,
                item.FullName,
                item.IsDirectory,
                item.Length,
                item.LastWriteTime))
            .ToList();

    public void CreateDirectory(string path) => _client.CreateDirectory(path);

    public bool Exists(string path) => _client.Exists(path);

    public void DeleteDirectory(string path) => _client.DeleteDirectory(path);

    public void DeleteFile(string path) => _client.DeleteFile(path);

    public Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken) =>
        _client.UploadFileAsync(input, remotePath, cancellationToken);

    public Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken) =>
        _client.DownloadFileAsync(remotePath, output, cancellationToken);

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        _client.RenameFileAsync(sourcePath, destinationPath, cancellationToken);
}
