using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

/// <summary>
/// 本机存储接缝的内存适配器。不触碰磁盘与 Windows 凭据保管库。
/// 已保存服务器的列表语义与生产适配器共用 <see cref="ServerProfileList"/>，只有持久化不同。
/// <see cref="PersistedProfiles"/> 记录最近一次持久化写入的内容，用于断言"写回去的是什么"。
/// </summary>
public sealed class InMemoryLocalStore : ILocalStore
{
    private readonly ServerProfileList _profiles = new();
    private readonly Dictionary<(Guid ProfileId, string Username), string> _secrets = [];
    private AppSettings _settings;

    public InMemoryLocalStore(
        IEnumerable<ServerProfile>? profiles = null,
        AppSettings? settings = null)
    {
        if (profiles is not null) _profiles.Replace(profiles);
        _settings = settings ?? new AppSettings();
        PersistedProfiles = [.. _profiles.Items];
    }

    public IReadOnlyList<ServerProfile> Profiles => _profiles.Items;
    public string DataFolder => "(in-memory)";

    /// <summary>最近一次 <see cref="SaveProfilesAsync"/> 写入的快照。</summary>
    public IReadOnlyList<ServerProfile> PersistedProfiles { get; private set; }

    /// <summary>最近一次 <see cref="SaveSettingsAsync"/> 写入的设置。</summary>
    public AppSettings PersistedSettings => _settings;

    public int SaveProfilesCallCount { get; private set; }

    public Task<AppSettings> LoadAsync() => Task.FromResult(_settings);

    public Task SaveSettingsAsync(AppSettings settings)
    {
        _settings = settings;
        return Task.CompletedTask;
    }

    public Task AddOrUpdateProfileAsync(ServerProfile profile)
    {
        _profiles.AddOrUpdate(profile);
        return SaveProfilesAsync();
    }

    public Task CopyProfileAsync(ServerProfile source)
    {
        _profiles.AddCopyOf(source);
        return SaveProfilesAsync();
    }

    public Task DeleteProfileAsync(ServerProfile profile)
    {
        RemoveSecret(profile);
        _profiles.Remove(profile);
        return SaveProfilesAsync();
    }

    public Task SaveProfilesAsync()
    {
        SaveProfilesCallCount++;
        PersistedProfiles = [.. _profiles.Items];
        return Task.CompletedTask;
    }

    public string? TryGetSecret(ServerProfile profile) =>
        _secrets.TryGetValue((profile.Id, profile.Username), out var secret) ? secret : null;

    public void SaveSecret(ServerProfile profile, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        _secrets[(profile.Id, profile.Username)] = secret;
    }

    public void RemoveSecret(ServerProfile profile) => RemoveSecret(profile.Id, profile.Username);

    public void RemoveSecret(Guid profileId, string username) => _secrets.Remove((profileId, username));

    public void ClearAll()
    {
        // 与生产适配器一致：删除数据目录会同时清空已保存服务器与设置文件。
        _secrets.Clear();
        _profiles.Clear();
        _settings = new AppSettings();
        PersistedProfiles = [];
    }
}
