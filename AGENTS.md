# Repository Guidelines

## Project Structure & Module Organization

FluentShell is a .NET 8 WinUI 3 SSH/SFTP client. `MainWindow.xaml` and its code-behind implement the application shell, navigation, and session tabs. Connected-session UI lives in `Views/SessionWorkspace.cs`. Keep reusable connection, storage, and credential logic in `Services/`; data contracts belong in `Models/`. Runtime images, icons, and the checked-in xterm.js bridge are under `Assets/`. `TerminalWeb/` records the npm packages used to source terminal assets. Product decisions and planned work are documented in `PLAN.md` and `BACKLOG.md`.

Do not edit generated content in `bin/`, `obj/`, `.tmp/`, or `TerminalWeb/node_modules/`.

## Build, Test, and Development Commands

Run commands from the repository root in PowerShell:

```powershell
dotnet restore .\FluentShell.csproj -p:Platform=x64
dotnet build .\FluentShell.csproj -c Debug -p:Platform=x64
dotnet run --project .\FluentShell.csproj -p:Platform=x64
dotnet test -p:Platform=x64
```

Use `x86` or `ARM64` instead of `x64` when validating another target. Visual Studio also exposes packaged and unpackaged launch profiles. When updating xterm dependencies, run `npm ci --prefix .\TerminalWeb` and intentionally synchronize the required browser files into `Assets/Terminal/`.

## Coding Style & Naming Conventions

Use four-space indentation for C# and XAML. Follow standard C# naming: `PascalCase` for types, methods, properties, and named XAML controls; `_camelCase` for private fields; and an `Async` suffix for asynchronous methods. Keep nullable reference types enabled and handle nullability instead of suppressing warnings. Prefer small, focused services and reuse existing WinUI resources from `App.xaml` rather than adding one-off colors or styles.

## Testing Guidelines

There is no automated test project yet. New service or model logic should add xUnit tests under `Tests/FluentShell.Tests/`, named `{TypeName}Tests.cs`. Cover changed behavior rather than targeting a numeric percentage. For UI changes, manually verify light and dark themes, expanded and compact sidebars, session switching, terminal input, and SFTP navigation. Never run destructive SFTP tests against a personal or production server.

## Commit & Pull Request Guidelines

This checkout contains no Git history, so use concise Conventional Commit-style subjects such as `feat: add SFTP parent entry` or `fix: preserve session state`. Keep commits scoped and exclude credentials, local profiles, build output, and `.csproj.user` changes. Pull requests should explain behavior changes, list verification commands, link relevant issues or backlog items, and include before/after screenshots for UI work.

## Security & Configuration

Store secrets only through `CredentialService`; never place passwords, private-key passphrases, host data, or fingerprints in source or test fixtures. Preserve host-key confirmation behavior and document any new third-party dependency in `THIRD-PARTY-NOTICES.md`.
