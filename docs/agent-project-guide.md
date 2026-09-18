# FluentShell project guide

Read the section relevant to the task. Source code and project files establish current behavior; this guide locates the main boundaries.

## Connections and sessions

- [ShellCoordinator](../Application/ShellCoordinator.cs) manages profiles, credentials, connection progress, session selection, and cancellation.
- [SessionConnection](../Application/SessionConnection.cs) owns one session's connection state, reconnection, metrics, and channel lifetime. Its `post` callback marshals updates; it does not assume the UI thread.
- [SessionCoordinator](../Application/SessionCoordinator.cs) contains sessions and selection state.
- [SessionWorkspace](../Views/SessionWorkspace.cs) adapts a connection to the shell's session interface and hosts terminal/SFTP UI.
- [MainWindow](../MainWindow.xaml.cs) switches between the disconnected pages and connected session workspace, and applies window/theme/layout state.

Connection progress and cancellation use the existing connection dialog. Failed or cancelled connections must release their resources; successful connections enter the session coordinator. Preserve host-key confirmation independently of credential validation.

## SFTP

[Application/SftpWorkspace.cs](../Application/SftpWorkspace.cs) coordinates operations and view state; [SftpSessionController](../Application/SftpSessionController.cs) manages navigation and file operations. Browsing and transfers use separate SFTP connections to keep the directory view responsive.

For a new SFTP operation, follow the existing interface/client/service/controller/view path only as far as the operation requires. [Services/SftpFileService.cs](../Services/SftpFileService.cs) contains file-service behavior; [SftpWorkspaceView](../Views/Session/SftpWorkspaceView.xaml) uses Syncfusion `SfDataGrid`.

Batch uploads/downloads use [FileConflictResolver](../Application/FileConflictResolver.cs) and [FileConflictDialog](../Views/Shell/FileConflictDialog.xaml). Overwrite, skip, cancel-all, and apply-to-all decisions belong to the current batch. Reset the resolver at each batch boundary.

## Persistence and profile changes

[LocalStore](../Services/LocalStore.cs) persists profiles and settings as JSON under `%LOCALAPPDATA%\FluentShell`. [CredentialService](../Services/CredentialService.cs) uses Windows Credential Manager. The app stores a private key's path, not a copy of the key.

For profile fields, inspect `ServerProfile`, the profile editor, persistence, and update records together. Preserve serialization compatibility and keep passwords/passphrases out of profile JSON. Validation tests use temporary files and fakes; real user keys and servers are unnecessary for local unit checks.

## UI and dialogs

Keep the existing coordinator/view split. Introducing MVVM, replacing `{Binding}` with `{x:Bind}`, or migrating localization is a separate design change unless the current task requires it.

Use theme resources and shared styles from [App.xaml](../App.xaml). Follow [ShellLayoutMode](../Application/ShellLayoutMode.cs) for actual responsive breakpoints and supported window sizes. Keep light, dark, high contrast, focus, and keyboard behavior intact in the affected surface.

Use [ShellDialogService](../Views/Shell/ShellDialogService.cs) for existing message, credential, fingerprint, and session-close dialogs. Every `ContentDialog` needs the correct `XamlRoot`. Match the project's dialog radius of 8 and button radius of 4 through shared or explicit styles. Built-in dialog buttons use `PrimaryButtonStyle`, `SecondaryButtonStyle`, and `CloseButtonStyle`; a generic Button resource does not style those buttons. Style the button roles the dialog actually uses.

The terminal's JavaScript and WebView2 message bridge are required features. Treat remote terminal text as data and constrain navigation/message sources; blanket disabling scripts or messages would break the terminal.

## Tests

[Tests/FluentShell.Tests](../Tests/FluentShell.Tests) contains MSTest tests and in-tree fakes for services, coordinators, paths, and session state. It references the WinUI app, so specify x64. See [run-tests](../.agents/skills/run-tests/SKILL.md) for execution and filtering; use the existing fakes and test seams before adding infrastructure.
