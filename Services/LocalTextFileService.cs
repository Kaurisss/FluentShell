using FluentShell.Models;

namespace FluentShell.Services;

public sealed class LocalTextFileService : ITextFileService
{
    public Task<byte[]> ReadTextFileAsync(string path, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await TextFileBuffer.ReadAsync(input, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task SaveTextFileAsync(string path, ReadOnlyMemory<byte> expected, byte[] content, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        TextFileDocument.EnsureSize(content.Length);
        // Exclusive access covers both the comparison and the write. Opening an existing
        // file also retains its permissions and never recreates an externally deleted file.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous);
        var actual = await TextFileBuffer.ReadAsync(file, cancellationToken).ConfigureAwait(false);
        TextFileBuffer.EnsureUnchanged(expected, actual);
        cancellationToken.ThrowIfCancellationRequested();
        file.Position = 0;
        // Once writing starts, closing the editor must not cancel and truncate the file.
        await file.WriteAsync(content, CancellationToken.None).ConfigureAwait(false);
        file.SetLength(content.Length);
        await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }, cancellationToken);
}
