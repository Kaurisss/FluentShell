using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public class TransferQueueTests
{
    [TestMethod]
    public void TransferQueueItem_CalculatesPercentComplete()
    {
        var item = new TransferQueueItem("test.txt", "test.txt", 1000, TransferItemState.Transferring, 250);

        Assert.AreEqual(25.0, item.PercentComplete);
    }

    [TestMethod]
    public void TransferQueueItem_HandlesZeroSize()
    {
        var item = new TransferQueueItem("test.txt", "test.txt", 0, TransferItemState.Transferring, 0);

        Assert.AreEqual(0.0, item.PercentComplete);
    }

    [TestMethod]
    public void TransferQueueItem_WithState_UpdatesStateAndError()
    {
        var item = new TransferQueueItem("test.txt", "test.txt", 1000, TransferItemState.Pending);
        var updated = item.WithState(TransferItemState.Failed, "Network error");

        Assert.AreEqual(TransferItemState.Failed, updated.State);
        Assert.AreEqual("Network error", updated.ErrorMessage);
    }

    [TestMethod]
    public void TransferQueue_CalculatesStatistics()
    {
        var items = new List<TransferQueueItem>
        {
            new("file1.txt", "file1.txt", 100, TransferItemState.Completed),
            new("file2.txt", "file2.txt", 200, TransferItemState.Completed),
            new("file3.txt", "file3.txt", 300, TransferItemState.Skipped),
            new("file4.txt", "file4.txt", 400, TransferItemState.Failed),
            new("file5.txt", "file5.txt", 500, TransferItemState.Pending)
        };

        var queue = new TransferQueue(items, 5, 2, 1, 1);

        Assert.AreEqual(5, queue.TotalCount);
        Assert.AreEqual(2, queue.CompletedCount);
        Assert.AreEqual(1, queue.SkippedCount);
        Assert.AreEqual(1, queue.FailedCount);
        Assert.AreEqual(1, queue.PendingCount);
        Assert.IsTrue(queue.HasItems);
        Assert.IsFalse(queue.IsCompleted);
    }

    [TestMethod]
    public void TransferQueue_IsCompleted_WhenAllItemsProcessed()
    {
        var items = new List<TransferQueueItem>
        {
            new("file1.txt", "file1.txt", 100, TransferItemState.Completed),
            new("file2.txt", "file2.txt", 200, TransferItemState.Skipped)
        };

        var queue = new TransferQueue(items, 2, 1, 1, 0);

        Assert.IsTrue(queue.IsCompleted);
    }

    [TestMethod]
    public void TransferQueueManager_AddPendingItem_AddsToQueue()
    {
        var manager = new TransferQueueManager();

        manager.AddPendingItem("test.txt", "folder/test.txt", 1024);

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(1, snapshot.TotalCount);
        Assert.AreEqual("test.txt", snapshot.Items[0].FileName);
        Assert.AreEqual(TransferItemState.Pending, snapshot.Items[0].State);
    }

    [TestMethod]
    public void TransferQueueManager_StartTransfer_UpdatesState()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test.txt", "test.txt", 1024);

        manager.StartTransfer("test.txt");

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(TransferItemState.Transferring, snapshot.Items[0].State);
    }

    [TestMethod]
    public void TransferQueueManager_CompleteTransfer_UpdatesState()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test.txt", "test.txt", 1024);
        manager.StartTransfer("test.txt");

        manager.CompleteTransfer("test.txt");

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(TransferItemState.Completed, snapshot.Items[0].State);
        Assert.AreEqual(1, snapshot.CompletedCount);
    }

    [TestMethod]
    public void TransferQueueManager_SkipTransfer_UpdatesState()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test.txt", "test.txt", 1024);

        manager.SkipTransfer("test.txt");

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(TransferItemState.Skipped, snapshot.Items[0].State);
        Assert.AreEqual(1, snapshot.SkippedCount);
    }

    [TestMethod]
    public void TransferQueueManager_FailTransfer_UpdatesStateWithError()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test.txt", "test.txt", 1024);
        manager.StartTransfer("test.txt");

        manager.FailTransfer("test.txt", "Connection timeout");

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(TransferItemState.Failed, snapshot.Items[0].State);
        Assert.AreEqual("Connection timeout", snapshot.Items[0].ErrorMessage);
        Assert.AreEqual(1, snapshot.FailedCount);
    }

    [TestMethod]
    public void TransferQueueManager_UpdateProgress_UpdatesBytesTransferred()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test.txt", "test.txt", 1024);
        manager.StartTransfer("test.txt");

        var snapshotUpdated = false;
        manager.UpdateProgress("test.txt", 512, () => snapshotUpdated = true);

        Assert.IsTrue(snapshotUpdated);
        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(512, snapshot.Items[0].BytesTransferred);
        Assert.AreEqual(50.0, snapshot.Items[0].PercentComplete);
    }

    [TestMethod]
    public void TransferQueueManager_Clear_RemovesAllItems()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("test1.txt", "test1.txt", 1024);
        manager.AddPendingItem("test2.txt", "test2.txt", 2048);

        manager.Clear();

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(0, snapshot.TotalCount);
        Assert.IsFalse(snapshot.HasItems);
    }

    [TestMethod]
    public void TransferQueueManager_AddPendingItems_AddsBatch()
    {
        var manager = new TransferQueueManager();
        var files = new[]
        {
            ("file1.txt", "folder/file1.txt", 100L),
            ("file2.txt", "folder/file2.txt", 200L),
            ("file3.txt", "folder/file3.txt", 300L)
        };

        manager.AddPendingItems(files);

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(3, snapshot.TotalCount);
        Assert.AreEqual(3, snapshot.PendingCount);
    }

    [TestMethod]
    public void TransferQueueManager_GetStatistics_ReturnsCorrectCounts()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("file1.txt", "file1.txt", 100);
        manager.AddPendingItem("file2.txt", "file2.txt", 200);
        manager.AddPendingItem("file3.txt", "file3.txt", 300);

        manager.CompleteTransfer("file1.txt");
        manager.SkipTransfer("file2.txt");
        manager.FailTransfer("file3.txt", "Error");

        var (total, completed, skipped, failed) = manager.GetStatistics();

        Assert.AreEqual(3, total);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(1, skipped);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    public void TransferQueueManager_SnapshotIsSorted_ByRelativePath()
    {
        var manager = new TransferQueueManager();
        manager.AddPendingItem("zebra.txt", "zebra.txt", 100);
        manager.AddPendingItem("alpha.txt", "alpha.txt", 200);
        manager.AddPendingItem("beta.txt", "beta.txt", 300);

        var snapshot = manager.CreateSnapshot();

        Assert.AreEqual("alpha.txt", snapshot.Items[0].RelativePath);
        Assert.AreEqual("beta.txt", snapshot.Items[1].RelativePath);
        Assert.AreEqual("zebra.txt", snapshot.Items[2].RelativePath);
    }

    [TestMethod]
    public void TransferQueueManager_ThreadSafety_ConcurrentUpdates()
    {
        var manager = new TransferQueueManager();
        var files = Enumerable.Range(0, 100)
            .Select(i => ($"file{i}.txt", $"file{i}.txt", (long)i * 100))
            .ToArray();

        manager.AddPendingItems(files);

        // 模拟并发更新
        Parallel.ForEach(files, file =>
        {
            manager.StartTransfer(file.Item2);
            Thread.Sleep(1);
            manager.UpdateProgress(file.Item2, file.Item3 / 2, () => { });
            Thread.Sleep(1);
            manager.CompleteTransfer(file.Item2);
        });

        var snapshot = manager.CreateSnapshot();
        Assert.AreEqual(100, snapshot.TotalCount);
        Assert.AreEqual(100, snapshot.CompletedCount);
    }
}
