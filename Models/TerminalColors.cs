using System.Text.RegularExpressions;

namespace FluentShell.Models;

/// <summary>Optional overrides; missing values use the terminal's built-in palette.</summary>
public sealed class TerminalColors
{
    public Dictionary<string, string> Light { get; set; } = [];
    public Dictionary<string, string> Dark { get; set; } = [];

    public static IReadOnlyDictionary<string, string> Fields { get; } = new Dictionary<string, string>
    {
        ["background"] = "背景", ["foreground"] = "文字", ["cursor"] = "光标",
        ["cursorAccent"] = "光标内文字", ["selectionBackground"] = "选区背景",
        ["black"] = "黑色", ["red"] = "红色", ["green"] = "绿色", ["yellow"] = "黄色",
        ["blue"] = "蓝色", ["magenta"] = "紫色", ["cyan"] = "青色", ["white"] = "白色",
        ["brightBlack"] = "亮黑色", ["brightRed"] = "亮红色", ["brightGreen"] = "亮绿色",
        ["brightYellow"] = "亮黄色", ["brightBlue"] = "亮蓝色", ["brightMagenta"] = "亮紫色",
        ["brightCyan"] = "亮青色", ["brightWhite"] = "亮白色"
    };

    public static bool IsColor(string value) => Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");

    // Same order as Fields; the dark selection swatch shows the default translucent
    // selection composited over #363636. Missing overrides retain that transparency.
    private static readonly string[] LightDefaults = ["#FEFEFE", "#17212B", "#17212B", "#FEFEFE", "#BDD8F5",
        "#24292F", "#B42318", "#216E39", "#805500", "#0550AE", "#7446C8", "#076678", "#59636E",
        "#57606A", "#CF222E", "#187632", "#8C5D00", "#0969DA", "#A03AAF", "#076F7C", "#616B75"];
    private static readonly string[] DarkDefaults = ["#363636", "#E6EDF3", "#E6EDF3", "#363636", "#4D5F7C",
        "#24292F", "#FF7B72", "#7EE787", "#E3B341", "#79C0FF", "#D2A8FF", "#76E3EA", "#D0D7DE",
        "#9DA7B3", "#FFA198", "#AFF5B4", "#F8E3A1", "#A5D6FF", "#E2C5FF", "#B3F0FF", "#FFFFFF"];

    public static string DefaultColor(string key, bool dark) =>
        (dark ? DarkDefaults : LightDefaults)[Array.IndexOf(Fields.Keys.ToArray(), key)];

    public TerminalColors Normalize() => new() { Light = Clean(Light), Dark = Clean(Dark) };

    private static Dictionary<string, string> Clean(Dictionary<string, string>? values) =>
        (values ?? []).Where(pair => Fields.ContainsKey(pair.Key) && pair.Value is not null && IsColor(pair.Value.Trim()))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim().ToUpperInvariant());
}
