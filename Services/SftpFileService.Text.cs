using FluentShell.Models;

namespace FluentShell.Services;

public sealed partial class SftpFileService
{
    public Task<byte[]> ReadTextFileAsync(string path, CancellationToken cancellationToken) =>
        ExecuteTextAsync(async client => await ReadTextAsync(client, path, cancellationToken).ConfigureAwait(false), cancellationToken);

    public Task SaveTextFileAsync(string path, ReadOnlyMemory<byte> expected, byte[] content, CancellationToken cancellationToken) =>
        ExecuteTextAsync(async client =>
        {
            TextFileDocument.EnsureSize(content.Length);
            var actual = await ReadTextAsync(client, path, cancellationToken).ConfigureAwait(false);
            TextFileBuffer.EnsureUnchanged(expected, actual);
            cancellationToken.ThrowIfCancellationRequested();
            using var input = new MemoryStream(content, writable: false);
            // Use the existing transfer channel. Do not cancel a write halfway through.
            await client.UploadAsync(input, path, CancellationToken.None).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<T> ExecuteTextAsync<T>(Func<ISftpClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            return await Task.Run(() => operation(client), cancellationToken).ConfigureAwait(false);
        }
        finally { _clientGate.Release(); }
    }

    private static async Task<byte[]> ReadTextAsync(ISftpClient client, string path, CancellationToken cancellationToken)
    {
        if (!path.StartsWith('/') || path.EndsWith('/') ||
            path[1..].Split('/').Any(segment => !SftpPathValidator.TryValidateRemoteName(segment, out _)))
            throw new IOException("远程文件路径无效。");
        cancellationToken.ThrowIfCancellationRequested();
        var name = path[(path.LastIndexOf('/') + 1)..];
        var entry = client.ListDirectory(RemotePath.Parent(path)).SingleOrDefault(item => item.Name == name);
        if (entry is null) throw new IOException("远程文件已不存在，请刷新目录后重试。");
        if (entry.IsDirectory || entry.IsSymbolicLink)
            throw new IOException("只能查看或编辑普通文件，请直接打开符号链接指向的文件。");
        TextFileDocument.EnsureSize(entry.Length);
        using var output = new TextFileBuffer();
        await client.DownloadAsync(path, output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}
