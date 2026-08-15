using System.Text.Json;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public async Task LoadAsync_returns_defaults_when_the_migration_write_fails()
    {
        var settingsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsFolder);
        var settingsPath = Path.Combine(settingsFolder, "settings.json");
        try
        {
            var legacyDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""
                {
                  "Theme": "深色",
                  "DownloadDirectory": {{JsonSerializer.Serialize(legacyDirectory)}}
                }
                """);
            File.SetAttributes(settingsPath, FileAttributes.ReadOnly);

            var settings = await new SettingsStore(settingsFolder).LoadAsync();

            Assert.AreEqual(
                new AppSettings().Theme,
                settings.Theme,
                "迁移写入失败时必须回退到默认设置，不得让载入抛出——主窗口的载入是即发即弃的。");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.SetAttributes(settingsPath, FileAttributes.Normal);
            Directory.Delete(settingsFolder, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAsync_migrates_and_persists_legacy_user_profile_download_directory()
    {
        var settingsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsFolder);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(settingsFolder, "settings.json"),
                """
                {
                  "DownloadDirectory": "C:\\Users\\Logic"
                }
                """);

            var settings = await new SettingsStore(settingsFolder).LoadAsync();

            Assert.AreEqual(Path.Combine("C:\\Users\\Logic", "Downloads"), settings.DownloadDirectory);
            var persistedSettings = await File.ReadAllTextAsync(Path.Combine(settingsFolder, "settings.json"));
            StringAssert.Contains(persistedSettings, "Downloads");
        }
        finally
        {
            Directory.Delete(settingsFolder, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAsync_preserves_explicit_user_profile_download_directory()
    {
        var settingsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsFolder);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(settingsFolder, "settings.json"),
                """
                {
                  "DownloadDirectory": "C:\\Users\\Logic",
                  "HasCustomDownloadDirectory": true
                }
                """);

            var settings = await new SettingsStore(settingsFolder).LoadAsync();

            Assert.AreEqual("C:\\Users\\Logic", settings.DownloadDirectory);
        }
        finally
        {
            Directory.Delete(settingsFolder, recursive: true);
        }
    }
}
