using System.Text.Json;
using NovaShell.Models;

namespace NovaShell.Services;

public sealed class ServerProfileStore
{
    private readonly string _folder = AppDataPaths.Folder;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ServerProfileStore()
    {
        _filePath = Path.Combine(_folder, "servers.json");
    }

    public async Task<List<ServerProfile>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<ServerProfile>>(stream, _options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<ServerProfile> profiles)
    {
        Directory.CreateDirectory(_folder);
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, profiles.ToList(), _options);
        }
        File.Move(tempPath, _filePath, true);
    }

    public string GetDataFolder() => _folder;

    public void ClearLocalData()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
