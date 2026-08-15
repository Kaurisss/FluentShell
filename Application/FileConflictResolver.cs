namespace FluentShell.Core;

/// <summary>
/// 管理批量文件传输时的冲突解决策略。
/// 用户可以选择"应用到全部"来避免重复确认每个冲突文件。
/// </summary>
public sealed class FileConflictResolver
{
    private FileConflictPolicy? _policy;

    /// <summary>
    /// 询问用户如何处理文件冲突。如果已有全局策略则直接应用。
    /// </summary>
    /// <param name="fileName">冲突的文件名</param>
    /// <param name="promptUser">当需要用户决策时调用的回调</param>
    /// <returns>true 表示覆盖，false 表示跳过，null 表示取消全部操作</returns>
    public async Task<bool?> ResolveConflictAsync(
        string fileName,
        Func<string, Task<(bool overwrite, bool applyToAll, bool cancelled)>> promptUser)
    {
        // 已有全局策略，直接应用
        if (_policy is not null)
        {
            return _policy switch
            {
                FileConflictPolicy.OverwriteAll => true,
                FileConflictPolicy.SkipAll => false,
                _ => null
            };
        }

        // 需要询问用户
        var (overwrite, applyToAll, cancelled) = await promptUser(fileName);

        if (cancelled)
        {
            return null;
        }

        // 用户选择了"应用到全部"，记录策略
        if (applyToAll)
        {
            _policy = overwrite ? FileConflictPolicy.OverwriteAll : FileConflictPolicy.SkipAll;
        }

        return overwrite;
    }

    /// <summary>
    /// 重置冲突解决策略，用于新的批量操作。
    /// </summary>
    public void Reset()
    {
        _policy = null;
    }

    private enum FileConflictPolicy
    {
        OverwriteAll,
        SkipAll
    }
}
