using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>在实际下载过程中限制大小，不依赖服务器报告的长度。</summary>
internal sealed class TextFileBuffer : MemoryStream
{
    private void CheckWrite(int count) => TextFileDocument.EnsureSize(Math.Max(Length, checked(Position + count)));
    public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
    public override void SetLength(long value) { TextFileDocument.EnsureSize(value); base.SetLength(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    internal static async Task<byte[]> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        if (input.CanSeek) TextFileDocument.EnsureSize(input.Length);
        using var output = new TextFileBuffer();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    internal static void EnsureUnchanged(ReadOnlyMemory<byte> expected, byte[] actual)
    {
        if (!expected.Span.SequenceEqual(actual))
            throw new IOException("文件已被其他程序修改，未覆盖原文件。请复制当前修改，关闭并重新打开文件后再编辑。");
    }
}
