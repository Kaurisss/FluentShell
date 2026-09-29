using FluentShell.Models;
using System.Text.Json;

namespace FluentShell.Tests;

[TestClass]
public sealed class TerminalColorsTests
{
    [TestMethod]
    public void Settings_round_trip_and_old_settings_keep_defaults()
    {
        var settings = new AppSettings { TerminalColors = new() { Light = new() { ["red"] = "#123456" }, Dark = new() { ["background"] = "#234567" } } };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.AreEqual("#123456", saved.TerminalColors.Light["red"]);
        Assert.AreEqual("#234567", saved.TerminalColors.Dark["background"]);
        Assert.IsEmpty(JsonSerializer.Deserialize<AppSettings>("{}")!.TerminalColors.Light);
    }

    [TestMethod]
    public void Normalization_filters_invalid_unknown_and_null_values_without_mutation()
    {
        var colors = new TerminalColors { Light = new() { ["red"] = " #abcdef ", ["background"] = "url(test)", ["unknown"] = "#123456", ["blue"] = null! }, Dark = null! };
        var result = colors.Normalize();
        Assert.HasCount(1, result.Light);
        Assert.AreEqual("#ABCDEF", result.Light["red"]);
        Assert.IsEmpty(result.Dark);
        Assert.AreEqual(" #abcdef ", colors.Light["red"]);
    }
}
