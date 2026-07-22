using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class FileSizeFormatterTests
{
    [DataRow(0L, "0 B")]
    [DataRow(1023L, "1023 B")]
    [DataRow(1024L, "1.0 KB")]
    [DataRow(1024L * 1024, "1.0 MB")]
    [DataRow(1024L * 1024 * 1024, "1.0 GB")]
    [TestMethod]
    public void Format_preserves_existing_units(long bytes, string expected)
    {
        Assert.AreEqual(expected, FileSizeFormatter.Format(bytes));
    }
}