namespace FluentShell.Models;

/// <summary>Validated, immutable application defaults. No credentials or server identities.</summary>
public sealed record UserPreferences
{
    public string FontFamily { get; init; } = "Cascadia Mono";
    public string CursorStyle { get; init; } = "bar";
    public bool CursorBlink { get; init; } = true;
    public int Scrollback { get; init; } = 20000;
    public bool CopyOnSelect { get; init; }
    public bool RightClickPaste { get; init; }
    public bool ConfirmMultilinePaste { get; init; } = true;
    public string TerminalTheme { get; init; } = "system";
    public bool TerminalBackdrop { get; init; } = true;
    public bool SidebarOpen { get; init; } = true;
    public int ConnectionTimeoutSeconds { get; init; } = 12;
    public int KeepAliveSeconds { get; init; } = 30;
    public int ReconnectAttempts { get; init; }
    public int ReconnectDelaySeconds { get; init; } = 5;
    public bool ShowHiddenFiles { get; init; } = true;
    public string ConflictPolicy { get; init; } = "ask";
    public int MaxTransfers { get; init; } = 3;
    public bool NotifyTransferComplete { get; init; } = true;
    public bool UseDefaultDownloadDirectory { get; init; }
    public string NewSessionKey { get; init; } = "T";
    public string CloseSessionKey { get; init; } = "W";
    public string NextSessionKey { get; init; } = "N";
    public string SearchTerminalKey { get; init; } = "F";
    public string ToggleFilesKey { get; init; } = "E";

    public UserPreferences Normalize() => this with
    {
        FontFamily = FontFamily is "Cascadia Mono" or "Consolas" or "Courier New" ? FontFamily : "Cascadia Mono",
        CursorStyle = CursorStyle is "block" or "underline" ? CursorStyle : "bar",
        Scrollback = Math.Clamp(Scrollback, 1000, 100000),
        TerminalTheme = TerminalTheme is "light" or "dark" ? TerminalTheme : "system",
        ConnectionTimeoutSeconds = Math.Clamp(ConnectionTimeoutSeconds, 3, 120),
        KeepAliveSeconds = Math.Clamp(KeepAliveSeconds, 0, 300),
        ReconnectAttempts = Math.Clamp(ReconnectAttempts, 0, 10),
        ReconnectDelaySeconds = Math.Clamp(ReconnectDelaySeconds, 1, 60),
        ConflictPolicy = ConflictPolicy is "skip" or "overwrite" ? ConflictPolicy : "ask",
        MaxTransfers = Math.Clamp(MaxTransfers, 1, 8),
        NewSessionKey = Key(NewSessionKey, "T"), CloseSessionKey = Key(CloseSessionKey, "W"),
        NextSessionKey = Key(NextSessionKey, "N"), SearchTerminalKey = Key(SearchTerminalKey, "F"),
        ToggleFilesKey = Key(ToggleFilesKey, "E")
    };

    public IReadOnlyDictionary<string, string> Shortcuts() => new Dictionary<string, string>
    {
        ["new"] = NewSessionKey, ["close"] = CloseSessionKey, ["next"] = NextSessionKey,
        ["search"] = SearchTerminalKey, ["files"] = ToggleFilesKey
    };
    public bool HasUniqueShortcuts => Shortcuts().Values.Distinct().Count() == 5;
    private static string Key(string? key, string fallback) => key is { Length: 1 } && key[0] is >= 'A' and <= 'Z' ? key : fallback;
}
