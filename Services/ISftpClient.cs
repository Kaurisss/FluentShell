namespace FluentShell.Services;

/// <summary>远程目录里的一个原始条目，尚未经过过滤、排序与标签化。</summary>
public sealed record RemoteDirectoryEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTime);

/// <summary>
/// SFTP 客户端的接缝：<see cref="SftpFileService"/> 需要的远程操作，不含 SSH.NET 类型。
/// </summary>
/// <remarks>
/// 生产适配器是 <see cref="SshNetSftpClient"/>；测试以伪客户端替换，
/// 目录列表的过滤、排序与标签化因此可以在没有远程主机的情况下验证。
/// </remarks>
public interface ISftpClient
{
    bool IsConnected { get; }

    IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path);
    void CreateDirectory(string path);
    bool Exists(string path);
    void DeleteDirectory(string path);
    void DeleteFile(string path);

    Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken);
    Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken);
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
