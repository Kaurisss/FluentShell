using FluentShell.Models;

namespace FluentShell.Services;

public sealed record DuplicateCheckResult(
    bool IsDuplicate,
    string? ExistingProfileName);

/// <summary>
/// 集中定义已保存服务器的身份比较规则，供配置编辑界面和后续入口复用。
/// </summary>
public sealed class ServerProfileValidator
{
    public DuplicateCheckResult CheckForDuplicate(
        IEnumerable<ServerProfile>? existingProfiles,
        ServerProfile? candidate,
        Guid? editingProfileId = null)
    {
        if (existingProfiles is null || candidate is null)
            return new DuplicateCheckResult(false, null);

        var host = candidate.Host?.Trim();
        var username = candidate.Username?.Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username))
            return new DuplicateCheckResult(false, null);

        foreach (var profile in existingProfiles)
        {
            if (profile is null || (editingProfileId.HasValue && profile.Id == editingProfileId.Value))
                continue;

            if (profile.Port != candidate.Port ||
                !string.Equals(profile.Host?.Trim(), host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(profile.Username?.Trim(), username, StringComparison.Ordinal))
            {
                continue;
            }

            return new DuplicateCheckResult(true, profile.Name);
        }

        return new DuplicateCheckResult(false, null);
    }
}
