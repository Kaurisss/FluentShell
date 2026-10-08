using FluentShell.Services;

namespace FluentShell.Core;

/// <summary>后台统计本地目录；不跟随符号链接或目录联接，不在统计阶段修改远程目录。</summary>
internal static class SftpUploadPlanner
{
    internal sealed record Item(string RelativePath, SftpUploadFile? File, long SizeBytes,
        string? Error = null)
    {
        public bool IsDirectory => File is null;
    }

    public static Task<IReadOnlyList<Item>> BuildAsync(IReadOnlyList<SftpUploadEntry> entries,
        Func<CancellationToken, Task> wait, CancellationToken token) => Task.Run(async () =>
    {
        var plan = new List<Item>();
        foreach (var entry in entries)
        {
            await wait(token);
            token.ThrowIfCancellationRequested();
            if (!SftpPathValidator.TryValidateRemoteName(entry.Name, out var error))
            {
                plan.Add(new(entry.Name, entry as SftpUploadFile, 0, error));
                continue;
            }
            if (entry is SftpUploadFile file)
            {
                long size = 0;
                // 保留按需打开流的接缝；无法读取的文件也要出现在失败明细中。
                try { using var stream = await file.OpenRead(); size = stream.CanSeek ? stream.Length : 0; }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { plan.Add(new(file.Name, file, 0, exception.Message)); continue; }
                plan.Add(new(file.Name, file, size));
                continue;
            }

            var directory = (SftpUploadDirectory)entry;
            var pending = new Stack<(string LocalPath, string RelativePath)>();
            pending.Push((Path.GetFullPath(directory.LocalPath), directory.Name));
            while (pending.TryPop(out var current))
            {
                await wait(token);
                token.ThrowIfCancellationRequested();
                var index = plan.Count;
                plan.Add(new(current.RelativePath, null, 0));
                try
                {
                    EnsureNoLinks(directory.LocalPath, current.LocalPath);
                    var children = new List<(string LocalPath, string RelativePath)>();
                    foreach (var info in new DirectoryInfo(current.LocalPath).EnumerateFileSystemInfos())
                    {
                        await wait(token);
                        token.ThrowIfCancellationRequested();
                        var relativePath = $"{current.RelativePath}/{info.Name}";
                        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                        var localPath = info.FullName;
                        var localFile = isDirectory ? null : new SftpUploadFile(info.Name,
                            () => Task.Run<Stream>(() =>
                            {
                                // 再次检查，避免统计之后被换成链接的条目逃出所选目录。
                                EnsureNoLinks(directory.LocalPath, localPath);
                                return new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            }));
                        if (!SftpPathValidator.TryValidateRemoteName(info.Name, out error))
                            plan.Add(new(relativePath, localFile, 0, error));
                        else if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            plan.Add(new(relativePath, localFile, 0, "不支持上传符号链接或目录联接。"));
                        else if (isDirectory)
                            children.Add((localPath, relativePath));
                        else
                            plan.Add(new(relativePath, localFile, ((FileInfo)info).Length));
                    }
                    // 栈保持父目录先于子目录，空目录也保留在计划里。
                    for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { plan[index] = plan[index] with { Error = exception.Message }; }
            }
        }
        return (IReadOnlyList<Item>)plan;
    }, token);

    private static void EnsureNoLinks(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("上传条目必须位于所选目录内。");
        var current = fullRoot;
        Check(current);
        if (relative == ".") return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        static void Check(string candidate)
        {
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不支持上传符号链接或目录联接。");
        }
    }
}
