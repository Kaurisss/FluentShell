using FluentShell.Models;

namespace FluentShell.Services;

public sealed class SftpFileService : ISftpFileService
{
    private readonly Func<ISftpClient?> _clientProvider;
    // 属性统计可能在窗口关闭后仍等待一次远程读取；同一客户端不能并发操作。
    // 浏览、传输的 SftpFileService 实例各自持有独立的门闩。
    private readonly SemaphoreSlim _clientGate = new(1, 1);

    public SftpFileService(Func<ISftpClient?> clientProvider)
    {
        _clientProvider = clientProvider;
    }

    public bool IsConnected => _clientProvider()?.IsConnected == true;

    public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path) =>
        ExecuteAsync(client =>
        {
            var items = client.ListDirectory(path)
                .Where(entry => entry.Name is not "." and not "..")
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToRemoteFileItem)
                .ToList();

            // 非根目录合成一个指向父目录的条目，它不来自远程列表，因此排在过滤与排序之后。
            if (path != "/")
            {
                items.Insert(0, new RemoteFileItem
                {
                    Name = "..",
                    IsDirectory = true,
                    FullPath = RemotePath.Parent(path),
                    TypeLabel = "目录",
                    SizeBytes = -1,
                    SizeLabel = "—",
                    ModifiedLabel = string.Empty
                });
            }

            return (IReadOnlyList<RemoteFileItem>)items;
        });

    public Task<long> GetDirectorySizeAsync(string path, CancellationToken cancellationToken) =>
        ExecuteAsync(client =>
        {
            var target = path == "/" ? path : path.TrimEnd('/');
            if (!target.StartsWith('/') || (target != "/" &&
                target[1..].Split('/').Any(segment => !SftpPathValidator.TryValidateRemoteName(segment, out _))))
                throw new IOException("远程目录路径无效。");

            cancellationToken.ThrowIfCancellationRequested();
            if (target != "/")
            {
                // 列表里的选中项可能已变成链接；不要统计链接目标。
                var name = target[(target.LastIndexOf('/') + 1)..];
                var selected = client.ListDirectory(RemotePath.Parent(target))
                    .SingleOrDefault(entry => entry.Name == name);
                if (selected is not { IsDirectory: true, IsSymbolicLink: false })
                    throw new IOException("远程目录已不存在或类型已改变，请刷新后重试。");
            }

            long total = 0;
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in client.ListDirectory(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name is "." or ".." || entry.IsSymbolicLink) continue;
                    if (!SftpPathValidator.TryValidateRemoteName(entry.Name, out _))
                        throw new IOException("远程目录包含无效的条目名称，无法完整计算大小。");
                    if (entry.IsDirectory)
                        pending.Push(RemotePath.Combine(directory, entry.Name));
                    else
                    {
                        if (entry.Length < 0) throw new IOException("服务器未提供文件大小，无法完整计算目录大小。");
                        total = checked(total + entry.Length);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return total;
        }, cancellationToken);

    public Task CreateDirectoryAsync(string path) =>
        ExecuteAsync(client => { client.CreateDirectory(path); return Task.CompletedTask; });

    public Task<bool> ExistsAsync(string path) =>
        ExecuteAsync(client => client.Exists(path));

    public Task<bool> IsDirectoryAsync(string path) =>
        ExecuteAsync(client => client.IsDirectory(path));

    public Task UploadAsync(
        Stream input,
        string remotePath,
        CancellationToken cancellationToken) =>
        ExecuteAsync(client => client.UploadAsync(input, remotePath, cancellationToken), cancellationToken);

    public Task DownloadAsync(
        string remotePath,
        Stream output,
        CancellationToken cancellationToken) =>
        ExecuteAsync(client => client.DownloadAsync(remotePath, output, cancellationToken), cancellationToken);

    public Task RenameAsync(string sourcePath, string destinationPath) =>
        ExecuteAsync(client => client.RenameAsync(sourcePath, destinationPath, CancellationToken.None));

    public Task DeleteAsync(RemoteFileItem item) => ExecuteAsync(async client =>
    {
        var path = SftpPathValidator.ValidateRemoteDeletePath(item.FullPath);
        if (!SftpPathValidator.TryValidateRemoteName(item.Name, out _) || path.Split('/').Last() != item.Name)
            throw new IOException("不能删除根目录、父目录或无效的远程路径。");

        // 重新读取父目录中的元数据，避免确认期间条目已变成符号链接。
        var selected = client.ListDirectory(RemotePath.Parent(path))
            .SingleOrDefault(entry => entry.Name == item.Name)
            ?? throw new IOException("待删除的远程项目已不存在。");
        if (selected.IsDirectory && !selected.IsSymbolicLink && (!item.IsDirectory || item.IsSymbolicLink))
            throw new IOException("远程项目类型已改变，请刷新目录后重试。");
        if (selected.IsDirectory && !selected.IsSymbolicLink &&
            await client.TryDeleteDirectoryRecursivelyAsync(path).ConfigureAwait(false))
            return;

        var pending = new Stack<(string Path, bool IsDirectory, bool Expanded)>();
        pending.Push((path, selected.IsDirectory && !selected.IsSymbolicLink, false));
        while (pending.TryPop(out var current))
        {
            if (!current.IsDirectory)
            {
                client.DeleteFile(current.Path);
                continue;
            }
            if (current.Expanded)
            {
                client.DeleteDirectory(current.Path);
                continue;
            }

            var children = client.ListDirectory(current.Path)
                .Where(entry => entry.Name is not "." and not "..")
                .ToList();
            foreach (var child in children)
                if (!SftpPathValidator.TryValidateRemoteName(child.Name, out _))
                    throw new IOException("远程目录包含无效的条目名称，已停止删除。");

            // 后序遍历：先移除内容，最后移除空目录；不信任远程返回的 FullPath。
            pending.Push((current.Path, true, true));
            foreach (var child in children.AsEnumerable().Reverse())
                pending.Push((RemotePath.Combine(current.Path, child.Name),
                    child.IsDirectory && !child.IsSymbolicLink, false));
        }
    });

    private static RemoteFileItem ToRemoteFileItem(RemoteDirectoryEntry entry)
    {
        var modifiedAt = entry.LastWriteTime.ToLocalTime();
        return new RemoteFileItem
        {
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            IsSymbolicLink = entry.IsSymbolicLink,
            FullPath = entry.FullPath,
            TypeLabel = entry.IsDirectory ? "目录" : "文件",
            SizeBytes = entry.IsDirectory ? -1 : entry.Length,
            SizeLabel = entry.IsDirectory ? "—" : FormatSize(entry.Length),
            ModifiedAt = modifiedAt,
            ModifiedLabel = modifiedAt.ToString("yyyy-MM-dd HH:mm")
        };
    }

    internal static string FormatSize(long length) => length switch
    {
        < 1024 => $"{length} B",
        < 1024 * 1024 => $"{length / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{length / 1024d / 1024d:0.0} MB",
        _ => $"{length / 1024d / 1024d / 1024d:0.0} GB"
    };

    private async Task<T> ExecuteAsync<T>(Func<ISftpClient, T> operation, CancellationToken cancellationToken = default)
    {
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            return await Task.Run(() => operation(client), cancellationToken).ConfigureAwait(false);
        }
        finally { _clientGate.Release(); }
    }

    private async Task ExecuteAsync(Func<ISftpClient, Task> operation, CancellationToken cancellationToken = default)
    {
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            await Task.Run(() => operation(client), cancellationToken).ConfigureAwait(false);
        }
        finally { _clientGate.Release(); }
    }

    private ISftpClient GetConnectedClient()
    {
        var client = _clientProvider();
        return client?.IsConnected == true
            ? client
            : throw new InvalidOperationException("文件服务尚未连接。");
    }
}
