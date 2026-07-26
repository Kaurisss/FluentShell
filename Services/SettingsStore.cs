using FluentShell.Models;
using System.Text.Json;

namespace FluentShell.Services;

public sealed class SettingsStore
{
    private readonly string _folder;
    private string FilePath => Path.Combine(_folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SettingsStore(string? folder = null)
    {
        _folder = folder ?? AppDataPaths.Folder;
    }

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            AppSettings settings;
            await using (var stream = File.OpenRead(FilePath))
            {
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options) ?? new AppSettings();
            }

            if (MigrateLegacyDownloadDirectory(settings))
                await SaveAsync(settings);
            return settings;
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

    private static bool MigrateLegacyDownloadDirectory(AppSettings settings)
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (settings.HasCustomDownloadDirectory ||
            !string.Equals(settings.DownloadDirectory, userProfileDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        settings.DownloadDirectory = AppSettings.DefaultDownloadDirectory;
        return true;
    }
}
