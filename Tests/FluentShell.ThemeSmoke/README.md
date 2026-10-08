# Offline theme regression

Windows/x64 interactive desktop check using the real WinUI session, sidebar, SFTP controls and WebView2 page. The test-only executable is unpackaged; the application's deployment model is unchanged. It reuses `App` resources but overrides startup so it never opens `MainWindow`, loads saved profiles, or connects to an SSH server. The local file pane performs its normal read-only directory listing.

```powershell
dotnet build Tests/FluentShell.ThemeSmoke/FluentShell.ThemeSmoke.csproj -a x64
& Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe theme-smoke.json
```

The temporary window exits automatically. Exit code 0 and `passed: true` in the JSON report indicate success. A failed assertion or startup exception produces exit code 1 and its details in the report. A missing report is not a passing run.

For the SFTP deletion flow only, pass `--sftp-delete-smoke` after the report path.
This checks busy/completed states and directory/link confirmation dialogs in light
and dark themes, including cancellation. It uses synthetic entries and never
connects to a server or deletes files:

```powershell
& Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe .tmp/sftp-delete-smoke.json --sftp-delete-smoke
```

The scenario creates a session in light mode, detaches it as when navigating to settings, changes the root theme, and reattaches it for Light → Dark → Light. Checks include the effective themes of the workspace/terminal/SFTP grids/path inputs, actual sidebar metric foreground colors, and the WebView page's actual computed background and color scheme.

For remote directory properties, use `--sftp-properties-smoke`. It checks asynchronous
loading, totals above 4 GB, cancellation and late results, read errors, and file/link
properties in light and dark themes. It uses synthetic results and never connects
to a server:

```powershell
& Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe .tmp/sftp-properties-smoke.json --sftp-properties-smoke
```

The scenario also opens the secondary terminal-color settings page and its real
ColorDialog, checks cancel/confirm and back navigation, then verifies applying
and resetting a custom background on the cached terminal. It uses only in-memory
settings and does not modify the user's saved preferences.

Terminal palette contrast and bridge behavior are also covered by:

```powershell
node --test Tests/Terminal/theme.test.cjs
```
