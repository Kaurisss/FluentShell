using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>单级跳板配置校验。失效引用必须报错，避免意外改为直连。</summary>
public static class JumpHostResolver
{
    public static bool IsEligible(ServerProfile target, ServerProfile candidate) =>
        target.Id != candidate.Id && candidate.JumpProfileId is null;

    public static ServerProfile? Resolve(ServerProfile target, IEnumerable<ServerProfile> profiles)
    {
        if (target.JumpProfileId is not Guid jumpId) return null;

        var jump = profiles.FirstOrDefault(profile => profile.Id == jumpId);
        if (jump is null)
            throw new InvalidOperationException("所选跳板服务器已不存在，请编辑服务器配置。");
        if (!IsEligible(target, jump))
            throw new InvalidOperationException("跳板服务器必须是另一台直连的已保存服务器。");

        return jump;
    }
}
