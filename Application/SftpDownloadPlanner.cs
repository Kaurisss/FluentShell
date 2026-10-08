using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Core;

/// <summary>只统计远程条目；目录创建和文件写入在传输阶段完成。</summary>
internal static class SftpDownloadPlanner
{
    private const int MaxDepth = 128;

    internal sealed record Item(string RemotePath, string LocalPath, string RelativePath,
        bool IsDirectory, long SizeBytes, string? Error = null);

    public static async Task<IReadOnlyList<Item>> BuildAsync(IReadOnlyList<RemoteFileItem> roots,
        string destinationDirectory, ISftpFileService service, Func<CancellationToken, Task> wait,
        Action<string, int> report, CancellationToken token)
    {
        var plan = new List<Item>();
        var localPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(RemoteFileItem Entry, string Parent, string RelativePath, int Depth)>();
        for (var i = roots.Count - 1; i >= 0; i--)
            pending.Push((roots[i], destinationDirectory, roots[i].Name, 0));

        while (pending.TryPop(out var current))
        {
            await wait(token);
            token.ThrowIfCancellationRequested();
            var entry = current.Entry;
            var size = entry.IsDirectory ? 0 : Math.Max(0, entry.SizeBytes);
            if (!SftpPathValidator.TryResolveDownloadPath(current.Parent, entry.Name, out var path, out var error))
            {
                plan.Add(new(entry.FullPath, "", current.RelativePath, entry.IsDirectory, 0, error));
                continue;
            }
            if (localPaths.TryGetValue(path, out var duplicateIndex))
            {
                plan[duplicateIndex] = plan[duplicateIndex] with
                {
                    Error = "远程条目在 Windows 上映射为同一个名称，无法同时下载。"
                };
                continue;
            }
            if (entry.IsSymbolicLink) error = "不支持下载远程符号链接。";
            else if (current.Depth > MaxDepth) error = "文件夹层级过深，可能存在循环链接。";
            else error = "";
            var index = plan.Count;
            localPaths.Add(path, index);
            plan.Add(new(entry.FullPath, path, current.RelativePath, entry.IsDirectory, size,
                error.Length == 0 ? null : error));
            if (error.Length > 0 || !entry.IsDirectory) continue;

            report(current.RelativePath, plan.Count);
            try
            {
                var children = await service.ListDirectoryAsync(entry.FullPath);
                await wait(token);
                token.ThrowIfCancellationRequested();
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    // 文件服务为浏览合成的父目录条目不属于下载内容。
                    if (child.Name is "." or "..") continue;
                    // 使用父路径及待校验的名称构造来源，不采用服务器返回的任意 FullPath。
                    var source = new RemoteFileItem
                    {
                        Name = child.Name, FullPath = RemotePath.Combine(entry.FullPath, child.Name),
                        IsDirectory = child.IsDirectory, IsSymbolicLink = child.IsSymbolicLink,
                        SizeBytes = child.SizeBytes
                    };
                    pending.Push((source, path, $"{current.RelativePath}/{child.Name}", current.Depth + 1));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { plan[index] = plan[index] with { Error = exception.Message }; }
        }
        return plan;
    }
}
