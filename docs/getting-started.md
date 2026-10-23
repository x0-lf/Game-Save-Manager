# Getting Started

This guide owns runtime and source-build requirements. For build, test, and
diagnostic commands, use the [development guide](development.md).

## Requirements

- Windows 10 or 11 (x64) for the complete supported desktop workflow
- .NET 10 SDK to build or run from source; the installed package carries its
  own runtime and needs neither the SDK nor a .NET runtime
- Git when cloning or contributing
- Internet access for the first NuGet restore
- Steam installed for automatic registry discovery and real profile workflows

Linux and macOS can build provider-neutral parts, but complete desktop, Steam
discovery, and secret-store support is not claimed. Google Drive development
also needs developer-local OAuth configuration; normal users should not create
a Google Cloud project.

## Install the Windows package

The installer is `GameSaveManager-Setup-<version>.exe`, produced by
`build/publish-release.ps1` (see the [development guide](development.md)).
It installs for the current user only, asks for no administrator rights, and
contains the .NET runtime, so the installed application needs no .NET SDK or
runtime on the machine.

- **Install.** Run the installer. The default location is
  `%LOCALAPPDATA%\Programs\Game Save Manager`; keep it, because scheduled
  backups and syncs store the program path in their Windows task. A Start
  menu entry is created; a desktop shortcut is optional.
- **Upgrade.** Run the newer installer over the existing install. It closes a
  running copy, replaces the program files in place, and changes nothing under
  `%LOCALAPPDATA%\GameSave\`: the database, backups, settings, saved sign-ins
  and scheduled jobs are all kept, and the Windows tasks keep pointing at the
  same path.
- **Uninstall.** Use Settings > Apps > Installed apps, or `unins000.exe` in
  the install folder. The uninstaller removes the program files only and tells
  you where your data was kept. Remove scheduled backups and syncs in the app
  before uninstalling; a task left behind points at a program that no longer
  exists and fails at its trigger time until it is deleted in Task Scheduler.
- **Unsigned builds.** A package built without a code-signing certificate
  shows a Windows SmartScreen warning on first run. Choose "More info", then
  "Run anyway", only when you built the package yourself or trust its source.
  Signed release builds are not yet produced (see the roadmap).

Running from source or from a copied folder instead of the installed location
changes the program path. The Manual backup and Sync pages then show the task
as starting a different program, and **Apply schedule** re-points it (see the
[desktop guide](desktop-app.md)).

## Updates

The app never contacts the network to look for updates unless you ask it to.

- **Opt in.** Settings > Behaviour > **Check for updates on start** is off by
  default. When on, the app asks this project's GitHub release feed once after
  startup, at most once a day, whether a newer version exists. The request is
  one plain HTTPS GET of
  `https://api.github.com/repos/x0-lf/Game-Save-Manager/releases/latest` with
  no account, token or identifier; nothing about you or your machine is sent,
  and nothing is downloaded or installed by itself.
- **Check now.** Settings > Diagnostics > Updates asks the feed at once,
  whatever the switch says. The result is one of: a newer version with its
  size and a **Release notes** button; "Up to date"; "No release has been
  published yet"; or a short failure message when the feed cannot be reached.
  A newer version is also announced in a notice at the top of the Dashboard.
- **Download and verify.** The installer is downloaded to
  `%LOCALAPPDATA%\GameSave\updates\` as a `.partial` file while its SHA-256 is
  computed, and compared with the digest published beside the release
  (`GameSaveManager-Setup-<version>.exe.sha256`). Only a matching file is kept
  under its final name; a mismatched, incomplete or cancelled download is
  deleted and the reason shown. The Authenticode state is shown as well
  (unsigned, or the signer); it is informational while packages are unsigned.
- **Install now** appears only for a verified file. It starts the installer's
  wizard and closes the app; the installer upgrades the program in place at the
  same path, so your database, backups, settings, saved sign-ins and scheduled
  jobs under `%LOCALAPPDATA%\GameSave\` are untouched. The downloaded file is
  marked as coming from the internet, so Windows shows the same SmartScreen
  prompt as for a browser download of an unsigned package (see above).
- **Offline.** A failed check shows a short message where the result would be
  and changes nothing else; startup is never delayed by it. Scheduled backups
  and syncs (`--run-job`, `--run-sync`) never check for updates.

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

The first start creates `%LOCALAPPDATA%\GameSave\gamesave.db` from the
catalog shipped with the package (game titles and save-path mappings; nothing
personal is in it), or empty with the bundled curated mappings when the
package carries no catalog. A database that already exists is never replaced.

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
