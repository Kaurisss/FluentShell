using Microsoft.VisualBasic.FileIO;

namespace FluentShell.Services;

/// <summary>本地面板的文件操作；名称只能指向当前目录的直接子项。</summary>
public sealed class LocalFileService
{
    private readonly Action<string, bool> _recycle;

    public LocalFileService(Action<string, bool>? recycle = null) => _recycle = recycle ?? Recycle;

    public Task CreateDirectoryAsync(string directory, string name) => Task.Run(() =>
    {
        var path = ResolveChild(directory, name);
        if (Path.Exists(path)) throw new IOException("同名文件或文件夹已存在。");
        Directory.CreateDirectory(path);
    });

    public Task RenameAsync(string directory, string name, string newName) => Task.Run(() =>
    {
        var source = ResolveChild(directory, name);
        var target = ResolveChild(directory, newName);
        if (string.Equals(source, target, StringComparison.Ordinal)) return;
        if ((File.GetAttributes(source) & FileAttributes.Directory) != 0) Directory.Move(source, target);
        else File.Move(source, target);
    });

    public Task RecycleAsync(string directory, IReadOnlyList<string> names) => Task.Run(() =>
    {
        // Validate the entire selection before changing anything.
        var items = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var path = ResolveChild(directory, name);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请使用资源管理器删除符号链接或目录联接。");
            return (Path: path, IsDirectory: (attributes & FileAttributes.Directory) != 0);
        }).ToArray();
        foreach (var item in items) _recycle(item.Path, item.IsDirectory);
    });

    public Task<long?> GetDirectorySizeAsync(string path, CancellationToken token) => Task.Run<long?>(() =>
    {
        token.ThrowIfCancellationRequested();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        var pending = new Stack<string>();
        pending.Push(path);
        long size = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((info.Attributes & FileAttributes.Directory) != 0) pending.Push(info.FullName);
                else size = checked(size + ((FileInfo)info).Length);
            }
        }
        return size;
    }, token);

    private static string ResolveChild(string directory, string name)
    {
        if (!SftpPathValidator.TryResolveDownloadPath(directory, name, out var path, out _))
            throw new IOException("名称无效。请输入当前目录内的单个名称，避免使用 Windows 不支持的字符或保留名称。");
        return path;
    }

    private static void Recycle(string path, bool directory)
    {
        if (directory)
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
}
