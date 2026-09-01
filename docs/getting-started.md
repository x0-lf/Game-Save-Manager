# Getting Started

This guide owns runtime and source-build requirements. For build, test, and
diagnostic commands, use the [development guide](development.md).

## Requirements

- Windows 10 or 11 for the complete supported desktop workflow
- .NET 10 SDK to build or run from source
- Git when cloning or contributing
- Internet access for the first NuGet restore
- Steam installed for automatic registry discovery and real profile workflows

Linux and macOS can build provider-neutral parts, but complete desktop, Steam
discovery, and secret-store support is not claimed. Google Drive development
also needs developer-local OAuth configuration; normal users should not create
a Google Cloud project.

## Run from source

From the repository root:

```powershell
dotnet restore Manager/Manager.sln
dotnet run --project Manager/GameSaves.App/GameSaves.App.csproj
```

The application stores its database and settings under:

```text
%LOCALAPPDATA%\GameSave\
```

The main database is `gamesave.db`. UI and lightweight sync state use separate
JSON files in the same directory. Google tokens are not stored in those JSON files.

## First run

1. Let the startup scan finish. If Steam is not found, use the Dashboard scan action.
2. Review Installed games and Profiles before attempting a transfer.
3. Create a Manual backup with disposable or copied data first.
4. Inspect the preview, destination, and overwrite settings before confirmation.
5. Verify the backup appears in Backups and test a restore with nonessential data.

The complete workflows and UI states are in the [desktop guide](desktop-app.md).
The guarantees and deletion boundaries are in the [safety model](safety-model.md).

## Google Drive

Packaged end-user OAuth configuration does not yet have a release process.
Developers testing Google Drive must follow the
[developer-only setup guide](google-drive-developer-setup.md) and must never
commit credentials, tokens, account values, or Drive IDs.
