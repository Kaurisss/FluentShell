namespace FluentShell.Core;

/// <summary>
/// 演示如何在上传和下载流程中集成传输队列管理。
/// 这些是对 SftpSessionController 现有方法的扩展示例。
/// </summary>
public static class SftpSessionControllerQueueExtensions
{
    /// <summary>
    /// 批量上传的示例实现：传输前先构建队列快照，传输过程中更新每项状态。
    /// </summary>
    public static async Task UploadBatchAsync(
        this SftpSessionController controller,
        IReadOnlyList<(string Name, long Size, Func<Task<Stream>> OpenInput)> files,
        TransferQueueManager queueManager,
        Func<string, Task<bool>> confirmOverwrite,
        CancellationToken cancellationToken)
    {
        // 1. 传输前构建队列快照：收集所有文件信息
        queueManager.Clear();
        foreach (var file in files)
        {
            queueManager.AddPendingItem(file.Name, file.Name, file.Size);
        }

        // 2. 逐文件传输，更新队列项状态
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 标记开始传输
            queueManager.StartTransfer(file.Name);

            try
            {
                // 执行上传逻辑（简化示例）
                await controller.UploadAsync(file.Name, file.OpenInput, confirmOverwrite);

                // 标记完成
                queueManager.CompleteTransfer(file.Name);
            }
            catch (Exception ex)
            {
                // 标记失败
                queueManager.FailTransfer(file.Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// 目录下载的队列集成示例：递归收集文件时构建队列，传输时更新状态。
    /// </summary>
    /// <remarks>
    /// 在 CollectDownloadPlanAsync 收集阶段调用 queueManager.AddPendingItem，
    /// 在实际传输每个文件时调用 StartTransfer/CompleteTransfer/FailTransfer/SkipTransfer。
    /// </remarks>
    public static void IntegrateQueueIntoDownload(
        TransferQueueManager queueManager,
        string relativePath,
        long sizeBytes)
    {
        // 收集阶段：添加到队列
        queueManager.AddPendingItem(
            System.IO.Path.GetFileName(relativePath),
            relativePath,
            sizeBytes);
    }
}
