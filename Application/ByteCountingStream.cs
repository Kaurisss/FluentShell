namespace FluentShell.Core;

/// <summary>
/// 只写计数流：包住下载的输出流，把累计写入字节数回调出去。
/// 这是把传输进度从 <c>ISftpFileService.DownloadAsync</c> 里引出来的最小接缝——
/// 不需要给服务接口加进度参数。
/// </summary>
internal sealed class ByteCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onBytesWritten;
    private long _totalWritten;

    public ByteCountingStream(Stream inner, Action<long> onBytesWritten)
    {
        _inner = inner;
        _onBytesWritten = onBytesWritten;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Report(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Report(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        Report(1);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Report(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        Report(buffer.Length);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private void Report(int written)
    {
        _totalWritten += written;
        _onBytesWritten(_totalWritten);
    }
}
