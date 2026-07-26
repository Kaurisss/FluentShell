using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>
/// 已保存服务器的内存列表语义：新增或就地更新、复制、移除。
/// 由 <see cref="ILocalStore"/> 的各适配器共用，使复制规则等领域逻辑只有一处实现。
/// 不负责持久化。
/// </summary>
public sealed class ServerProfileList
{
    private readonly List<ServerProfile> _profiles = [];

    public IReadOnlyList<ServerProfile> Items => _profiles;

    public void Replace(IEnumerable<ServerProfile> profiles)
    {
        _profiles.Clear();
        _profiles.AddRange(profiles);
    }

    public void AddOrUpdate(ServerProfile profile)
    {
        if (!_profiles.Contains(profile)) _profiles.Add(profile);
    }

    /// <summary>追加一份副本。不复制凭据、上次连接时间与标识。</summary>
    public ServerProfile AddCopyOf(ServerProfile source)
    {
        var copy = new ServerProfile
        {
            Name = source.Name + " 副本",
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Authentication = source.Authentication,
            PrivateKeyPath = source.PrivateKeyPath,
            Notes = source.Notes,
            HostFingerprint = source.HostFingerprint
        };
        _profiles.Add(copy);
        return copy;
    }

    public void Remove(ServerProfile profile) => _profiles.Remove(profile);

    public void Clear() => _profiles.Clear();
}
