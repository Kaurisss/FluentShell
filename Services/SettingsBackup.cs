using FluentShell.Models;
using System.Text.Json;

namespace FluentShell.Services;

public static class SettingsBackup
{
    public static string Export(AppSettings settings) => JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

    public static AppSettings Import(string json)
    {
        if (json.Length > 1_000_000) throw new InvalidDataException("设置文件过大。");
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("设置文件为空。");
        return Normalize(settings);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        settings.Theme = settings.Theme is "浅色" or "深色" ? settings.Theme : "系统";
        settings.BackdropMaterial = settings.BackdropMaterial is "Mica Alt" or "亚克力" or "Acrylic Thin"
            ? settings.BackdropMaterial : "Mica";
        settings.TerminalFontSize = double.IsFinite(settings.TerminalFontSize) ? Math.Clamp(settings.TerminalFontSize, 11, 24) : 14;
        settings.TerminalColors = (settings.TerminalColors ?? new()).Normalize();
        settings.Preferences = (settings.Preferences ?? new()).Normalize();
        if (!settings.Preferences.HasUniqueShortcuts) settings.Preferences = settings.Preferences with
        { NewSessionKey = "T", CloseSessionKey = "W", NextSessionKey = "N", SearchTerminalKey = "F", ToggleFilesKey = "E" };
        if (string.IsNullOrWhiteSpace(settings.DownloadDirectory) || !Path.IsPathFullyQualified(settings.DownloadDirectory))
        { settings.DownloadDirectory = AppSettings.DefaultDownloadDirectory; settings.HasCustomDownloadDirectory = false; }
        return settings;
    }
}
