using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class TextEditorPreferencesTests
{
    [TestMethod]
    [DataRow(2, 2)]
    [DataRow(4, 4)]
    [DataRow(8, 8)]
    [DataRow(0, 4)]
    [DataRow(3, 4)]
    [DataRow(100, 4)]
    public void Normalize_accepts_only_supported_indentation_widths(int width, int expected)
    {
        Assert.AreEqual(expected, new TextEditorPreferences { IndentWidth = width }.Normalize().IndentWidth);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"TextEditor\":null}")]
    public void Legacy_and_null_editor_settings_keep_readonly_defaults(string json)
    {
        var restored = SettingsBackup.Import(json);
        Assert.AreEqual(new TextEditorPreferences(), restored.TextEditor);
        Assert.IsTrue(restored.TextEditor.ReadOnly);
    }

    [TestMethod]
    public async Task All_editor_options_survive_backup_and_store_reload()
    {
        var preferences = new TextEditorPreferences
        {
            ReadOnly = false, WordWrap = true, ShowLineNumbers = false,
            ShowWhitespace = true, IndentWidth = 8, UseTabs = true
        };
        var settings = new AppSettings { TextEditor = preferences };
        Assert.AreEqual(preferences, SettingsBackup.Import(SettingsBackup.Export(settings)).TextEditor);
        var folder = Path.Combine(Path.GetTempPath(), "FluentShell-editor-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await new SettingsStore(folder).SaveAsync(settings);
            Assert.AreEqual(preferences, (await new SettingsStore(folder).LoadAsync()).TextEditor);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public async Task Concurrent_editor_and_appearance_updates_preserve_each_other()
    {
        var initial = new AppSettings { BackdropMaterial = "Mica Alt", Preferences = new() { Scrollback = 4500 } };
        var store = new InMemoryLocalStore(settings: initial);
        var shell = new ShellCoordinator(store, (profile, _, _) => new FakeShellSession(profile),
            _ => Task.FromResult<string?>(null), _ => Task.FromResult(false));
        await shell.LoadAsync();
        var preferences = new TextEditorPreferences { ReadOnly = false, WordWrap = true, IndentWidth = 2, UseTabs = true };
        await Task.WhenAll(
            shell.UpdateSettingsAsync(new(TextEditor: preferences)),
            shell.UpdateSettingsAsync(new(Theme: "深色")),
            shell.UpdateSettingsAsync(new(TerminalFontSize: 18)));
        Assert.AreEqual(preferences, store.PersistedSettings.TextEditor);
        Assert.AreEqual("深色", store.PersistedSettings.Theme);
        Assert.AreEqual("Mica Alt", store.PersistedSettings.BackdropMaterial);
        Assert.AreEqual(18, store.PersistedSettings.TerminalFontSize);
        Assert.AreEqual(4500, store.PersistedSettings.Preferences.Scrollback);
    }
}
