namespace FluentShell.Services;

public static class SftpPathValidator
{
    public static bool TryResolveDownloadPath(
        string destinationDirectory,
        string remoteItemName,
        out string localPath,
        out string error)
    {
        localPath = string.Empty;
        if (!TryValidateLocalFileName(remoteItemName, out error)) return false;

        try
        {
            var normalizedDirectory = Path.GetFullPath(destinationDirectory);
            var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, remoteItemName));
            var directoryWithSeparator = EnsureTrailingSeparator(normalizedDirectory);
            if (!candidate.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                error = "下载文件名必须位于所选目录内。";
                return false;
            }

            localPath = candidate;
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "下载文件名或目标目录无效。";
            return false;
        }
    }

    public static bool TryValidateRemoteName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名称不能为空。";
            return false;
        }

        if (name is "." or ".." ||
            name.Contains('\0') ||
            name.Contains('/') ||
            name.Contains('\\') ||
            Path.IsPathRooted(name))
        {
            error = "名称必须是当前目录内的单个文件或文件夹名称。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>在创建目录、打开或清理文件前，检查落地路径及已有祖先，拒绝链接和目录联接。</summary>
    public static void EnsureSafeDownloadPath(string destinationDirectory, string localPath)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var candidate = Path.GetFullPath(localPath);
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("下载条目必须位于所选目录内。");

        // 包含文件本身以及所选目录的祖先，避免已有链接把写入导向别处。
        for (var current = candidate; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("下载目标包含符号链接或目录联接，请选择普通目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>上传目录内的相对路径，每段都必须是合法名称，不能归一化掉越界段。</summary>
    public static bool TryValidateUploadRelativePath(string path, out string error)
    {
        foreach (var segment in path.Split('/'))
            if (!TryValidateRemoteName(segment, out error)) return false;
        error = string.Empty;
        return true;
    }

    private static bool TryValidateLocalFileName(string name, out string error)
    {
        if (!TryValidateRemoteName(name, out error)) return false;

        if (name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsWindowsReservedName(name))
        {
            error = "下载文件名包含 Windows 不支持的字符或保留名称。";
            return false;
        }

        return true;
    }

    private static bool IsWindowsReservedName(string name)
    {
        var baseName = name.Split('.', 2)[0].TrimEnd(' ');
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.Length != 4) return false;
        var prefix = baseName[..3];
        return (prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase)) &&
               baseName[3] is >= '1' and <= '9';
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
