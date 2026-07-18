using NovaShell.Models;
using System.Text.Json;

namespace NovaShell.Services;

public sealed class SettingsStore
{
    private readonly string _folder = AppDataPaths.Folder;
    private string FilePath => Path.Combine(_folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(_folder);
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, settings, Options);
        File.Move(temp, FilePath, true);
    }
}
