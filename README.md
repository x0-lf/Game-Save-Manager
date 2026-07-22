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
* **Sync tab** - backup-run sync with a local/mounted folder or an SFTP server:
  * **Saved remote profiles**: create, update, Save As, rename, and explicitly delete named Local Folder or SFTP configurations in the existing Sync target section. Selecting or saving a profile never connects, previews, or syncs.
  * Profiles are stored in SQLite with stable IDs and non-secret settings only. Existing meaningful `sync-settings.json` configuration is migrated once; when a profile is selected, its SQLite values take precedence over the lightweight UI-state file.
  * Provider behavior is described by one capability catalog. Google Drive is configuration-selectable for account authorization while `IsImplemented` remains false for sync.
  * Saved-provider authentication has a platform-neutral secret-store contract. On Windows, payloads are protected for the current user with DPAPI and SQLite stores encrypted BLOBs only. Profile deletion and disconnect remove owned encrypted secrets; SFTP passwords and passphrases remain session-only.
  * Google Drive authorization runs in the system browser with PKCE and a loopback callback, requests only `drive.file`, stores tokens through the protected secret store, restores them after restart, refreshes access when possible, and displays non-secret account metadata.
  * Google Drive backup synchronization is not implemented. The [Google Drive developer setup guide](docs/google-drive-developer-setup.md) explains private development configuration; normal users do not create a Cloud project, and personal credentials or tokens must never be committed.
  * Type-safe provider selection exposes `LocalFolder`, `Sftp`, and configuration-only `GoogleDrive`. Only Local Folder and SFTP can preview or execute sync; WebDAV and OneDrive remain unavailable.
  * `ISyncProvider` abstraction with `LocalFolderSyncProvider` and `SftpSyncProvider` (SSH.NET); WebDAV and cloud providers come later.
  * **SFTP**: host/port/username with password or private-key-file authentication; passwords, passphrases, and trust-new-host confirmation are session-only, cleared when profiles change, and never written to disk. Host keys use trust-on-first-use: the SHA-256 fingerprint is shown on first connect, stored like SSH known_hosts, and any later change fails loudly ("Forget Stored Host Key" covers planned reinstalls).
  * Copy-only, both ways: a run missing on one side is copied there; nothing is ever deleted or overwritten.
  * Sync preview with per-run actions (upload / download / in sync / conflict), counts, and sizes; execution requires explicit confirmation.
  * **Per-run selection**: every upload/download in the plan has a checkbox (plus Select All / Select None and a live "selected X of Y" summary); deselected runs are reported as skipped and stay pending for the next sync.
  * **Live progress**: a byte-accurate progress bar with run x/y and the current file, updated after every copied file.
  * **Connection & sync-status check**: a one-line verdict ("Everything is in sync: N run(s) match the sync target" / counts / the failure reason) shown next to the connection fields - via a dedicated check button or automatically on every preview. Nothing is copied by the check.
  * **Conflict detection**: same run name with different content (compared via manifest identity and per-file SHA-256) is reported and never copied automatically.
  * **Version-history metadata**: every executed sync is appended to a `sync-log.json` stored alongside the remote data (device, timestamp, uploaded/downloaded runs), shared by all devices syncing with that folder.
  * Downloaded runs get their manifest paths rewritten and verified, so they are immediately restorable.
  * The whole tab scrolls as one, and every section (connection, plan, warnings, results, history) is collapsible; a successful preview tucks the connection section away automatically.
* **History tab** - durable run history in SQLite (`transfer_runs` / `transfer_items`):
  * Every executed transfer copy, restore, manual backup, cleanup, and sync is recorded automatically - counts, bytes, flags (dry run, overwrite, backups), blocking reason if refused, and per-file outcomes.
  * Recording failures never fail the run itself; history is a pure audit trail.
* **Regression suite** - repeatable .NET tests for transfer no-overwrite and safe overwrite, backup manifests and hashes, restore integrity, ZIP archive import/export, SQLite history, shared sync-engine safety, provider selection, settings migration, saved profiles, and secret exclusion.
* Resizable panes (drag the dividers) so the app works on 1080p displays.

### Implemented - CLI / developer tooling

* Steam root discovery from the Windows registry, with validation and fallback disk scan.
* `libraryfolders.vdf` and `appmanifest_*.acf` parsing.
* SQLite save-path mapping database with JSON import and review states (Approved / Pending / NeedsFix / Rejected).
* Save-path template expansion (environment tokens, Steam tokens, wildcards) and verification with confidence scores.
* Verified-path backups with SHA-256 hashing and backup run/item records.
* Steam catalog fetching and PCGamingWiki harvesting workflows.

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
│   ├── Transfers/             # Transfer preview/execution, overwrite backups, history, restore
│   └── Sync/                  # ISyncProvider abstraction, sync plans/results, SFTP settings
├── GameSaves.Infrastructure/  # Real implementations
│   ├── Registry/              # Steam root from Windows registry
│   ├── Steam/                 # VDF/manifest readers, discovery, fallback scanner
│   ├── Profiles/              # userdata profile detection
│   ├── Save/                  # SQLite repository, path expander, verifier
│   ├── Transfers/             # Preview, transfer, overwrite backup, history, restore services
│   ├── Sync/                  # SyncEngine + IRemoteFileSystem backends (local folder, SFTP)
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
| `GameSaves.Reviewer`       | Internal Avalonia tool to review whether scraped save paths are accurate before they are trusted (approve/reject/needs-fix with notes, search/filter, keyboard shortcuts). Self-contained: it does not reference Core or Infrastructure. |
| `GameSaves.Tests`          | xUnit regression tests for transfer, backup, restore, archive, history, and sync safety.      |

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
| Sync               | Sync backup runs with a local/mounted folder or an SFTP server: check status → preview → select runs → confirm → Sync Now, with live progress. |
| History            | Every executed transfer, restore, manual backup, cleanup, and sync from SQLite, with per-file outcomes. |

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

### Syncing backups (Sync tab)

1. Select a named Local Folder or SFTP profile, choose **No saved profile (use current settings)** to work directly from the form, or choose **New** to reset an unsaved target. Profile selection never previews or executes sync. Selecting a saved Google Drive profile may silently validate existing protected authentication, but never opens a browser automatically.
2. Save, Save As, rename, or explicitly delete profile configuration as needed. Deletion affects only the profile row, never backups, remote files, history, archives, or SFTP known-host entries.
3. For SFTP, enter the session-only password or passphrase after selecting the profile; profile changes clear these values and the trust-new-host confirmation.
4. **Check Connection & Sync Status** answers "am I in sync?" in one line without copying anything; **Preview Sync (Dry Run)** additionally builds the full plan (upload / download / in sync / conflict per run).
5. Untick any runs you do not want to copy, confirm, and press **Sync Now** - a byte-accurate progress bar tracks the copy.
6. The remote keeps a shared `sync-log.json` of every executed sync; downloaded runs are immediately restorable from the Backups tab.

Google Drive also appears in the provider selector for saved-profile setup and account authorization. It opens Google's supported system-browser flow, requests only `drive.file`, and displays the validated account metadata. Google Drive preview and execution stay disabled because root-folder and remote-file operations are not implemented yet.

Sync never deletes or overwrites anything on either side; conflicts are reported and left for you to resolve (export one side as ZIP, or delete one side via Cleanup).

---

## Requirements

* .NET 8 SDK
* Steam installed on the machine
* Windows (registry-based Steam discovery; other platforms are planned)
* Internet access only for Steam catalog / PCGamingWiki harvesting commands

Main packages: `Avalonia` 12, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData`, `Gameloop.Vdf`, `SSH.NET`, `Google.Apis.Auth` 1.75.0, and `Google.Apis.Drive.v3` 1.75.0.4210.

The official Google client-library packages are referenced only by `GameSaves.Infrastructure`. Milestone J reads developer-local client configuration, performs installed-app OAuth with PKCE, persists tokens only through `ISecretStore`, and makes one minimal Drive `about.get` request for account metadata. No root-folder, listing, upload, download, or sync implementation exists.

Test packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

---

## Local database

```text
%LOCALAPPDATA%\GameSave\gamesave.db
```

Stores save-path mappings (with review status), verification results, backup runs/items (CLI), operation history, named non-secret sync remote profiles, current-user DPAPI-protected sync-secret BLOBs, Steam catalog records, and harvesting queues.

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

## Development roadmap

### A — Baseline and regression protection

* [x] Steam discovery
* [x] Installed-game discovery
* [x] Steam profile detection
* [x] Approved save-path mappings
* [x] Profile-to-profile transfer preview
* [x] Guarded transfer execution
* [x] Backup-before-overwrite
* [x] Manual backup
* [x] Restore workflow
* [x] Restore target selection
* [x] Backup retention cleanup
* [x] ZIP export/import
* [x] SQLite operation history
* [x] Local-folder sync
* [x] SFTP/SSH sync
* [x] Add repeatable regression tests for the current transfer, backup, restore, archive, history, and sync behavior

### B — Replace the two-provider Boolean model

Provider selection now uses one scalable, type-safe provider kind instead of the former `UseSftp` Boolean:

```text
LocalFolder
Sftp
GoogleDrive
WebDav
OneDrive
```

Only `LocalFolder` and `Sftp` are implemented and shown in the Sync selector. The other values reserve stable persisted identities for later roadmap milestones. Existing settings migrate on load without a startup rewrite; passwords and private-key passphrases remain session-only.

* [x] Keep provider selection type-safe
* [x] Preserve existing local-folder and SFTP behavior
* [x] Migrate existing `sync-settings.json` values
* [x] Preserve the user's saved SFTP settings
* [x] Avoid empty Boolean combinations such as `UseSftp`, `UseGoogleDrive`, and `UseWebDav`

### C — Saved sync remote profiles

Named remote configurations are stored in SQLite. Local Folder and SFTP profiles are usable now; Google Drive, WebDAV, and OneDrive profiles remain unavailable roadmap values.

Each profile contains non-secret information only:

* [x] Profile ID
* [x] Display name
* [x] Provider kind
* [x] Account display name
* [x] Remote root display name
* [x] Provider-specific non-secret settings
* [x] Creation date
* [x] Last-used date
* [x] Last successful connection
* [x] Optional remote folder ID
* [x] Never store passwords, passphrases, refresh tokens, or OAuth token caches in plain JSON or plain SQLite fields

### D — Provider capabilities

Introduce a provider capability description so the App does not rely on provider-name checks.

The catalog is the authoritative metadata source. Future-provider capabilities describe the intended design only; `IsImplemented` remains false and preview/execution stay blocked.

* [x] Requires interactive login
* [x] Requires server credentials
* [x] Supports resumable upload
* [x] Supports remote quota
* [x] Supports selecting a remote folder
* [x] Supports persistent authentication
* [x] Supports connection testing
* [x] Supports logout
* [x] Supports opening the remote location in a browser
* [x] Drive UI behavior from capabilities instead of scattered provider-name checks

### E — Secret-store abstraction

Add a platform-neutral secret-storage interface.

The byte-oriented abstraction can securely hold planned OAuth token data, OneDrive token data, WebDAV passwords, and optional SFTP secrets. This storage capability does not implement those providers or enable SFTP credential persistence.

* [x] Store secret
* [x] Read secret
* [x] Delete secret
* [x] Check whether a secret exists
* [x] Keep the interface in Core only if it remains platform-neutral and contains no Windows-specific types
* [x] Put the Windows implementation in Infrastructure
* [x] Support Google OAuth token data, future OneDrive tokens, future WebDAV app passwords, and optionally saved SFTP passwords/passphrases

### F — Windows secure secret storage

Study and choose between Windows DPAPI through `.NET ProtectedData` and Windows Credential Manager.

Windows DPAPI with `DataProtectionScope.CurrentUser` was selected. SQLite contains only versioned encrypted BLOBs; copied data is not expected to decrypt for another Windows user or machine. Corrupted, revoked, or otherwise unreadable authentication remains removable and requires reauthentication. Linux Secret Service and macOS Keychain implementations are future work.

* [x] Encrypt for the current Windows user
* [x] Never log secret values
* [x] Never show stored tokens in the UI
* [x] Remove secrets when the remote profile is deleted or disconnected
* [x] Handle unreadable or corrupted secret data safely
* [x] Document Linux and macOS secret stores as future implementations

### G — Google Cloud setup documentation

The [developer setup guide](docs/google-drive-developer-setup.md) documents the current Google Cloud and Google Auth Platform workflow and adds repository ignore protections. These completed documentation tasks do not create a personal Cloud project, install Google packages, or make Google Drive sync functional.

* [x] Create a Google Cloud project
* [x] Enable Google Drive API
* [x] Configure the OAuth consent screen
* [x] Add development test users while the app is in Testing mode
* [x] Create an OAuth client for a desktop application
* [x] Configure the application's client ID
* [x] Never commit client secrets, downloaded credential files, tokens, or personal account data
* [x] Add relevant local configuration files to `.gitignore`
* [x] Never place a personal Google OAuth token or downloaded private configuration in the repository

### H — Google Drive dependencies and boundaries

Milestone H placed the official Google Drive and authentication packages only in Infrastructure and added regression tests preventing Google SDK types from crossing into Core or App public APIs. Later OAuth work preserves that boundary; Drive synchronization remains unavailable.

* [x] Add only the Google packages needed by Infrastructure
* [x] Keep Google SDK types out of GameSaves.Core, transfer models, backup models, and App view models where practical
* [x] Keep Google SDK usage inside Infrastructure services
* [x] Review and update project package references
* [x] Update `THIRD-PARTY-NOTICES.md`
* [x] Update the README package list and license notices if required

### I — Google Drive connection settings

Add pure connection/settings models containing no persisted access or refresh tokens:

Existing profile columns remain authoritative for the profile ID, account display name, root-folder display name, and root-folder ID. The allowlisted provider JSON contains only the optional account email and exact `drive.file` requested scope. Connection status and token presence are runtime-derived; token presence is not authentication validation. Milestone J now validates authentication through a minimal account lookup, while Drive synchronization remains unavailable.

* [x] Remote profile ID
* [x] Account display name
* [x] Account email, when available
* [x] Google Drive root folder ID
* [x] Google Drive root folder display name
* [x] Requested scope
* [x] Connection status
* [x] Whether a token is stored

### J — Google OAuth login inside the existing App

Implement Google sign-in directly in `GameSaves.App`; do not create another application project.

Google Drive account authorization uses the system browser, a loopback callback, and PKCE. Profile-scoped token data uses the existing protected secret store, and account identity comes from the minimal Drive `about.get` response. This does not enable Drive synchronization, and Milestone K account-lifecycle actions are not included.

* [x] User selects Google Drive as the sync provider
* [x] User clicks **Connect Google Drive**
* [x] Authentication opens through the supported desktop OAuth flow
* [x] Request only `https://www.googleapis.com/auth/drive.file`
* [x] Display the connected account
* [x] Store tokens through the secure secret store
* [x] Keep the user connected after restarting the app
* [x] Refresh authentication without forcing a new login
* [x] Return safely to the UI when authentication is cancelled
* [x] Show a friendly message when authorization is denied
* [x] Do not request full Drive access unless a later feature proves it is necessary

### K — Google account lifecycle

* [ ] Connect
* [ ] Reconnect
* [ ] Disconnect
* [ ] Remove stored authentication
* [ ] Handle revoked authorization
* [ ] Show the connected account
* [ ] Show connection state
* [ ] Prevent sync when no valid account is connected
* [ ] Remove locally stored token data when disconnecting

### L — Google Drive application root folder

For the first Google Drive version, create or find one visible application folder instead of building a full Drive browser:

```text
My Drive/
└── GameSave Manager Backups/
```

* [ ] Store the Drive folder ID and use it as the authoritative identity
* [ ] Use names only for display
* [ ] Reuse the existing folder when reconnecting
* [ ] Do not create duplicate root folders
* [ ] Handle a deleted or moved root folder
* [ ] Recreate the root folder only after explicit user confirmation
* [ ] Do not use Google Drive `appDataFolder` for user backup runs

### M — Refine remote metadata write semantics

Separate the two meanings currently represented by `IRemoteFileSystem.WriteTextFileAsync`:

* [ ] Create a backup-run file only if it is missing
* [ ] Replace or update explicitly mutable provider metadata such as `.gamesave-sync/sync-log.json`
* [ ] Read provider metadata
* [ ] Keep backup-run content create-only and never weaken the no-overwrite rule

### N — Google Drive object/path resolver

Add an Infrastructure component responsible for:

* [ ] Resolving a relative Game Save Manager path to Drive file/folder IDs
* [ ] Finding a child by parent ID and name
* [ ] Creating missing parent folders
* [ ] Escaping Drive search values safely
* [ ] Handling pagination
* [ ] Rejecting ambiguous duplicate objects
* [ ] Caching IDs only when safe
* [ ] Invalidating stale cached IDs
* [ ] Identifying trashed files and folders
* [ ] Keeping Drive query construction out of the provider

### O — GoogleDriveRemoteFileSystem validation

Implement `ValidateAsync` to verify:

* [ ] Google account is connected
* [ ] Token can be refreshed
* [ ] Drive API is reachable
* [ ] Configured root folder exists
* [ ] Configured root is a folder
* [ ] Configured root is not trashed
* [ ] The app can read and write there
* [ ] Return provider-specific, user-friendly errors such as `GoogleDriveNotConnected`, `GoogleDriveAuthorizationRevoked`, `GoogleDriveRootMissing`, `GoogleDriveRootInaccessible`, `GoogleDriveUnavailable`, and `GoogleDriveQuotaExceeded`

### P — Google Drive listing and text metadata

Implement:

* [ ] `RootExistsAsync`
* [ ] `ListRunFolderNamesAsync`
* [ ] `FolderExistsAsync`
* [ ] `ReadTextFileAsync`
* [ ] The safe metadata-write operations introduced in milestone M
* [ ] Support API pagination and request only required fields
* [ ] Use folder/file IDs
* [ ] Ignore folders without `manifest.json`
* [ ] Handle unreadable manifests as warnings
* [ ] Preserve existing `SyncEngine` behavior
* [ ] Support `.gamesave-sync/sync-log.json`

### Q — Google Drive file listing

Implement recursive file listing beneath a backup-run folder.

* [ ] Return paths relative to the run folder
* [ ] Keep `/` as the remote relative separator
* [ ] Handle nested directories
* [ ] Handle pagination
* [ ] Ignore trashed objects
* [ ] Detect ambiguous duplicate names instead of choosing one arbitrarily
* [ ] Support cancellation

### R — Google Drive uploads

* [ ] Stream file uploads without loading entire files into memory
* [ ] Create parent folders as required
* [ ] Never overwrite an existing remote file
* [ ] Report progress through the existing sync progress model
* [ ] Support cancellation
* [ ] Use resumable uploads for larger files
* [ ] Keep `manifest.json` uploaded last
* [ ] Ensure interrupted runs without a manifest are not treated as complete backups
* [ ] Map quota, authentication, permission, and transient errors to clear warnings

### S — Google Drive downloads

* [ ] Stream downloads to a temporary local file first
* [ ] Never overwrite an existing final local file
* [ ] Move the temporary file into place only after a successful download
* [ ] Delete failed temporary files
* [ ] Report byte progress
* [ ] Support cancellation
* [ ] Preserve current manifest rewriting
* [ ] Verify downloaded backup runs before they become restorable
* [ ] Keep SHA-256 manifests as the content identity source

### T — GoogleDriveSyncProvider

Add a thin provider wrapper following the existing SFTP pattern.

* [ ] Own the Google Drive remote filesystem lifetime
* [ ] Create the existing `SyncEngine`
* [ ] Forward preview to `SyncEngine`
* [ ] Forward execution to `SyncEngine`
* [ ] Forward sync-log reading to `SyncEngine`
* [ ] Dispose provider-specific resources
* [ ] Do not reimplement conflict detection, sync plans, selection logic, sync history, manifest comparison, or upload/download decisions

### U — Sync provider factory

* [ ] Extend the existing factory to create Google Drive providers
* [ ] Keep existing local and SFTP factory methods working
* [ ] Keep provider creation free of UI logic
* [ ] Receive dependencies through DI
* [ ] Inject OAuth/token services and secret storage
* [ ] Keep secrets and tokens out of display roots

### V — Sync tab provider selector

Replace the two-provider Sync UI with a selector that initially shows:

```text
Local folder
SFTP server
Google Drive
```

* [ ] Change provider-specific connection fields with the selected provider
* [ ] For Google Drive, show connection status and the connected account
* [ ] Add **Connect Google Drive** and **Disconnect**
* [ ] Show the remote folder name
* [ ] Add **Open in Google Drive/browser** later if safe
* [ ] Keep **Check Connection & Sync Status**
* [ ] Keep upload enabled, download enabled, preview, per-run selection, Select All, Select None, explicit confirmation, Sync Now, live progress, warnings, execution results, and sync history shared by every provider
* [ ] Keep Google Drive in the existing Sync tab

### W — Google Drive sync integration

Connect Google Drive to the existing Sync tab workflow:

* [ ] Local-only run → upload
* [ ] Remote-only run → download
* [ ] Matching run → in sync
* [ ] Same run name with different content → conflict
* [ ] Never copy a conflict automatically
* [ ] Keep deselected runs pending
* [ ] Never overwrite an existing remote run
* [ ] Never overwrite an existing local run
* [ ] Introduce no delete operation
* [ ] Make downloaded runs appear in Backups and remain restorable
* [ ] Record every executed sync in SQLite history
* [ ] Update the shared `sync-log.json`

### X — Retry, cancellation, and incomplete uploads

Add provider-neutral hardening where possible:

* [ ] Expose a Cancel Sync button
* [ ] Cancel the current operation through existing cancellation tokens
* [ ] Retry transient Google API and network failures with bounded retries
* [ ] Respect server retry instructions
* [ ] Do not endlessly retry authentication or permission failures
* [ ] Do not upload the manifest when payload upload fails
* [ ] Identify incomplete remote folders that have no manifest
* [ ] Do not treat incomplete folders as backup runs
* [ ] Initially report incomplete folders without deleting them automatically

### Y — Google Drive acceptance verification

Add automated tests where API calls can be mocked or abstracted, then run live manual verification with a development Google account.

1. [ ] Connect one Google account
2. [ ] Restart the app and remain connected
3. [ ] Disconnect and remove the local token
4. [ ] Create or find one application root folder
5. [ ] Detect a local-only run
6. [ ] Upload the selected run
7. [ ] Verify the manifest is uploaded last
8. [ ] Detect a remote-only run
9. [ ] Download the selected run
10. [ ] Restore the downloaded run
11. [ ] Identify identical runs as in sync
12. [ ] Detect a same-name/different-manifest conflict
13. [ ] Never overwrite remote files
14. [ ] Never overwrite local runs
15. [ ] Never delete local or remote runs
16. [ ] Cancel an active upload
17. [ ] Handle revoked access
18. [ ] Handle a missing root folder
19. [ ] Handle quota and network errors
20. [ ] Record Google Drive sync in SQLite history
21. [ ] App builds
22. [ ] CLI builds
23. [ ] Reviewer builds

### Z — Documentation and next roadmap

After Google Drive is verified:

* [ ] Update README current status
* [ ] Mark Google Drive sync complete
* [ ] Document Google Cloud setup
* [ ] Document the requested OAuth scope
* [ ] Document where tokens are stored
* [ ] Document connect/disconnect behavior
* [ ] Document the visible Drive folder structure
* [ ] Document the copy-only safety model
* [ ] Update `THIRD-PARTY-NOTICES.md`
* [ ] Add screenshots later if useful

Then continue with these future milestones in order:

1. [ ] WebDAV/Nextcloud provider
2. [ ] OneDrive provider
3. [ ] Multiple active/saved remote profiles
4. [ ] Remote quota and health dashboard
5. [ ] Compressed-by-default backups
6. [ ] 7z support
7. [ ] Scheduled backups and sync
8. [ ] Backup diff viewer
9. [ ] Optional client-side encryption before cloud upload
10. [ ] Linux Steam discovery
11. [ ] Proton/Wine prefix support
12. [ ] Steam Deck support
13. [ ] macOS discovery
14. [ ] Cross-platform secret stores
15. [ ] Release packaging, update system, and migration testing

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

Run the regression suite:

```bash
dotnet test Manager/GameSaves.Tests
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
