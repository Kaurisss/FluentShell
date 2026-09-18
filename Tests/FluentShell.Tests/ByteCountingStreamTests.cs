using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public sealed class ByteCountingStreamTests
{
    [TestMethod]
    [DataRow("Array")]
    [DataRow("Span")]
    [DataRow("Byte")]
    [DataRow("ArrayAsync")]
    [DataRow("MemoryAsync")]
    public async Task Reads_report_actual_bytes_once_and_do_not_report_end_of_stream(string mode)
    {
        var reports = new List<long>();
        using var input = new MemoryStream(new byte[] { 0, 128, 255 }, writable: false);
        using var stream = new ByteCountingStream(input, reports.Add);
        var buffer = new byte[8];
        var received = new List<byte>();
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanWrite);
        Assert.IsFalse(stream.CanSeek);

        int count;
        do
        {
            switch (mode)
            {
                case "Array":
                    count = stream.Read(buffer, 1, 4);
                    received.AddRange(buffer.Skip(1).Take(count));
                    break;
                case "Span":
                    count = stream.Read(buffer.AsSpan(1, 4));
                    received.AddRange(buffer.Skip(1).Take(count));
                    break;
                case "Byte":
                    var value = stream.ReadByte();
                    count = value < 0 ? 0 : 1;
                    if (value >= 0) received.Add((byte)value);
                    break;
                case "ArrayAsync":
                    count = await stream.ReadAsync(buffer, 1, 4, CancellationToken.None);
                    received.AddRange(buffer.Skip(1).Take(count));
                    break;
                default:
                    count = await stream.ReadAsync(buffer.AsMemory(1, 4));
                    received.AddRange(buffer.Skip(1).Take(count));
                    break;
            }
        } while (count > 0);

        CollectionAssert.AreEqual(new byte[] { 0, 128, 255 }, received);
        CollectionAssert.AreEqual(mode == "Byte" ? new long[] { 1, 2, 3 } : new long[] { 3 }, reports);
    }

    [TestMethod]
    public async Task Cancelled_reads_do_not_report_progress()
    {
        var reports = new List<long>();
        using var stream = new ByteCountingStream(new MemoryStream(new byte[] { 1 }), reports.Add);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await stream.ReadAsync(new byte[1].AsMemory(), cts.Token));

        Assert.IsEmpty(reports);
    }

    [TestMethod]
    public async Task Writes_still_report_progress_and_dispose_the_inner_stream()
    {
        var reports = new List<long>();
        var output = new MemoryStream();
        using (var stream = new ByteCountingStream(output, reports.Add))
        {
            stream.WriteByte(1);
            stream.Write(new byte[] { 2, 3 }, 0, 2);
            stream.Write(new byte[] { 4 }.AsSpan());
            await stream.WriteAsync(new byte[] { 5 }, 0, 1, CancellationToken.None);
            await stream.WriteAsync(new byte[] { 6 }.AsMemory());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, output.ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 3, 4, 5, 6 }, reports);
        }
        Assert.IsFalse(output.CanWrite);
    }
}
