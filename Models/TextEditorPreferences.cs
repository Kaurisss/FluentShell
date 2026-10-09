namespace FluentShell.Models;

public sealed record TextEditorPreferences
{
    public bool ReadOnly { get; init; } = true;
    public bool WordWrap { get; init; }
    public bool ShowLineNumbers { get; init; } = true;
    public bool ShowWhitespace { get; init; }
    public int IndentWidth { get; init; } = 4;
    public bool UseTabs { get; init; }

    public TextEditorPreferences Normalize() => this with { IndentWidth = IndentWidth is 2 or 8 ? IndentWidth : 4 };
}
