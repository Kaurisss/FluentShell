namespace FluentShell.Services;

/// <summary>把已确认的目录删除交给服务器，路径始终是一个 shell 参数。</summary>
internal static class SshDirectoryDeletion
{
    internal static string BuildCommand(string path)
    {
        var target = SftpPathValidator.ValidateRemoteDeletePath(path);
        var quotedPath = "'" + target.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return "rm -r -- " + quotedPath;
    }

    internal static async Task DeleteAsync(string path, Func<string, Task<(int? ExitStatus, string Error)>> execute)
    {
        var result = await execute(BuildCommand(path)).ConfigureAwait(false);
        if (result.ExitStatus == 0) return;
        var error = result.Error.Trim();
        throw new IOException(error.Length > 0
            ? error
            : $"远程目录删除未完成（退出码：{result.ExitStatus?.ToString() ?? "未知"}）。");
    }
}
