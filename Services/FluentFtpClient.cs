using FluentFTP;

namespace FluentShell.Services;

/// <summary>Adapts FTP to the existing remote-file boundary. Synchronous members run on the file service's worker thread.</summary>
internal sealed class FluentFtpClient(AsyncFtpClient client) : ISftpClient
{
    public bool IsConnected => client.IsConnected;

    internal static string ValidateArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("FTP 路径或用户名不能包含换行符或空字符。");
        return value;
    }

    public IReadOnlyList<RemoteDirectoryEntry> ListDirectory(string path) =>
        client.GetListing(ValidateArgument(path)).GetAwaiter().GetResult()
            .Select(item => new RemoteDirectoryEntry(item.Name, item.FullName,
                item.Type == FtpObjectType.Directory, item.Size, item.Modified)).ToList();

    public void CreateDirectory(string path) => client.CreateDirectory(ValidateArgument(path)).GetAwaiter().GetResult();
    public bool Exists(string path) => client.FileExists(ValidateArgument(path)).GetAwaiter().GetResult() ||
        client.DirectoryExists(path).GetAwaiter().GetResult();
    public void DeleteFile(string path) => client.DeleteFile(ValidateArgument(path)).GetAwaiter().GetResult();

    public void DeleteDirectory(string path)
    {
        // DeleteDirectory in FluentFTP is recursive; preserve the application's empty-directory-only semantics.
        var reply = client.Execute("RMD " + ValidateArgument(path)).GetAwaiter().GetResult();
        if (!reply.Success) throw new IOException("无法删除 FTP 目录，请确认目录为空且具有删除权限。");
    }

    public async Task UploadAsync(Stream input, string remotePath, CancellationToken cancellationToken)
    {
        var status = await client.UploadStream(input, ValidateArgument(remotePath), FtpRemoteExists.Overwrite,
            token: cancellationToken).ConfigureAwait(false);
        if (status != FtpStatus.Success) throw new IOException("FTP 上传未完成。");
    }

    public async Task DownloadAsync(string remotePath, Stream output, CancellationToken cancellationToken)
    {
        if (!await client.DownloadStream(output, ValidateArgument(remotePath), token: cancellationToken).ConfigureAwait(false))
            throw new IOException("FTP 下载未完成。");
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
        client.Rename(ValidateArgument(sourcePath), ValidateArgument(destinationPath), cancellationToken);
}
