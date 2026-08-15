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