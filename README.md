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
* **Manual Backup tab** - on-demand backups:
  * Choose a profile, an installed game, a backup source (Steam userdata folder, approved mappings, or both), and a destination folder via a native folder picker ("Choose Folder...") or by typing/editing the path directly.
  * Dry-run preview showing exactly which save locations and how many files will be backed up.
  * Every run is a fresh timestamped folder with mirrored original paths and a SHA-256 `manifest.json` - nothing is ever overwritten or deleted.
  * Backups written to the default location appear in the Backups tab and are restorable from there.
  * **Named backup presets**: save the destination and source selection under a name (stored in SQLite) and reapply it with one click. Applying a preset never starts a backup by itself.
* **Backups tab** - backup history and restore:
  * Lists every backup run from its `manifest.json` (game, profiles, timestamp, files, size).
  * Restore with dry-run preview, confirmation gate, and overwrite opt-in.
  * **Restore target selection**: restore to the original locations (default), redirect the backup into a selected Steam profile's userdata folder for the same game, or restore into an approved mapping location resolved from the database - with the resolved target path shown before execution. Only approved, enabled mappings that resolve to exactly one path can be used.
  * **Cleanup / retention** - the only delete in the app: remove a selected backup run, or apply a retention policy (keep newest N, optionally only runs older than D days). Preview-first with explicit confirmation; only manifest-bearing run folders inside the application backup base are ever deleted - save files and custom-destination backups are never touched. Every cleanup is recorded in the run history.
  * **ZIP archive export / import**: export any backup run as a single self-contained ZIP (files + manifest) for cold storage or another machine, and import such a ZIP back into the backup base - the manifest's backup-file paths are rewritten to the extracted location and verified, so the imported run is fully restorable. Nothing is ever overwritten.
  * SHA-256 integrity check: tampered or missing backup files are never restored.
  * Pre-restore backup: files replaced by a restore are themselves backed up, so a restore is always undoable.
* **Sync tab** - backup-run sync with a local or mounted folder (NAS share, USB drive, cloud-synced folder):
  * `ISyncProvider` abstraction with `LocalFolderSyncProvider` as the first provider (WebDAV/SFTP/cloud come later).
  * Copy-only, both ways: a run missing on one side is copied there; nothing is ever deleted or overwritten.
  * Sync preview with per-run actions (upload / download / in sync / conflict), counts, and sizes; execution requires explicit confirmation.
  * **Conflict detection**: same run name with different content (compared via manifest identity and per-file SHA-256) is reported and never copied automatically.
  * **Version-history metadata**: every executed sync is appended to a `sync-log.json` stored alongside the remote data (device, timestamp, uploaded/downloaded runs), shared by all devices syncing with that folder.
  * Downloaded runs get their manifest paths rewritten and verified, so they are immediately restorable.
* **History tab** - durable run history in SQLite (`transfer_runs` / `transfer_items`):
  * Every executed transfer copy, restore, and manual backup is recorded automatically - counts, bytes, flags (dry run, overwrite, backups), blocking reason if refused, and per-file outcomes.
  * Recording failures never fail the run itself; history is a pure audit trail.
* Resizable panes (drag the dividers) so the app works on 1080p displays.

### Implemented - CLI / developer tooling

* Steam root discovery from the Windows registry, with validation and fallback disk scan.
* `libraryfolders.vdf` and `appmanifest_*.acf` parsing.
* SQLite save-path mapping database with JSON import and review states (Approved / Pending / NeedsFix / Rejected).
* Save-path template expansion (environment tokens, Steam tokens, wildcards) and verification with confidence scores.
* Verified-path backups with SHA-256 hashing and backup run/item records.
* Steam catalog fetching and PCGamingWiki harvesting workflows.

### Not implemented yet

* Compressed-by-default backups and 7z support (ZIP export/import of runs is done).
* Remote sync providers: WebDAV/Nextcloud, SFTP/SSH, OneDrive/Google Drive (the local-folder provider and the `ISyncProvider` abstraction are done).
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
| Manual Backup      | Back up one game's saves for one profile on demand: choose sources and destination → preview → confirm → Back Up Now.   |
| Backups            | Every backup run with its files and hashes; restore with dry-run preview and confirmation.          |
| History            | Every executed transfer, restore, and manual backup from SQLite, with per-file outcomes.             |

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

### Phase 2 - Backup system ✅ done

* [x] Backup-before-overwrite hook with Safe Mode (failed backup refuses the overwrite).
* [x] SHA-256 hashing and per-run `manifest.json`.
* [x] Backup history read from manifests.
* [x] Restore with dry-run preview, confirmation gate, overwrite opt-in, integrity check, and pre-restore backup.
* [x] Manual on-demand backups - back up a selected game's saves for a chosen profile to a chosen destination (Manual Backup tab), without waiting for an overwrite.
* [x] Backup and copy from profile to profile, sourced from the Steam userdata game folder and/or approved mappings in the database
    at C:\Users\<username>\AppData\Local\GameSave\gamesave.db (copy-only by design - move/delete is intentionally not supported).
* [x] Named backup presets (saved destination + source selection, stored in SQLite, applied from the Manual Backup tab).
* [x] SQLite history tables (`transfer_runs`, `transfer_items`) recording transfers, restores, and manual backups, with a History tab.
* [x] Retention/cleanup for old backup runs (preview-first, confirmation-gated, strictly scoped to the backup base, recorded in run history).
* [x] ZIP archive support: export a run as one self-contained ZIP, import it back restorable (7z later).

### Phase 3 - Cloud sync abstraction 🔨 in progress

* [x] `ISyncProvider` abstraction and sync preview (Sync tab, dry-run first, confirmation-gated).
* [x] `LocalFolderSyncProvider` first (no cloud dependency; NAS/USB/cloud-synced folders).
* [x] Conflict detection (manifest + SHA-256 comparison, conflicts reported, never auto-resolved) and version-history metadata (`sync-log.json` on the remote).
* [ ] WebDAV/Nextcloud → SFTP/SSH → OneDrive/Google Drive (Mega only if feasible) - deliberately on hold until the provider approach is reviewed.

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
