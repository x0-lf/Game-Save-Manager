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
| Google.Apis.Auth | 1.75.0 | Apache-2.0 | Official OAuth 2.0 authorization and credential primitives; referenced only by Infrastructure for later Google provider milestones. |
| Google.Apis.Drive.v3 | 1.75.0.4210 | Apache-2.0 | Official generated Google Drive API v3 client library; referenced only by Infrastructure. |
| Microsoft.Extensions.DependencyInjection | 10.0.9 | MIT | Dependency injection container and service registration helpers. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | MIT | Dependency injection abstractions. |
| Microsoft.Extensions.Hosting | 10.0.9 | MIT | Generic hosting infrastructure. |
| Microsoft.Data.Sqlite | 10.0.9 | MIT | SQLite ADO.NET provider. |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | Windows DPAPI access for current-user secret protection. |
| System.Text.Json | 10.0.9 | MIT | JSON serialization used by sync settings and regression tests. |
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

### Google API Client Library for .NET

`Google.Apis.Drive.v3` is the official generated Google Drive API v3 client
library. `Google.Apis.Auth` provides OAuth 2.0 authorization and credential
primitives for the official Google API Client Library for .NET. Both packages
are referenced only by `GameSaves.Infrastructure`; no Google SDK type is part
of Core or App APIs, and adding these packages does not implement Google Drive
sync or OAuth login.

Official project and package information:

- [Google API Client Library for .NET source](https://github.com/googleapis/google-api-dotnet-client)
- [Google.Apis.Drive.v3 on NuGet](https://www.nuget.org/packages/Google.Apis.Drive.v3/1.75.0.4210)
- [Google.Apis.Auth on NuGet](https://www.nuget.org/packages/Google.Apis.Auth/1.75.0)

The packages are licensed under Apache-2.0. Their direct transitive Google
dependencies resolve to `Google.Apis` 1.75.0 and `Google.Apis.Core` 1.75.0;
`System.Management` 7.0.2 is also supplied transitively by `Google.Apis.Auth`.
These transitive packages are not promoted to direct project references.

### Microsoft.Extensions packages

Microsoft.Extensions packages are used for dependency injection and application service wiring.

Packages used:

- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Hosting`

### Microsoft.Data.Sqlite

Microsoft.Data.Sqlite is used for the local SQLite database storing save-path mappings,
verification data, catalog/harvest data, and application history.

### System.Text.Json

System.Text.Json is used to read, migrate, and write the non-secret Sync UI settings.

### System.Security.Cryptography.ProtectedData

System.Security.Cryptography.ProtectedData is used only by Infrastructure to
protect sync authentication payloads with Windows DPAPI current-user scope.
Core and App do not reference DPAPI types.

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
```
