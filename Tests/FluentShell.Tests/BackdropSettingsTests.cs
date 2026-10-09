using System.Text.Json;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class BackdropSettingsTests
{
    [TestMethod]
    [DataRow("Mica")]
    [DataRow("Mica Alt")]
    [DataRow("亚克力")]
    [DataRow("Acrylic Thin")]
    public void Settings_backup_preserves_each_backdrop(string material)
    {
        var settings = new AppSettings { BackdropMaterial = material };
        var restored = SettingsBackup.Import(SettingsBackup.Export(settings));
        Assert.AreEqual(material, restored.BackdropMaterial);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("")]
    [DataRow(null)]
    public void Import_falls_back_to_Mica_for_invalid_backdrops(string? material)
    {
        var restored = SettingsBackup.Import(JsonSerializer.Serialize(new { BackdropMaterial = material }));
        Assert.AreEqual("Mica", restored.BackdropMaterial);
    }

    [TestMethod]
    [DataRow("Mica")]
    [DataRow("Mica Alt")]
    [DataRow("亚克力")]
    [DataRow("Acrylic Thin")]
    public async Task Settings_store_preserves_each_backdrop_after_reload(string material)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FluentShell-backdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await new SettingsStore(folder).SaveAsync(new AppSettings { BackdropMaterial = material });
            var restored = await new SettingsStore(folder).LoadAsync();
            Assert.AreEqual(material, restored.BackdropMaterial);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
