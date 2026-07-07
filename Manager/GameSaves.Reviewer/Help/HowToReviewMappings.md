# How To Review PCGamingWiki Mappings

This guide explains how to use the internal PCGamingWiki mapping reviewer.

The reviewer is a developer-only Windows Forms tool used to review save-path mappings created by the PCGamingWiki harvester.

Normal users should not need this tool. Normal users should use the prepared reviewed database and the normal commands:

```powershell
dotnet run -- verify
dotnet run -- backup-dry-run "D:\GameSaveBackups"
dotnet run -- backup "D:\GameSaveBackups"
```

---

## Purpose

The PCGamingWiki harvester imports extracted mappings into the SQLite database as disabled by default:

```text
enabled = 0
source_name = PCGamingWiki-AutoExtracted
```

This is intentional. Auto-extracted mappings are useful candidates, but they should not be trusted immediately.

The reviewer exists so a developer can quickly inspect mappings and mark them as:

```text
Pending
Approved
Rejected
NeedsFix
```

This replaces manual SQL like:

```sql
UPDATE save_path_mappings
SET enabled = 1,
    priority = 40,
    notes = COALESCE(notes, '') || ' Reviewed manually.'
WHERE id = 123;
```

Instead of running one SQL query per mapping, use the reviewer window.

---

## Project location

The reviewer is a separate project next to the main CLI project:

```text
Manager/
├─ GameSaves/
│  └─ GameSaves.csproj
└─ GameSaves.Reviewer/
   ├─ GameSaves.Reviewer.csproj
   ├─ Program.cs
   ├─ MainForm.cs
   ├─ MappingReviewItem.cs
   └─ MappingReviewRepository.cs
```

This keeps the internal curation tool separate from the main end-user save manager.

---

## Database location

By default, the reviewer opens:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

This is the same SQLite database used by the main `GameSaves` CLI.

You can also pass a database path manually:

```powershell
dotnet run -- "C:\Users\MikeAbrams\AppData\Local\GameSave\gamesave.db"
```

---

## Run the reviewer

From the reviewer project folder:

```powershell
cd C:\Users\MikeAbrams\Documents\Game-Save-Manager\Manager\GameSaves.Reviewer
dotnet run
```

Or run with an explicit database path:

```powershell
dotnet run -- "C:\Users\MikeAbrams\AppData\Local\GameSave\gamesave.db"
```

---

## What the reviewer shows

The grid displays PCGamingWiki auto-extracted mappings from table:

```sql
save_path_mappings
```

filtered by:

```sql
source_name = 'PCGamingWiki-AutoExtracted'
```

Useful columns include:

```text
Id
SteamAppId
GameName
Platform
PathTemplate
PathKind
SourceName
SourceUrl
Priority
Enabled
ReviewStatus
```

You are not reviewing all Steam catalog entries.

Do not review:

```sql
steam_catalog_apps
```

Review only extracted mapping rows:

```sql
SELECT *
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted';
```

The Steam catalog may contain more than 170,000 AppIDs, but the actual review workload is only the mappings extracted by the harvester.

---

## Review statuses

### Pending

The mapping has not been reviewed yet.

Expected state:

```text
enabled = 0
review_status = Pending
```

### Approved

The mapping was checked and accepted.

Approved mappings are enabled for normal verification:

```text
enabled = 1
review_status = Approved
priority = 40
```

After approval, the normal CLI can use the mapping:

```powershell
dotnet run -- verify
```

### Rejected

The mapping is wrong or not useful.

Examples:

```text
wrong game
wrong platform
cache folder
log folder
config folder instead of save folder
broken extractor result
```

Rejected mappings stay disabled:

```text
enabled = 0
review_status = Rejected
```

### NeedsFix

The mapping looks useful, but needs manual cleanup before it can be approved.

Examples:

```text
path template is almost correct
wrong token format
missing wildcard
platform needs correction
extractor included extra text
path points to config and save data mixed together
```

Needs-fix mappings stay disabled:

```text
enabled = 0
review_status = NeedsFix
```

---

## Buttons

### Approve Selected

Approves selected rows.

This sets:

```text
enabled = 1
review_status = Approved
reviewed_utc = current timestamp
priority = selected priority value
```

Use this for mappings that look correct and useful.

---

### Reject Selected

Rejects selected rows.

This sets:

```text
enabled = 0
review_status = Rejected
reviewed_utc = current timestamp
```

Use this for obviously wrong mappings.

---

### Needs Fix

Marks selected rows as needing manual correction.

This sets:

```text
enabled = 0
review_status = NeedsFix
reviewed_utc = current timestamp
```

Use this when the data is promising but not ready.

---

### Reset Pending

Moves selected rows back to pending.

This sets:

```text
enabled = 0
review_status = Pending
reviewed_utc = NULL
review_notes = NULL
```

Use this if you approved, rejected, or marked something incorrectly.

---

### Open Source

Opens the PCGamingWiki source URL for the selected mapping.

Use this to check the original page before approving.

---

## Keyboard shortcuts

When the grid is focused:

```text
A       Approve selected
R       Reject selected
F       Mark selected as NeedsFix
O       Open source URL
Ctrl+R  Reload
```

You can select multiple rows and apply an action to all selected rows.

---

## Recommended review workflow

Start with pending mappings:

```text
Status: Pending
Limit: 1000
```

Review in this order:

```text
1. Windows mappings first
2. Known games first
3. Simple save folders first
4. Suspicious paths later
5. NeedsFix for anything that needs manual cleanup
```

Good candidates often contain:

```text
%APPDATA%
%LOCALAPPDATA%
%USERPROFILE%\Documents
{Documents}
{SavedGames}
{GameInstallPath}
{SteamRoot}\userdata\*\{AppId}\remote
```

Be careful with paths containing:

```text
config
settings
logs
cache
screenshots
crash
crash dumps
shadercache
temp
workshop
```

These may be config/cache data rather than save data.

---

## After approving mappings

Run verification from the main project:

```powershell
cd C:\Users\MikeAbrams\Documents\Game-Save-Manager\Manager\GameSaves
dotnet run -- verify
```

The verifier only checks mappings for installed Steam games.

A mapping may not appear during verification if:

```text
the game is not installed
the AppID does not match an installed Steam appmanifest
the platform is not windows
the path does not exist on this PC
the mapping is still disabled
```

---

## Dry-run backup after review

After verification looks correct, run:

```powershell
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

Check the output carefully.

Dry-run should show:

```text
Copied: False
```

Only after the dry-run looks correct, run a real backup:

```powershell
dotnet run -- backup "D:\GameSaveBackups"
```

---

## Useful SQL checks

Count pending mappings:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND COALESCE(review_status, 'Pending') = 'Pending';
```

Count approved mappings:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND review_status = 'Approved';
```

Count rejected mappings:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND review_status = 'Rejected';
```

Count needs-fix mappings:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND review_status = 'NeedsFix';
```

Show approved mappings:

```sql
SELECT
    id,
    steam_app_id,
    game_name,
    platform,
    path_template,
    path_kind,
    priority,
    enabled,
    review_status
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND review_status = 'Approved'
ORDER BY game_name, platform, path_template;
```

Show mappings still disabled but approved by mistake:

```sql
SELECT
    id,
    steam_app_id,
    game_name,
    path_template,
    enabled,
    review_status
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND review_status = 'Approved'
  AND enabled = 0;
```

Expected result:

```text
0 rows
```

---

## Important rule

Do not approve mappings blindly.

The reviewer makes review faster, but it does not make auto-extracted data automatically correct.

Use `Approved` only when the path looks like a real save location.

Use `Rejected` for clear junk.

Use `NeedsFix` when the path is close but requires manual cleanup or extractor improvement.

---

## Future improvements

Useful future reviewer features:

```text
Verify selected mapping on this PC
Show expanded path preview
Show whether path exists locally
Group mappings by game
Open local extracted JSON
Open raw PCGamingWiki wikitext
Bulk approve all selected mappings for same game
Add manual corrected path from reviewer
Export reviewed database snapshot
```

The first important future feature should be:

```text
Verify selected mapping on this PC
```

That can reuse the existing `SavePathVerifier` logic from the main project.
