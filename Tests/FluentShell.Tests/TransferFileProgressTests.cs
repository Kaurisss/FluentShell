using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public sealed class TransferFileProgressTests
{
    private static TransferTask CreateTask() => new TransferCenter().Add(
        Guid.NewGuid(), "server", "下载", "files", "target", () => Task.CompletedTask, () => true);

    [TestMethod]
    public void Progress_ticks_preserve_collection_and_rows_without_collection_events()
    {
        var task = CreateTask();
        var file = new TransferQueueItem("file", "file", 100, TransferItemState.Transferring);
        task.UpdateFiles([file]);
        var files = task.Files;
        var row = files[0];
        var collectionEvents = 0;
        var properties = new List<string?>();
        files.CollectionChanged += (_, _) => collectionEvents++;
        row.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        for (var bytes = 1; bytes <= 100; bytes++)
            task.UpdateFiles([file.WithProgress(bytes)]);

        Assert.AreSame(files, task.Files);
        Assert.AreSame(row, task.Files[0]);
        Assert.AreEqual(0, collectionEvents);
        Assert.AreEqual(100d, row.PercentComplete);
        Assert.AreEqual("100%", row.StatusLabel);
        Assert.Contains(nameof(TransferFileProgress.PercentComplete), properties);
        Assert.DoesNotContain(string.Empty, properties);
        properties.Clear();
        task.UpdateFiles([file.WithProgress(100)]);
        Assert.HasCount(0, properties);
    }

    [TestMethod]
    public void Membership_changes_preserve_surviving_rows_and_update_errors()
    {
        var task = CreateTask();
        var a = new TransferQueueItem("a", "a", 100, TransferItemState.Pending);
        var b = a with { FileName = "b", RelativePath = "b" };
        task.UpdateFiles([a, b]);
        var row = task.Files[1];
        task.UpdateFiles([b.WithState(TransferItemState.Failed, "failure"), a]);
        Assert.AreSame(row, task.Files[0]);
        Assert.AreEqual("失败", row.StatusLabel);
        Assert.AreEqual("failure", row.ErrorMessage);
        task.UpdateFiles([b.WithState(TransferItemState.Completed)]);
        Assert.HasCount(1, task.Files);
        Assert.AreSame(row, task.Files[0]);
        Assert.AreEqual("已完成", row.StatusLabel);
        Assert.IsNull(row.ErrorMessage);
        task.UpdateFiles([]);
        Assert.HasCount(0, task.Files);
    }
}
