using FluentShell.Models;
using FluentShell.Services;
using System.Collections.ObjectModel;

namespace FluentShell.Core;

public sealed class ServerCatalog
{
    private readonly ServerProfileStore _store;
    private readonly CredentialService _credentialService;
    private readonly ObservableCollection<ServerProfile> _profiles = [];

    public ServerCatalog(ServerProfileStore store, CredentialService credentialService)
    {
        _store = store;
        _credentialService = credentialService;
    }

    public ObservableCollection<ServerProfile> Profiles => _profiles;
    public string DataFolder => _store.GetDataFolder();

    public async Task LoadAsync()
    {
        _profiles.Clear();
        foreach (var profile in await _store.LoadAsync()) _profiles.Add(profile);
    }

    public IReadOnlyList<ServerProfile> Query(string? filter, ServerSortOrder sortOrder) =>
        ServerProfileQuery.Apply(_profiles, filter, sortOrder);

    public async Task AddOrUpdateAsync(ServerProfile profile)
    {
        if (!_profiles.Contains(profile)) _profiles.Add(profile);
        await SaveAsync();
    }

    public async Task<ServerProfile> CopyAsync(ServerProfile source)
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
            HostFingerprint = source.HostFingerprint,
            ShowHiddenFiles = source.ShowHiddenFiles
        };
        _profiles.Add(copy);
        await SaveAsync();
        return copy;
    }

    public async Task DeleteAsync(ServerProfile profile)
    {
        _credentialService.Remove(profile);
        _profiles.Remove(profile);
        await SaveAsync();
    }

    public Task SaveAsync() => _store.SaveAsync(_profiles);

    public void Clear()
    {
        _store.ClearLocalData();
        _credentialService.ClearAll();
        _profiles.Clear();
    }
}