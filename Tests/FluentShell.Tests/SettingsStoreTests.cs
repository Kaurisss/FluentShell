using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
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
