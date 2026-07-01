# How To Test Steam Save-Game Manager

This guide explains how to test Steam discovery, SQLite database initialization, save-path import, save-path verification, and dry-run backup.

## File location

Create this help file at:

```text
/Help/HowToTest.md
```

Create the sample save-path database file in the project root, next to your `.csproj` file:

```text
savepaths.sample.json
```

Example project layout:

```text
GameSaves/
├─ GameSaves.csproj
├─ Program.cs
├─ savepaths.sample.json
├─ Help/
│  └─ HowToTest.md
```

---

## Create `savepaths.sample.json`

Create a file named:

```text
savepaths.sample.json
```

with this content:

```json
[
  {
    "steamAppId": "413150",
    "gameName": "Stardew Valley",
    "platform": "windows",
    "pathTemplate": "%APPDATA%\\StardewValley\\Saves",
    "pathKind": "Directory",
    "sourceName": "ManualSeed",
    "sourceUrl": null,
    "sourceLicense": "Project-local",
    "notes": "Initial manually curated sample mapping.",
    "priority": 10
  },
  {
    "steamAppId": "413150",
    "gameName": "Stardew Valley",
    "platform": "windows",
    "pathTemplate": "{SteamRoot}\\userdata\\*\\{AppId}\\remote",
    "pathKind": "Directory",
    "sourceName": "SteamCloudCandidate",
    "sourceUrl": null,
    "sourceLicense": "Project-local",
    "notes": "Candidate Steam Cloud cache path; verify before trusting.",
    "priority": 90
  }
]
```

---

## Test order

Open a terminal in the folder containing your `.csproj` file.

First, test Steam discovery with deep fallback scanning:

```bash
dotnet run -- discover-deep
```

This confirms that Steam installation, Steam libraries, and installed games can be discovered.

Then initialize the SQLite database:

```bash
dotnet run -- init-db
```

This creates the local database at:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

You do **not** need Docker, PostgreSQL, MariaDB, SQL Server, or a separate SQLite server. SQLite is only a local `.db` file created and used directly by the application.

Then import the sample save-path mappings:

```bash
dotnet run -- import savepaths.sample.json
```

Then verify save paths:

```bash
dotnet run -- verify
```

Then test backup without copying files:

```bash
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

The intended flow is:

```bash
dotnet run -- discover-deep
dotnet run -- init-db
dotnet run -- import savepaths.sample.json
dotnet run -- verify
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

---

## Important note about the sample

The sample mapping uses:

```text
steamAppId: 413150
```

This is the Steam AppID for Stardew Valley.

If Stardew Valley is not installed, the import will still work, but `verify` may not show any save-path verification output for this mapping because the tool only verifies mappings for games discovered from installed Steam app manifests.

---

## Test with any installed game

If you do not have Stardew Valley installed, test with any installed Steam game.

First run:

```bash
dotnet run -- discover
```

Find one installed game in the output and copy its `AppId`.

Example:

```text
Game: Some Game
 - AppId: 123456
```

Create a fake test save folder:

```text
C:\Users\<YourUser>\GameSaveTest\123456\Saves
```

Put a dummy file inside it:

```text
test-save.dat
```

Then replace the contents of `savepaths.sample.json` with this:

```json
[
  {
    "steamAppId": "123456",
    "gameName": "My Test Game",
    "platform": "windows",
    "pathTemplate": "{UserProfile}\\GameSaveTest\\123456\\Saves",
    "pathKind": "Directory",
    "sourceName": "ManualSeed",
    "sourceUrl": null,
    "sourceLicense": "Project-local",
    "notes": "Local test mapping.",
    "priority": 10
  }
]
```

Replace `123456` with the real AppID from your discovered game.

Then run:

```bash
dotnet run -- import savepaths.sample.json
dotnet run -- verify
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

Expected result from `verify`:

```text
Exists: True
Files: 1
Confidence: 90+
```

Expected result from `backup-dry-run`:

```text
Dry-run backup plan:
 - C:\Users\<YourUser>\GameSaveTest\123456\Saves\test-save.dat
   -> D:\GameSaveBackups\...
   Copied: False
```

Because this is a dry run, no files should actually be copied.

---

## Real backup test

Only after dry-run output looks correct, run a real backup:

```bash
dotnet run -- backup "D:\GameSaveBackups"
```

After this command, check:

```text
D:\GameSaveBackups
```

You should see a backup folder named with the Steam AppID and game name.

---

## Useful commands

```bash
dotnet run -- discover
```

Runs normal Steam discovery.

```bash
dotnet run -- discover-deep
```

Runs Steam discovery with fallback disk scanning enabled.

```bash
dotnet run -- init-db
```

Creates or updates the local SQLite database.

```bash
dotnet run -- import savepaths.sample.json
```

Imports save-path mappings into the SQLite database.

```bash
dotnet run -- verify
```

Expands save-path templates, checks whether paths exist, counts files, and computes confidence.

```bash
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

Shows what would be backed up without copying files.

```bash
dotnet run -- backup "D:\GameSaveBackups"
```

Performs the actual backup.

---

## Troubleshooting

### `verify` shows nothing

Possible causes:

* The AppID in `savepaths.sample.json` does not match any installed Steam game.
* The game is not installed.
* The JSON was not imported.
* The database was created in a different user profile or working directory context.

Run:

```bash
dotnet run -- discover
```

and confirm that the AppID from the JSON appears in the discovered games list.

---

### `Exists: False`

The path template expanded successfully, but the folder or file does not exist.

Check the expanded path printed by `verify`, then create the folder or fix the mapping.

---

### Backup dry-run shows zero items

Possible causes:

* No verified path has `Exists: True`.
* Confidence is below the backup threshold.
* The imported mapping does not match an installed Steam AppID.
* The save folder exists but contains no files.

---

### SQLite database location

The database is stored at:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

It is a normal local SQLite file. No database server is required.
