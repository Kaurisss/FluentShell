# Offline theme regression

For the xterm Windows backdrop prototype, pass `--terminal-backdrop-smoke`.
It compares the stock WinUI WebView2 control with a directly hosted HWND WebView2,
both using the shipped terminal page with the opt-in `?backdrop=1` URL. Normal
sessions keep their opaque backgrounds and saved terminal colors. The prototype
uses separate temporary browser profiles, synthetic terminal output and local
input echo; it never connects to SSH or loads saved application settings.

```powershell
New-Item -ItemType Directory .tmp/terminal-backdrop -Force | Out-Null
dotnet build Tests/FluentShell.ThemeSmoke/FluentShell.ThemeSmoke.csproj -a x64
Start-Process -FilePath Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe -ArgumentList '.tmp/terminal-backdrop/result.json', '--terminal-backdrop-smoke' -WindowStyle Hidden -Wait
```

Add `--keep-backdrop-preview` and omit `-Wait` to leave the checked window open
for manual material/theme/background switching and typing. The JSON report is
written after the checks finish. This desktop check needs `winapp` on PATH and
briefly foregrounds only its own window for native screenshots and keyboard input.
The harness uses the application's PerMonitorV2 manifest so viewport positioning
and screen samples agree at 150% DPI.

The check measures actual screen pixels over two changing XAML background colors,
captures Mica and Desktop Acrylic in light/dark themes, verifies output retention,
real keyboard input and local echo, resize, and an opaque fallback. Screen captures
are required because RenderTargetBitmap omits WebView and system materials.

Verified on Windows 11 build 26200, WebView2 154.0.4258.62, Windows App SDK 2.3.1,
at 150% scaling: the HWND host reveals both diagnostic colors and the window's
Mica/Acrylic material; the stock WinUI control retains its solid theme surface.
The shipped xterm viewport's black CSS fallback also needs a scoped transparent
override, which is enabled only for the prototype URL.

This is a feasibility prototype, not a production replacement for TerminalPane.
A browser HWND must be kept above WinUI's child island and cannot participate in
normal XAML clipping, overlays and z-order. Production adoption still needs
session detach/reattach, dialogs/flyouts, IME, accessibility, and multiple-monitor
DPI verification, or a Composition host that integrates those behaviors.

For terminal color settings, pass `--terminal-colors-smoke` after the report path.
It exercises the actual color picker save, cancel and per-color default actions,
persists only temporary settings, and checks independent light/dark resets.
It captures both expanded palettes in light/dark themes and verifies side-by-side
groups, narrow layouts, all 21 color fields and keyboard focus without clipping.
It also checks the color dialog scrollbar at the right edge, content clearance,
and access to the last input when the dialog is short in light/dark themes.

For settings icons, pass `--settings-icons-smoke` after the report path.
It checks all eight settings pages in light and dark themes, verifies
20 x 20 FluentIcons card headers, captures each page, and exercises the hint
flyout. It also checks the compact hint button (24 x 24) and its centered
FluentIcons artwork (16 x 16). No saved profiles or SSH servers are used.
The hint uses the original FluentIcon size-16 artwork at font size 14 inside its
16 x 16 box to leave room for antialiasing. It checks its four edges
at 100%, 125%, and 150% render scales.

For new-tab motion, pass `--navigation-animation-smoke`. It uses the production
`MainWindow` with profile loading disabled and two offline workspaces. It checks
the actual add button from an active session, intermediate scale/opacity,
the default sidebar entrance, settings sliding from the right in both directions,
animation completion and rapid switching in light and dark themes. Add
`--keep-animation-preview` to leave the checked window open for manual preview;
the JSON report is still written. No SSH connection is opened.

For local context menus and file operations, use `--local-file-menu-smoke` after
the report path. It checks file/folder/multiple/parent selection, disconnected
states, blank-area commands, properties, new folders, renaming, cancellation and
dialog teardown in light and dark themes. Only temporary fixture files are changed;
native Recycle Bin dispatch is covered through the file service's test seam.

For SFTP file/folder drop ingestion, run with `--sftp-upload-drop-smoke` after the
report path. It uses real Windows StorageItems and synthetic connection state,
checks mixed selection, local pane drag data, copy semantics, fixed targets, busy/disconnected states,
late data and view disposal in light and dark themes, and captures the SFTP pane.
It does not connect to a server; Explorer's native drag gesture still needs a manual check.

Windows/x64 interactive desktop check using the real WinUI session, sidebar, SFTP controls and WebView2 page. The test-only executable is unpackaged; the application's deployment model is unchanged. It reuses `App` resources but overrides startup so it never opens `MainWindow`, loads saved profiles, or connects to an SSH server. The local file pane performs its normal read-only directory listing.

```powershell
dotnet build Tests/FluentShell.ThemeSmoke/FluentShell.ThemeSmoke.csproj -a x64
& Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe theme-smoke.json
```

The temporary window exits automatically. Exit code 0 and `passed: true` in the JSON report indicate success. A failed assertion or startup exception produces exit code 1 and its details in the report. A missing report is not a passing run.

For the transfer center only, pass `--transfer-center-smoke`. It checks the real
task cards in light/dark themes at 520 and 280 DIPs, the existing corner radii,
live filter counts, progress and file-detail updates, keyboard focus retention,
pause/resume, retry, discard, discovery with unknown totals, and native flyout
reload. It saves layout screenshots next to the JSON report. All tasks are
synthetic; it never loads saved profiles or connects to a server:

```powershell
Start-Process -FilePath Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe -ArgumentList '.tmp/transfer-center-smoke.json', '--transfer-center-smoke' -WindowStyle Hidden -Wait
```

For the in-app text editor, pass the --text-editor-smoke option after the report path.
It exercises native WinUIEdit windows, local and remote entry points, light/dark
layout, the settings flyout (read-only, wrapping, line numbers, whitespace and
indentation), UTF-8 Chinese/emoji search, undo/redo, CRLF preservation, save
conflicts, native unsaved-close protection, loading cancellation and late results.
Local saves use a temporary file; remote
saves use an in-memory service. It never loads saved profiles or connects to a server.

With WinApp CLI available, add `--native-editor-ui` to verify title-bar hover and
Ctrl+F/Ctrl+S with actual input. This also captures light/dark window and settings
screenshots from the desktop, including Mica, which RenderTargetBitmap omits.
These commands target only the offline editor's own window handles.

For same-server tabs, pass `--multi-session-smoke`. This opens two real workspaces
for one synthetic profile, exercises the tab buttons and close action, and checks
independent terminal output and session-specific sidebar metrics. It never connects
to a server or loads saved profiles:

```powershell
& Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe .tmp/multi-session-smoke.json --multi-session-smoke
```

For tab overflow, pass `--tab-overflow-smoke`. This exercises 20 synthetic tabs,
both scroll buttons, selection at both ends, the adjacent add button, keyboard focus,
and closing tabs until they fit. It uses the production caption/input regions in
an extended title bar and injects vertical/horizontal wheel input into its own
foreground window, including fractional/rapid input and input over close buttons.
The cursor is briefly positioned over each target and restored unless the user
has moved it. It also checks both scroll boundaries,
add-button placement with one/two tabs and caret edges at 100%, 125%, and 150%
render scales. It captures light/dark title bars at 800, 1200, and 1700 DIPs
without opening SSH connections:

```powershell
Start-Process -FilePath Tests/FluentShell.ThemeSmoke/bin/Debug/net8.0-windows10.0.19041.0/win-x64/FluentShell.ThemeSmoke.exe -ArgumentList '.tmp/tab-overflow-smoke.json', '--tab-overflow-smoke' -WindowStyle Hidden -Wait
```

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
