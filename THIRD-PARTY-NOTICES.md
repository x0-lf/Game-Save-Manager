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
| Avalonia | 12.1.2 | MIT | Cross-platform UI framework. |
| Avalonia.Desktop | 12.1.2 | MIT | Desktop application support for Avalonia. |
| Avalonia.Themes.Fluent | 12.1.2 | MIT | Fluent theme package for Avalonia. |
| Avalonia.Fonts.Inter | 12.1.2 | MIT | Inter font package used by Avalonia. |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT | Data grid control used by the desktop app and Reviewer tool. |
| Avalonia.Headless | 12.1.2 | MIT | Headless rendering platform; referenced only by the development-only GameSaves.UiCapture screenshot harness, never shipped. |
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | MIT | Avalonia diagnostics support package used for development/debug builds. |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM helpers, observable objects, and relay commands. |
| Google.Apis.Auth | 1.76.0 | Apache-2.0 | Official OAuth 2.0 authorization and credential primitives; referenced only by Infrastructure. |
| Google.Apis.Drive.v3 | 1.76.0.4261 | Apache-2.0 | Official generated Google Drive API v3 client library; referenced only by Infrastructure. |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT | Dependency injection container and service registration helpers. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | MIT | Dependency injection abstractions. |
| Microsoft.Extensions.Hosting | 10.0.12 | MIT | Generic hosting infrastructure. |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | SQLite ADO.NET provider. |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | Apache-2.0 | Explicit native SQLite bundle constraint used by the projects that directly own SQLite initialization. |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | Windows DPAPI access for current-user secret protection. |
| ValveKeyValue | 0.20.0.417 | MIT | KeyValues1 parser used for Steam VDF files; referenced only by Infrastructure. |
| SSH.NET | 2026.0.0 | MIT | SFTP/SSH client used by the SFTP sync provider. |
| SharpCompress | 0.50.4 | MIT | Pure managed archive library used for 7-Zip (.7z) LZMA/LZMA2 export, import, and zero-extraction inspection; referenced only by Infrastructure. |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | .NET test host and discovery support; test project only. |
| xunit.v3 | 4.0.1 | Apache-2.0 | Regression test framework; test project only. |
| xunit.runner.visualstudio | 4.0.0 | Apache-2.0 | VSTest adapter for xUnit; test project only. |

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
of Core or App APIs. They implement Google Drive OAuth and backup-run sync
inside Infrastructure; provider behavior and limits are documented in
`docs/sync-providers.md`.

Official project and package information:

- [Google API Client Library for .NET source](https://github.com/googleapis/google-api-dotnet-client)
- [Google.Apis.Drive.v3 on NuGet](https://www.nuget.org/packages/Google.Apis.Drive.v3/1.76.0.4261)
- [Google.Apis.Auth on NuGet](https://www.nuget.org/packages/Google.Apis.Auth/1.76.0)

The packages are licensed under Apache-2.0. Their direct transitive Google
dependencies resolve to `Google.Apis` 1.76.0 and `Google.Apis.Core` 1.76.0;
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

`GameSaves.Infrastructure`, `GameSaves`, and `GameSaves.Reviewer` explicitly
reference `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12. The constraint prevents NuGet
from selecting the vulnerable 2.1.11 native library still permitted by the
provider's minimum dependency range. The bundle package metadata declares the
Apache-2.0 license. This package-only remediation introduces no database schema
or persisted-format change.

### System.Text.Json

System.Text.Json is used to read, migrate, and write the non-secret Sync UI settings.
It is supplied by the `Microsoft.NETCore.App` shared framework rather than by a
NuGet package reference, so no separate package version is pinned for it.

### System.Security.Cryptography.ProtectedData

System.Security.Cryptography.ProtectedData is used only by Infrastructure to
protect sync authentication payloads with Windows DPAPI current-user scope.
Core and App do not reference DPAPI types.

### ValveKeyValue

`ValveKeyValue` 0.20.0.417 is used to
parse Steam KeyValues1 library and application-manifest data. The package is
licensed under MIT according to its official package metadata and is referenced
only by `GameSaves.Infrastructure`. The CLI consumes provider-neutral Steam
discovery interfaces and does not directly reference a VDF parser. Replacing
`Gameloop.Vdf` removes its obsolete `NETStandard.Library` 1.6.1 dependency
chain.

### SSH.NET

SSH.NET is used by the SFTP sync provider for SSH authentication and remote file operations.

### SharpCompress

`SharpCompress` 0.50.4 is a pure managed C# compression and archive library.
It is licensed under the MIT license and referenced only by
`GameSaves.Infrastructure`. It provides reading, writing, and zero-extraction
metadata inspection for 7-Zip (`.7z`) archives using LZMA and LZMA2 compression
without requiring native C++ or Win32 `7z.dll` dependencies.

### Test packages

`Microsoft.NET.Test.Sdk`, `xunit.v3`, and `xunit.runner.visualstudio` are used only by
`GameSaves.Tests` to run the repeatable regression suite.

## Additional / transitive dependencies

The packages above may bring additional transitive dependencies. Those packages remain
licensed under their own terms as provided by NuGet and their respective authors.

Before publishing binary releases, review the full dependency graph with:

```bash
dotnet list package --include-transitive
```
