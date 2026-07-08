# Game Save Manager

Game Save Manager is an experimental .NET tool for discovering installed Steam games, resolving known save-game locations, verifying which save paths exist on the current machine, and backing them up safely.

The long-term goal is to become a cross-platform Steam save-game manager with a graphical interface, save transfer, restore, synchronization, and cloud-provider integration. The current repository is an early CLI-focused foundation that concentrates on Steam discovery, save-path mapping, verification, backup, and data harvesting.

---

## Current status

This project is currently a prototype / foundation layer.

Implemented now:

* Detect Steam installation from the Windows registry.
* Validate the discovered Steam root.
* Parse Steam `libraryfolders.vdf`.
* Read installed Steam games from `appmanifest_*.acf` files.
* Use a fallback disk scan when normal Steam discovery fails.
* Store save-path mappings in a local SQLite database.
* Import save-path mappings from JSON.
* Expand save-path templates using Steam/game/user-directory tokens.
* Verify whether mapped save paths exist.
* Count files and total size for verified save paths.
* Save verification results to the database.
* Run dry-run backups before copying files.
* Back up verified save files/directories.
* Compute SHA-256 hashes during backup.
* Record backup runs and backup items in SQLite.
* Fetch Steam catalog data.
* Export missing Steam AppIDs for future PCGamingWiki harvesting.
* Harvest save-path data for selected or installed Steam AppIDs.

Not implemented yet:

* Avalonia desktop UI.
* Restore workflow.
* User-to-user save transfer.
* Cloud sync providers such as Google Drive, OneDrive, Mega, Nextcloud, FTP, or SSH.
* Full cross-platform save-path resolution.
* Archive creation/extraction using ZIP/7z.
* Background sync.
* Conflict resolution.
* Automatic scheduled backups.
* Full curated save-location database coverage for all Steam titles.

---

## Project goals

Game Save Manager is being designed around a simple idea:

> Find the user’s installed Steam games, match them against a verified save-location database, verify the real save files on disk, and make it easy to back up, move, restore, or synchronize those saves.

The planned application should eventually answer questions like:

* Which Steam games are installed?
* Which Steam users exist on this machine?
* Which save-location rules match each installed game?
* Which of those save paths actually exist?
* Which files changed since the last backup?
* Where should these files be restored for another Windows user?
* How can saves be transferred to another PC?
* How can saves be synchronized through cloud or self-hosted storage?

---

## Requirements

### Required

* .NET 8 SDK
* Steam installed on the machine
* Windows for the current registry-based Steam root discovery
* Internet access for Steam catalog and PCGamingWiki harvesting commands

### NuGet packages currently used

* `Gameloop.Vdf`
* `Microsoft.Data.Sqlite`

---

## Repository layout

Current layout:

```text
Manager/
└── GameSaves/
    ├── Backup/
    ├── Data/
    ├── External/
    ├── SavePaths/
    ├── External/
    │   ├── Steam/
    │   └── Titles/
    ├── Program.cs
    └── GameSaves.csproj
```

Main areas:

| Area                        | Purpose                                                                                        |
| --------------------------- | ---------------------------------------------------------------------------------------------- |
| `Program.cs`                | CLI entry point and command dispatcher.                                                        |
| `SteamDiscoveryService`     | Orchestrates Steam root, library, manifest, and fallback discovery.                            |
| `RegistrySteamLocator`      | Reads Steam install path from the Windows registry.                                            |
| `SteamLibraryFoldersReader` | Reads Steam library paths from `libraryfolders.vdf`.                                           |
| `SteamAppManifestReader`    | Reads installed games from Steam app manifests.                                                |
| `SteamFallbackScanner`      | Scans disks for Steam libraries when normal discovery fails.                                   |
| `SavePathDatabase`          | Stores mappings, verification results, catalog data, and backup records in SQLite.             |
| `SavePathExpander`          | Expands path templates such as `%APPDATA%`, `{SteamRoot}`, `{AppId}`, and `{GameInstallPath}`. |
| `SavePathVerifier`          | Checks candidate save paths and assigns confidence scores.                                     |
| `BackupManager`             | Copies verified save files into timestamped backup folders.                                    |
| `SteamCatalogService`       | Fetches Steam catalog data and exports AppIDs for harvesting.                                  |

---

## Planned architecture

The project is expected to move toward a cleaner multi-project structure:

```text
src/
├── GameSaves.App/
├── GameSaves.Core/
└── GameSaves.Infrastructure/
```

Planned responsibilities:

| Project                    | Responsibility                                                                                                                 |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| `GameSaves.Core`           | Domain models, interfaces, save-path rules, backup planning, restore planning, and platform-neutral logic.                     |
| `GameSaves.Infrastructure` | Steam discovery, filesystem access, SQLite persistence, PCGamingWiki/Steam integrations, archive handling, and sync providers. |
| `GameSaves.App`            | Avalonia UI, view models, user workflows, settings, backup/restore screens, and progress reporting.                            |

The current CLI prototype can later become a development/testing tool while the Avalonia application becomes the main user-facing app.

---

## How Steam discovery works

The current discovery pipeline works in stages:

1. Try to locate Steam using the Windows registry.
2. Validate that the discovered path looks like a real Steam installation.
3. Add the Steam root as a library candidate.
4. Read additional Steam libraries from `libraryfolders.vdf`.
5. Validate each Steam library.
6. Read installed games from `appmanifest_*.acf`.
7. If normal discovery fails, optionally run a fallback disk scan.
8. Return discovered Steam root, libraries, games, warnings, and confidence levels.

Discovery confidence:

| Confidence | Meaning                                                            |
| ---------- | ------------------------------------------------------------------ |
| `High`     | Game was found from normal Steam metadata.                         |
| `Low`      | Game/library was found through fallback scanning.                  |
| `Orphaned` | Game data appears incomplete or disconnected from normal metadata. |

---

## Local database

The application stores its local database under the user’s local application data folder:

```text
%LOCALAPPDATA%\GameSave\gamesave.db
```

The database is used for:

* Save-path mappings.
* Verification results.
* Backup runs.
* Backup items.
* Steam catalog records.
* Missing AppID queues for future harvesting.

Initialize the database:

```bash
dotnet run --project Manager/GameSaves -- init-db
```

---

## Save-path mappings

Save-path mappings can be imported from JSON.

Example mapping file:

```json
[
  {
    "steamAppId": "413150",
    "gameName": "Stardew Valley",
    "platform": "windows",
    "pathTemplate": "%APPDATA%\\StardewValley\\Saves",
    "pathKind": "Directory",
    "sourceName": "Manual",
    "sourceUrl": null,
    "sourceLicense": null,
    "notes": "Example mapping",
    "priority": 100
  }
]
```

Import mappings:

```bash
dotnet run --project Manager/GameSaves -- import savepaths.json
```

Supported path-template examples:

```text
%APPDATA%\SomeGame\Saves
%LOCALAPPDATA%\SomeGame\Saved
%USERPROFILE%\Documents\My Games\SomeGame
{Documents}\My Games\SomeGame
{SteamRoot}\userdata\*\{AppId}\remote
{GameInstallPath}\saves
{LibraryRoot}\steamapps\common\{InstallDir}\saves
```

Supported tokens include:

| Token               | Meaning                                      |
| ------------------- | -------------------------------------------- |
| `%USERPROFILE%`     | Current user profile directory.              |
| `%APPDATA%`         | Current user roaming AppData directory.      |
| `%LOCALAPPDATA%`    | Current user local AppData directory.        |
| `%PROGRAMDATA%`     | Common ProgramData directory.                |
| `%DOCUMENTS%`       | Current user Documents directory.            |
| `{UserProfile}`     | Current user profile directory.              |
| `{AppData}`         | Current user roaming AppData directory.      |
| `{LocalAppData}`    | Current user local AppData directory.        |
| `{ProgramData}`     | Common ProgramData directory.                |
| `{Documents}`       | Current user Documents directory.            |
| `{SteamRoot}`       | Detected Steam root.                         |
| `{SteamUserData}`   | Steam `userdata` directory.                  |
| `{AppId}`           | Steam AppID.                                 |
| `{GameName}`        | Steam game name.                             |
| `{LibraryRoot}`     | Steam library root.                          |
| `{GameInstallPath}` | Installed game directory.                    |
| `{InstallDir}`      | Steam install folder name from the manifest. |

Wildcards such as `*` and `?` are supported in path expansion.

---

## CLI commands

Show help:

```bash
dotnet run --project Manager/GameSaves -- help
```

Run normal discovery:

```bash
dotnet run --project Manager/GameSaves -- discover
```

Run discovery with deep fallback scan:

```bash
dotnet run --project Manager/GameSaves -- discover-deep
```

Initialize the database:

```bash
dotnet run --project Manager/GameSaves -- init-db
```

Import save-path mappings:

```bash
dotnet run --project Manager/GameSaves -- import savepaths.json
```

Verify mapped save paths for installed Steam games:

```bash
dotnet run --project Manager/GameSaves -- verify
```

Preview a backup without copying files:

```bash
dotnet run --project Manager/GameSaves -- backup-dry-run "D:\Backups\GameSaves"
```

Run a real backup:

```bash
dotnet run --project Manager/GameSaves -- backup "D:\Backups\GameSaves"
```

Fetch Steam catalog data:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-fetch External/SteamCatalog games 1000
```

Fetch all available Steam games using a Steam Web API key:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-fetch External/SteamCatalog games 0 YOUR_STEAM_WEB_API_KEY
```

Alternatively, set the API key as an environment variable:

```bash
set STEAM_WEB_API_KEY=your_key_here
dotnet run --project Manager/GameSaves -- steam-catalog-fetch External/SteamCatalog games 0
```

Export missing Steam AppIDs:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-missing External/SteamCatalog/missing-appids.txt 1000 false
```

Queue missing games for harvesting:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-queue-missing
```

Export the next queued batch:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-export-next External/SteamCatalog/batch-001.txt 1000
```

Harvest PCGamingWiki data for specific AppIDs:

```bash
dotnet run --project Manager/GameSaves -- pcgw-harvest-appids External/Titles "GameSaveManager/0.1 (https://github.com/x0-lf/Game-Save-Manager; your-email@example.com) .NET/8.0" 413150
```

Harvest PCGamingWiki data for installed Steam games:

```bash
dotnet run --project Manager/GameSaves -- pcgw-harvest-installed External/Titles "GameSaveManager/0.1 (https://github.com/x0-lf/Game-Save-Manager; your-email@example.com) .NET/8.0" 10
```

---

## Backup behavior

Backups are created from verified save paths.

The backup process:

1. Discovers installed Steam games.
2. Loads save-path mappings for each Steam AppID.
3. Expands candidate save paths.
4. Verifies whether those paths exist.
5. Saves verification results to SQLite.
6. Copies verified files to the backup destination.
7. Stores backup item records in the database.
8. Optionally computes SHA-256 hashes.

Backup output is timestamped and grouped by Steam AppID and game name.

Example structure:

```text
D:\Backups\GameSaves\
└── 413150_Stardew Valley\
    └── 20260707_180000\
        └── C__Users_User_AppData_Roaming_StardewValley_Saves\
            └── save files...
```

Use dry-run mode before running a real backup:

```bash
dotnet run --project Manager/GameSaves -- backup-dry-run "D:\Backups\GameSaves"
```

Then run the actual backup:

```bash
dotnet run --project Manager/GameSaves -- backup "D:\Backups\GameSaves"
```

---

## Data harvesting workflow

The project includes early tooling for building a larger Steam save-location dataset.

A typical workflow:

1. Fetch Steam catalog data.
2. Store Steam app metadata in SQLite.
3. Export missing game AppIDs.
4. Harvest PCGamingWiki data for those AppIDs.
5. Extract save-path mappings.
6. Review and approve mappings before relying on them.

Example:

```bash
dotnet run --project Manager/GameSaves -- steam-catalog-fetch External/SteamCatalog games 1000
dotnet run --project Manager/GameSaves -- steam-catalog-missing External/SteamCatalog/missing-appids.txt 1000 false
dotnet run --project Manager/GameSaves -- pcgw-harvest-appids External/Titles "GameSaveManager/0.1 (https://github.com/x0-lf/Game-Save-Manager; your-email@example.com) .NET/8.0" External/SteamCatalog/missing-appids.txt
```

Important: harvested data should be treated as candidate data until reviewed. Save locations can be wrong, incomplete, outdated, platform-specific, or mixed with configuration paths.

---

## Roadmap

### Milestone 1 — Clean foundation

* Keep the current CLI working.
* Split domain logic from infrastructure.
* Prepare `GameSaves.Core`.
* Prepare `GameSaves.Infrastructure`.
* Prepare `GameSaves.App`.
* Move Steam discovery behind interfaces.
* Move SQLite persistence behind repository abstractions.
* Keep the existing CLI as a test harness.

### Milestone 2 — Avalonia application shell

* Create Avalonia MVVM app.
* Use CommunityToolkit.Mvvm.
* Add navigation shell.
* Add discovery screen.
* Add installed-games list.
* Add basic settings screen.
* Show Steam root, libraries, games, and warnings.

### Milestone 3 — Save verification UI

* Display mapped save paths per game.
* Show path confidence.
* Show file counts and total size.
* Show missing mappings.
* Allow manual path override.
* Allow user confirmation of correct save paths.

### Milestone 4 — Backup and restore

* Add backup profiles.
* Add dry-run preview UI.
* Add restore preview UI.
* Add backup history.
* Add hash verification.
* Add archive support.

### Milestone 5 — Transfer and sync

* Add user-to-user save transfer.
* Add local folder sync.
* Add cloud-provider abstractions.
* Add Nextcloud/WebDAV support.
* Add OneDrive/Google Drive/Mega support if feasible.
* Add FTP/SFTP/SSH support.
* Add conflict detection.

### Milestone 6 — Cross-platform support

* Add Linux Steam discovery.
* Add macOS Steam discovery.
* Add platform-specific save-path expansion.
* Add per-platform mapping validation.
* Add Proton/Wine prefix support.
* Add Steam Deck support.

---

## Design principles

* Do not guess blindly when save files are involved.
* Prefer verified mappings over scraped mappings.
* Always support dry-run mode before destructive or large file operations.
* Preserve original file timestamps and attributes where possible.
* Keep backup, restore, transfer, and sync workflows auditable.
* Store enough metadata to explain what happened.
* Treat every save path as platform-specific unless proven otherwise.
* Keep infrastructure separate from core logic.
* Make the GUI a thin layer over tested application services.

---

## Safety notes

This project deals with user save data. Save files can represent hundreds or thousands of hours of progress.

Before using real backup or restore features:

* Run discovery first.
* Import only mappings you trust.
* Use `verify`.
* Use `backup-dry-run`.
* Check the printed paths carefully.
* Back up to a separate destination.
* Do not delete original saves until restore is fully implemented and tested.

---

## Development

Restore packages and build:

```bash
dotnet restore Manager/GameSaves/GameSaves.csproj
dotnet build Manager/GameSaves/GameSaves.csproj
```

Run the CLI:

```bash
dotnet run --project Manager/GameSaves -- help
```

Run the default discovery test:

```bash
dotnet run --project Manager/GameSaves
```

---

## Disclaimer

Game Save Manager is experimental software.

<<<<<<< HEAD
Use it carefully, especially when working with real save files. Always keep an independent backup before testing restore, transfer, synchronization, or automation features.
=======
Use it carefully, especially when working with real save files. Always keep an independent backup before testing restore, transfer, synchronization, or automation features.
>>>>>>> 50e5d04 (Update README and harvesting batch files)
