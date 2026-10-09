using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class UserPreferencesTests
{
    [TestMethod]
    public void Import_normalizes_ranges_and_preserves_safe_defaults()
    {
        var settings = SettingsBackup.Import("""{"Preferences":{"Scrollback":-1,"MaxTransfers":999,"ConnectionTimeoutSeconds":0,"CursorStyle":"invalid","FontFamily":"invalid","NewSessionKey":"W"},"TerminalColors":null} """);
        Assert.AreEqual(1000, settings.Preferences.Scrollback);
        Assert.AreEqual(8, settings.Preferences.MaxTransfers);
        Assert.AreEqual(3, settings.Preferences.ConnectionTimeoutSeconds);
        Assert.AreEqual("bar", settings.Preferences.CursorStyle);
        Assert.IsTrue(settings.Preferences.HasUniqueShortcuts);
        Assert.IsTrue(settings.Preferences.ConfirmMultilinePaste);
        Assert.IsTrue(settings.Preferences.TerminalBackdrop);
        Assert.IsEmpty(settings.TerminalColors.Light);
    }

    [TestMethod]
    public void Settings_backup_roundtrips_preferences_and_colors()
    {
        var original = new AppSettings { Preferences = new() { FontFamily = "Consolas", ReconnectAttempts = 2, ShowHiddenFiles = false, ConflictPolicy = "skip", TerminalBackdrop = false }, TerminalColors = new() { Light = new() { ["red"] = "#123456" } } };
        var restored = SettingsBackup.Import(SettingsBackup.Export(original));
        Assert.AreEqual(original.Preferences, restored.Preferences);
        Assert.AreEqual("#123456", restored.TerminalColors.Light["red"]);
    }

    [TestMethod]
    public async Task Transfer_limit_waits_cancels_and_changes_without_interrupting_active_leases()
    {
        var limiter = new TransferLimiter();
        limiter.SetLimit(1);
        using var first = await limiter.AcquireAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var blocked = limiter.AcquireAsync(cancelled.Token);
        Assert.IsFalse(blocked.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await blocked);
        var second = limiter.AcquireAsync(CancellationToken.None);
        limiter.SetLimit(2);
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(2));
        limiter.SetLimit(1);
        var third = limiter.AcquireAsync(CancellationToken.None);
        acquired.Dispose();
        Assert.IsFalse(third.IsCompleted);
        first.Dispose();
        using var final = await third.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Conflict_default_applies_to_a_batch_and_reset_restores_prompting()
    {
        var resolver = new FileConflictResolver();
        var prompts = 0;
        Task<(bool, bool, bool)> Prompt(string _) { prompts++; return Task.FromResult((true, false, false)); }
        resolver.Reset("skip");
        Assert.IsFalse(await resolver.ResolveConflictAsync("file", Prompt) ?? true);
        resolver.Reset("overwrite");
        Assert.IsTrue(await resolver.ResolveConflictAsync("file", Prompt) ?? false);
        Assert.AreEqual(0, prompts);
        resolver.Reset();
        Assert.IsTrue(await resolver.ResolveConflictAsync("file", Prompt) ?? false);
        Assert.AreEqual(1, prompts);
    }

    [TestMethod]
    public void Ssh_timeout_is_applied_without_contacting_a_server()
    {
        var info = SshConnectionService.CreateConnectionInfo(new ServerProfile { Username = "fixture" }, "fixture", [], "localhost", 22, 37);
        Assert.AreEqual(TimeSpan.FromSeconds(37), info.Timeout);
    }
}
