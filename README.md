# Game Save Manager

Game Save Manager is a .NET 8 desktop tool for discovering installed Steam games and Steam profiles, resolving known save-game locations, and **safely copying saves between local Steam profiles** - with automatic backups, SHA-256 integrity checking, and a full restore workflow.

The project ships two front ends over a shared core:

* **GameSaves.App** - the Avalonia desktop application (the main user-facing app).
* **GameSaves** - the original CLI, now used as developer tooling for discovery, verification, catalog fetching, and PCGamingWiki harvesting.

The long-term goal is a cross-platform Steam save manager with backup profiles, restore, synchronization, and cloud-provider integration.

---

## Current status

### Implemented - desktop app (Avalonia)

* Dashboard with Steam root, library count, installed games, Steam profiles, and mapping counts.
* Installed-games list with per-game save-mapping status (approved / pending / needs-fix), file counts, and sizes.
* Steam profile detection from `userdata`, with source/target profile selection.
* **Transfer Preview** - copy saves between local Steam profiles:
  * First-class **Steam userdata game folder** transfer (`<SteamRoot>\userdata\<AccountId>\<AppId>`), independent of the mapping database.
  * Approved save-path mappings shown as a second group, expanded per profile.
  * Explicit **transfer source selection**: Steam userdata folder only, approved mappings only, or both. Preview is blocked with a clear message when neither is selected; equivalent paths are deduplicated so files are never copied twice.
  * **Blocked-item handling**: items with errors (for example a non-profile-specific mapping resolving to the same path for both profiles) block the copy by default; an explicit "Skip blocked items and copy the rest" opt-in copies the remaining safe items while each blocked item is reported as skipped.
  * Dry-run preview with file counts, total size, conflict status, and a plain-English "what will happen" line per item.
  * Execution requires an explicit confirmation checkbox; overwrite is off by default.
  * **Backup before overwrite (Safe Mode)** - every target file about to be replaced is backed up first; if the backup fails, that file is not overwritten.
  * Per-file execution results: copied / skipped / failed, with reasons and backup locations.
* **Backups tab** - backup history and restore:
  * Lists every backup run from its `manifest.json` (game, profiles, timestamp, files, size).
  * Restore with dry-run preview, confirmation gate, and overwrite opt-in.
  * SHA-256 integrity check: tampered or missing backup files are never restored.
  * Pre-restore backup: files replaced by a restore are themselves backed up, so a restore is always undoable.
* Resizable panes (drag the dividers) so the app works on 1080p displays.

### Implemented - CLI / developer tooling

* Steam root discovery from the Windows registry, with validation and fallback disk scan.
* `libraryfolders.vdf` and `appmanifest_*.acf` parsing.
* SQLite save-path mapping database with JSON import and review states (Approved / Pending / NeedsFix / Rejected).
* Save-path template expansion (environment tokens, Steam tokens, wildcards) and verification with confidence scores.
* Verified-path backups with SHA-256 hashing and backup run/item records.
* Steam catalog fetching and PCGamingWiki harvesting workflows.

### Not implemented yet

* Manual on-demand backups from the GUI (back up a chosen game to a chosen destination).
* SQLite transfer-history tables (`transfer_runs` / `transfer_items`).
* Archive support (ZIP/7z).
* Cloud sync providers (local-folder sync provider first, then WebDAV/Nextcloud, SFTP, OneDrive/Google Drive).
* Linux / macOS / Steam Deck discovery and platform-specific path expansion.
* Scheduled backups, diff viewer, encryption.

---

## Safety model

The transfer and restore flows are built around a small set of hard rules:

1. **Copy, never move.** Nothing in the transfer or restore flow deletes source files. There is no delete anywhere in the pipeline.
2. **Preview first.** Every copy and restore starts as a dry run; execution is a separate, explicitly confirmed step.
3. **Overwrite is opt-in.** Existing target files are skipped by default.
4. **Backup before overwrite.** With Safe Mode (default on), a file is only overwritten after it has been backed up with a SHA-256 manifest entry. A failed backup refuses the overwrite.
5. **Path containment.** Steam userdata transfers are validated - at preview *and again at execution* - to stay inside the expected `userdata\<AccountId>\<AppId>` roots. Per-file path-traversal guards prevent any write outside the target root.
6. **Auditable results.** Every file's outcome (copied / skipped / failed / backed up, and why) is shown in the UI and recorded in backup manifests.

---

## Repository layout

```text
Manager/
├── Manager.sln
├── GameSaves.Core/            # Pure models, enums, interfaces (no SQLite/registry/VDF/Avalonia/filesystem)
│   ├── Steam/                 # Discovery models and interfaces
│   ├── Profiles/              # Steam profile model and detector interface
│   ├── Save/                  # Mappings, verification, save-status models
│   └── Transfers/             # Transfer preview/execution, overwrite backups, history, restore
├── GameSaves.Infrastructure/  # Real implementations
│   ├── Registry/              # Steam root from Windows registry
│   ├── Steam/                 # VDF/manifest readers, discovery, fallback scanner
│   ├── Profiles/              # userdata profile detection
│   ├── Save/                  # SQLite repository, path expander, verifier
│   ├── Transfers/             # Preview, transfer, overwrite backup, history, restore services
│   └── DependencyInjection/   # Service registration
├── GameSaves.App/             # Avalonia + CommunityToolkit.Mvvm desktop app
│   ├── ViewModels/            # MainWindow, InstalledGames, Profiles, TransferPreview, BackupHistory
│   ├── Views/                 # AXAML views (Dashboard, tabs)
│   └── Models/                # Row view-models
├── GameSaves/                 # CLI: discovery, verify, backup, catalog fetch, PCGW harvester
└── GameSaves.Reviewer/        # Internal mapping-review tool for harvested save paths
```

Layering rules:

| Project                    | Responsibility                                                                               |
| -------------------------- | -------------------------------------------------------------------------------------------- |
| `GameSaves.Core`           | Domain models, enums, and interfaces only. Platform-neutral, dependency-free.                |
| `GameSaves.Infrastructure` | Registry, VDF, filesystem, SQLite, path expansion, transfer/backup/restore logic.            |
| `GameSaves.App`            | Thin Avalonia UI over the services. No business logic in views.                              |
| `GameSaves`                | Developer CLI and data-harvesting tooling. Kept working as a test harness.                   |
| `GameSaves.Reviewer`       | Internal tool to review whether scraped save paths are accurate before they are trusted.     |

Trusted data rule: only mappings with review status **Approved** are used by the transfer flow. Pending/NeedsFix mappings are visible but never trusted automatically.

---

## The desktop app

Run it:

```bash
dotnet run --project Manager/GameSaves.App
```

Tabs:

| Tab                | What it does                                                                                       |
| ------------------ | -------------------------------------------------------------------------------------------------- |
| Dashboard          | Steam root, libraries, installed games, profiles, and mapping counts. Refresh re-scans everything.  |
| Installed Games    | Every installed game with mapping status, save-path existence, file counts, and sizes.              |
| Profiles           | Detected Steam profiles from `userdata`, with source/target selection and folder shortcuts.         |
| Transfer Preview   | Copy saves between profiles: pick source, target, and game → Preview Copy (Dry Run) → confirm → Copy to Target Profile. |
| Backups            | Every backup run with its files and hashes; restore with dry-run preview and confirmation.          |

### How a profile-to-profile copy works

1. Select a source profile, a target profile, and an installed game.
2. **Preview Copy (Dry Run)** builds the plan:
   * the Steam userdata game folder (`userdata\<source>\<AppId>` → `userdata\<target>\<AppId>`), if enabled;
   * every approved save-path mapping, expanded for both profiles;
   * warnings (same profile, missing source, existing target, non-profile-specific mappings, containment failures).
3. Check "I understand this will copy files into the target profile".
4. Optionally enable overwrite (existing files are skipped otherwise). Backup-before-overwrite is on by default.
5. **Copy to Target Profile** - relative paths and timestamps preserved, per-file results listed.

### Backups and restore

Automatic backups live under the app data folder:

```text
%LOCALAPPDATA%\GameSave\TransferBackups\
└── 20260711_143512_transfer_227300_1199012097_to_1703495256\
    ├── manifest.json          # game, profiles, timestamps, per-file SHA-256
    └── files\C\...            # full original path mirrored, timestamps preserved
```

Restore (Backups tab) copies backed-up files to their original locations:

* Dry-run preview first, explicit confirmation to execute.
* Files identical to their backup are skipped as "already matches".
* Files that differ are only replaced with overwrite enabled - and the replaced version is backed up first as a new restore-kind run.
* Every backup file is verified against its manifest SHA-256 before it is restored.

---

## Requirements

* .NET 8 SDK
* Steam installed on the machine
* Windows (registry-based Steam discovery; other platforms are planned)
* Internet access only for Steam catalog / PCGamingWiki harvesting commands

Main packages: `Avalonia` 12, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Data.Sqlite`, `Gameloop.Vdf`.

---

## Local database

```text
%LOCALAPPDATA%\GameSave\gamesave.db
```

Stores save-path mappings (with review status), verification results, backup runs/items (CLI), Steam catalog records, and harvesting queues.

Initialize:

```bash
dotnet run --project Manager/GameSaves -- init-db
```

---

## Save-path mappings

Mappings are imported from JSON and reviewed before being trusted:

```json
[
  {
    "steamAppId": "413150",
    "gameName": "Stardew Valley",
    "platform": "windows",
    "pathTemplate": "%APPDATA%\\StardewValley\\Saves",
    "pathKind": "Directory",
    "sourceName": "Manual",
    "notes": "Example mapping",
    "priority": 100
  }
]
```

```bash
dotnet run --project Manager/GameSaves -- import savepaths.json
```

Supported template tokens:

| Token               | Meaning                                      |
| ------------------- | -------------------------------------------- |
| `%USERPROFILE%` / `{UserProfile}`   | User profile directory.      |
| `%APPDATA%` / `{AppData}`           | Roaming AppData.             |
| `%LOCALAPPDATA%` / `{LocalAppData}` | Local AppData.               |
| `%PROGRAMDATA%` / `{ProgramData}`   | ProgramData.                 |
| `%DOCUMENTS%` / `{Documents}`       | Documents directory.         |
| `{SteamRoot}`       | Detected Steam root.                         |
| `{SteamUserData}`   | Steam `userdata` directory.                  |
| `{AppId}`           | Steam AppID.                                 |
| `{GameName}`        | Steam game name.                             |
| `{LibraryRoot}`     | Steam library root.                          |
| `{GameInstallPath}` | Installed game directory.                    |
| `{InstallDir}`      | Steam install folder name from the manifest. |

Wildcards `*` and `?` are supported. Profile-aware expansion replaces userdata wildcards/tokens with the selected Steam profile's account ID.

---

## CLI reference

```bash
dotnet run --project Manager/GameSaves -- help
```

| Command | Purpose |
| ------- | ------- |
| `discover` / `discover-deep` | Steam discovery (with optional deep fallback scan). |
| `init-db` | Create the local SQLite database. |
| `import <file.json>` | Import save-path mappings. |
| `verify` | Verify mapped save paths for installed games. |
| `backup-dry-run <dest>` / `backup <dest>` | Preview / run a backup of verified save paths. |
| `steam-catalog-fetch <dir> games <n> [apikey]` | Fetch Steam catalog data. |
| `steam-catalog-missing <file> <n> <bool>` | Export missing Steam AppIDs. |
| `steam-catalog-queue-missing` / `steam-catalog-export-next <file> <n>` | Manage the harvesting queue. |
| `pcgw-harvest-appids <dir> <user-agent> <appids...>` | Harvest PCGamingWiki data for specific AppIDs. |
| `pcgw-harvest-installed <dir> <user-agent> <n>` | Harvest PCGamingWiki data for installed games. |

Harvested data is candidate data: save locations can be wrong, incomplete, outdated, or platform-specific. Review mappings (GameSaves.Reviewer) before approving them.

---

## Roadmap

### Milestone 1 - Local GUI manager and safe local transfer ✅ done

* [x] 1A - Foundation: Core/Infrastructure/App split, CLI kept as tooling, approved-mappings trust model.
* [x] 1B - GUI discovery: Steam root, libraries, installed games, profiles, dashboard, mapping status.
* [x] 1C - Steam userdata game-folder transfer as a first-class concept:
  * [x] `SteamUserDataGameFolder` transfer source and `CopyScope` semantics.
  * [x] Preview independent of approved mappings; grouped UI.
  * [x] Path containment checks at preview and execution.
  * [x] Copy-only wording; no delete anywhere; overwrite off by default; explicit confirmation.
* [x] Resizable, 1080p-friendly UI.

### Phase 2 - Backup system 🔨 in progress

* [x] Backup-before-overwrite hook with Safe Mode (failed backup refuses the overwrite).
* [x] SHA-256 hashing and per-run `manifest.json`.
* [x] Backup history read from manifests.
* [x] Restore with dry-run preview, confirmation gate, overwrite opt-in, integrity check, and pre-restore backup.
* [ ] **Next: manual on-demand backups** - back up a selected game's saves for a chosen profile to a chosen destination, without waiting for an overwrite.
* [ ] Backup, Copy, Move From Profile To Profile, with possibility to move it from Steam profile to profile, or from mapping that are in the database
    at C:\Users\<username>\AppData\Local\GameSave\gamesave.db
* [ ] Backup destination selection and named backup profiles.
* [ ] SQLite history tables (`transfer_runs`, `transfer_items`) for transfers and restores.
* [ ] Retention/cleanup for old backup runs.
* [ ] Archive support (ZIP first, 7z later).

### Phase 3 - Cloud sync abstraction

* [ ] `ISyncProvider` abstraction and sync preview.
* [ ] `LocalFolderSyncProvider` first (no cloud dependency).
* [ ] Conflict detection and version-history metadata.
* [ ] WebDAV/Nextcloud → SFTP/SSH → OneDrive/Google Drive (Mega only if feasible).

### Phase 4 - Cross-platform

* [ ] Windows path-resolver cleanup.
* [ ] Linux and macOS Steam discovery.
* [ ] Proton/Wine prefix support; Steam Deck support.
* [ ] Platform-specific save-path expansion and per-platform mapping validation.

### Phase 5 - Advanced features

* [ ] Encryption, scheduled backups, diff viewer, cloud conflict UI, modding export/import.

---

## Design principles

* Do not guess when save files are involved; prefer verified mappings over scraped ones.
* Every large or destructive-adjacent operation has a dry run, and execution is explicitly confirmed.
* Copy, never move: source saves are never deleted or modified by transfer or restore.
* Overwrites are opt-in and protected by automatic backups with SHA-256 manifests.
* Preserve timestamps; keep every workflow auditable end to end.
* Core stays free of SQLite, registry, VDF, Avalonia, and filesystem details; the GUI stays a thin layer over tested services.

---

## Development

Build everything:

```bash
dotnet build Manager/Manager.sln
```

Run the desktop app:

```bash
dotnet run --project Manager/GameSaves.App
```

Run the CLI:

```bash
dotnet run --project Manager/GameSaves -- help
```

---

## Disclaimer

Game Save Manager is experimental software. Save files can represent hundreds or thousands of hours of progress - keep an independent backup before testing transfer, restore, or synchronization features.
