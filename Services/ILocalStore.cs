using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>
/// 本机存储接缝：已保存服务器、应用设置与凭据。
/// 生产适配器为磁盘加 Windows 凭据保管库，测试适配器为内存实现。
/// </summary>
public interface ILocalStore
{
    /// <summary>已保存服务器，顺序即写入顺序。<see cref="LoadAsync"/> 之前为空。</summary>
    IReadOnlyList<ServerProfile> Profiles { get; }

    /// <summary>展示给用户的数据位置；内存适配器返回占位串。</summary>
    string DataFolder { get; }

    /// <summary>载入已保存服务器与应用设置，返回设置。已保存服务器经 <see cref="Profiles"/> 读取。</summary>
    Task<AppSettings> LoadAsync();

    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>新增或就地更新一台已保存服务器，随后持久化整份列表。</summary>
    Task AddOrUpdateProfileAsync(ServerProfile profile);

    /// <summary>以源服务器为模板追加一份副本；不复制凭据。</summary>
    Task CopyProfileAsync(ServerProfile profile);

    /// <summary>删除一台已保存服务器及其凭据。</summary>
    Task DeleteProfileAsync(ServerProfile profile);

    /// <summary>持久化当前列表，用于就地修改了 <see cref="ServerProfile"/> 之后。</summary>
    Task SaveProfilesAsync();

    string? TryGetSecret(ServerProfile profile);
    void SaveSecret(ServerProfile profile, string secret);
    void RemoveSecret(ServerProfile profile);
    void RemoveSecret(Guid profileId, string username);

    /// <summary>清空本机全部数据：已保存服务器、设置文件与凭据。</summary>
    void ClearAll();
}
