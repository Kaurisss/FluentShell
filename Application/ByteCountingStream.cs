namespace FluentShell.Core;

/// <summary>
/// 顺序传输计数流：上传统计读取字节，下载统计写入字节。
/// 保留底层流的读写能力，不支持寻址，避免重复统计。
/// </summary>
internal sealed class ByteCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onBytesTransferred;
    private long _totalTransferred;
    private readonly TransferControl? _control;
    private readonly CancellationToken _cancellationToken;

    public ByteCountingStream(Stream inner, Action<long> onBytesTransferred, TransferControl? control = null, CancellationToken cancellationToken = default)
    {
        _inner = inner;
        _control = control;
        _cancellationToken = cancellationToken;
        _onBytesTransferred = onBytesTransferred;
    }

    public override bool CanRead => _inner.CanRead;
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
        WaitForResume();
        _inner.Write(buffer, offset, count);
        Report(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WaitForResume();
        _inner.Write(buffer);
        Report(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        WaitForResume();
        _inner.WriteByte(value);
        Report(1);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WaitForResumeAsync(cancellationToken).ConfigureAwait(false);
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Report(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await WaitForResumeAsync(cancellationToken).ConfigureAwait(false);
        await _inner.WriteAsync(buffer, cancellationToken);
        Report(buffer.Length);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        WaitForResume();
        var read = _inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        WaitForResume();
        var read = _inner.Read(buffer);
        Report(read);
        return read;
    }

    public override int ReadByte()
    {
        WaitForResume();
        var value = _inner.ReadByte();
        if (value >= 0) Report(1);
        return value;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WaitForResumeAsync(cancellationToken).ConfigureAwait(false);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await WaitForResumeAsync(cancellationToken).ConfigureAwait(false);
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private void WaitForResume()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _control?.WaitAsync(_cancellationToken).GetAwaiter().GetResult();
    }

    private async Task WaitForResumeAsync(CancellationToken cancellationToken)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (_control is not null)
            await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Report(int count)
    {
        if (count <= 0) return;
        _totalTransferred += count;
        _onBytesTransferred(_totalTransferred);
    }
}
