using FluentShell.Models;

namespace FluentShell.Services;

public sealed class SettingsStore
{
    private readonly JsonDocumentStore<AppSettings> _store;

    public SettingsStore(string? folder = null)
    {
        _store = new JsonDocumentStore<AppSettings>(
            folder ?? AppDataPaths.Folder,
            "settings.json",
            static () => new AppSettings());
    }

    public async Task<AppSettings> LoadAsync()
    {
        var settings = await _store.LoadAsync();
        try
        {
            if (MigrateLegacyDownloadDirectory(settings)) await SaveAsync(settings);
        }
        catch
        {
            // 迁移写入失败不得让载入抛出：MainWindow 的载入是即发即弃的，
            // 异常会被静默吞掉并留下空白外壳。与迁移前行为一致，回退到默认设置。
            return new AppSettings();
        }

        return settings;
    }

    public Task SaveAsync(AppSettings settings) => _store.SaveAsync(settings);

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
