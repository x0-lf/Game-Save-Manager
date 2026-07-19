# Third-Party Notices

Game Save Manager uses third-party open-source packages and libraries.

This file summarizes the main third-party dependencies used by the project.
Each third-party component remains licensed under its own license terms. This
project's MIT license applies only to the Game Save Manager source code owned
by this repository's author, not to third-party packages.

This notice is provided for convenience and should be kept up to date when
dependencies are added, removed, or upgraded.

## Main dependencies

| Package | Version used | License | Notes |
|---|---:|---|---|
| Avalonia | 12.0.5 | MIT | Cross-platform UI framework. |
| Avalonia.Desktop | 12.0.5 | MIT | Desktop application support for Avalonia. |
| Avalonia.Themes.Fluent | 12.0.5 | MIT | Fluent theme package for Avalonia. |
| Avalonia.Fonts.Inter | 12.0.5 | MIT | Inter font package used by Avalonia. |
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | MIT | Avalonia diagnostics support package used for development/debug builds. |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM helpers, observable objects, and relay commands. |
| Microsoft.Extensions.DependencyInjection | 10.0.9 | MIT | Dependency injection container and service registration helpers. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | MIT | Dependency injection abstractions. |
| Microsoft.Extensions.Hosting | 10.0.9 | MIT | Generic hosting infrastructure. |
| Microsoft.Data.Sqlite | 10.0.9 | MIT | SQLite ADO.NET provider. |
| Gameloop.Vdf | 0.6.2 | MIT | Valve Data Format parser used for Steam VDF files. |
| SSH.NET | 2024.2.0 | MIT | SFTP/SSH client used by the SFTP sync provider. |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | .NET test host and discovery support; test project only. |
| xunit | 2.9.3 | Apache-2.0 | Regression test framework; test project only. |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | VSTest adapter for xUnit; test project only. |

## Dependency purpose

### Avalonia packages

Avalonia is used to build the desktop graphical interface.

Packages used:

- `Avalonia`
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `Avalonia.Fonts.Inter`
- `AvaloniaUI.DiagnosticsSupport`

### CommunityToolkit.Mvvm

CommunityToolkit.Mvvm is used for the MVVM application structure, including:

- observable view models
- generated properties
- relay commands
- async relay commands

### Microsoft.Extensions packages

Microsoft.Extensions packages are used for dependency injection and application service wiring.

Packages used:

- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Hosting`

### Microsoft.Data.Sqlite

Microsoft.Data.Sqlite is used for the local SQLite database storing save-path mappings,
verification data, catalog/harvest data, and application history.

### Gameloop.Vdf

Gameloop.Vdf is used to parse Steam VDF files, including Steam library and manifest data.

### SSH.NET

SSH.NET is used by the SFTP sync provider for SSH authentication and remote file operations.

### Test packages

`Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` are used only by
`GameSaves.Tests` to run the repeatable regression suite.

## Additional / transitive dependencies

The packages above may bring additional transitive dependencies. Those packages remain
licensed under their own terms as provided by NuGet and their respective authors.

Before publishing binary releases, review the full dependency graph with:

```bash
dotnet list package --include-transitive
