using FluentShell.Models;

namespace FluentShell.Services;

/// <summary>
/// 磁盘加 Windows 凭据保管库的本机存储适配器。
/// </summary>
public sealed class LocalStore : ILocalStore
{
    private readonly JsonDocumentStore<List<ServerProfile>> _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly CredentialService _credentials = new();
    private readonly ServerProfileList _profiles = new();
    private readonly string _folder;

    public LocalStore(string? folder = null)
    {
        _folder = folder ?? AppDataPaths.Folder;
        _profileStore = new JsonDocumentStore<List<ServerProfile>>(_folder, "servers.json", static () => []);
        _settingsStore = new SettingsStore(_folder);
    }

    public IReadOnlyList<ServerProfile> Profiles => _profiles.Items;
    public string DataFolder => _folder;

    public async Task<AppSettings> LoadAsync()
    {
        _profiles.Replace(await _profileStore.LoadAsync());
        return await _settingsStore.LoadAsync();
    }

    public Task SaveSettingsAsync(AppSettings settings) => _settingsStore.SaveAsync(settings);

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
        _credentials.Remove(profile);
        _profiles.Remove(profile);
        return SaveProfilesAsync();
    }

    public Task SaveProfilesAsync() => _profileStore.SaveAsync([.. _profiles.Items]);

    public string? TryGetSecret(ServerProfile profile) => _credentials.TryGet(profile);
    public void SaveSecret(ServerProfile profile, string secret) => _credentials.Save(profile, secret);
    public void RemoveSecret(ServerProfile profile) => _credentials.Remove(profile);
    public void RemoveSecret(Guid profileId, string username) => _credentials.Remove(profileId, username);

    public void ClearAll()
    {
        // 整个数据目录一并删除，已保存服务器与设置文件同时清空。
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        _credentials.ClearAll();
        _profiles.Clear();
    }
}
