namespace FluentShell.Services;

/// <summary>编辑器的文件接缝；保存前比较原始字节，拒绝覆盖外部修改。</summary>
public interface ITextFileService
{
    Task<byte[]> ReadTextFileAsync(string path, CancellationToken cancellationToken);
    Task SaveTextFileAsync(string path, ReadOnlyMemory<byte> expected, byte[] content, CancellationToken cancellationToken);
}
