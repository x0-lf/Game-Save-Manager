# How To Harvest Multiple PCGamingWiki Batches

This guide explains how to harvest Steam save-path data from PCGamingWiki in multiple controlled batches.

The goal is:

```text
Steam catalog
→ harvest queue
→ batch-001.txt
→ PCGamingWiki harvest
→ review extracted mappings
→ batch-002.txt
→ repeat
```

Do **not** pass the full `steam-appids-game.txt` file directly into the PCGamingWiki harvester. The full Steam catalog can contain hundreds of thousands of AppIDs, and harvesting must be done slowly in controlled batches.

---

## Current project folders

Steam catalog output:

```text
External/SteamCatalog/
├─ steam-appids-game.txt
├─ steam-catalog-game.json
├─ batch-001.txt
├─ batch-002.txt
└─ ...
```

PCGamingWiki harvest output:

```text
External/Titles/
├─ index/
│  └─ steam-appids.input.json
├─ <pageId>-<PageName>/
│  ├─ metadata.json
│  ├─ raw.wikitext
│  └─ savepaths.extracted.json
└─ ...
```

SQLite database:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

---

## Important rule

Run **one PCGamingWiki harvest batch at a time**.

Do not run several 1000-AppID harvests in parallel. PCGamingWiki is the slow and sensitive part of this pipeline, so keep harvesting serial and polite.

Recommended rate:

```text
20 requests/minute
pause every 100 requests
pause duration: 1 minute
custom User-Agent
serial requests only
```

A 1000-AppID batch can take several hours depending on how many AppIDs resolve to PCGamingWiki pages and how many pages need wikitext downloads.

---

## Step 1: Make sure the Steam catalog exists

You only need to fetch the full Steam catalog occasionally.

```powershell
dotnet run -- steam-catalog-fetch External/SteamCatalog games 0
```

This creates or refreshes:

```text
External/SteamCatalog/steam-catalog-game.json
External/SteamCatalog/steam-appids-game.txt
```

---

## Step 2: Create the harvest queue

Run this after fetching the Steam catalog:

```powershell
dotnet run -- steam-catalog-queue-missing
```

Expected output:

```text
Steam catalog harvest queue updated:
 - New pending games queued: <number>
```

If it says `0`, that can be fine. It means there are no new missing games to add to the queue, or they were already queued before.

The queue avoids repeatedly exporting the same first 1000 AppIDs.

---

## Step 3: Export the first batch

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-001.txt 1000
```

Expected output:

```text
Steam catalog queue export finished:
 - AppIDs exported: 1000
 - Output: External/SteamCatalog/batch-001.txt
```

Check the batch file:

```powershell
Get-Content External/SteamCatalog/batch-001.txt -TotalCount 20
```

Count lines:

```powershell
(Get-Content External/SteamCatalog/batch-001.txt).Count
```

Expected:

```text
1000
```

When this export command runs, those 1000 AppIDs are marked as:

```text
Exported
```

in the harvest queue.

---

## Step 4: Harvest batch 001

Run:

```powershell
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-001.txt
```

Expected early output:

```text
Steam AppIDs loaded: 1000
```

Then the harvester should process AppIDs one by one.

During the run, it should create folders under:

```text
External/Titles/
```

Example:

```text
External/Titles/
├─ 31535-Stardew_Valley/
│  ├─ metadata.json
│  ├─ raw.wikitext
│  └─ savepaths.extracted.json
```

---

## Step 5: If batch 001 is interrupted

If the PCGamingWiki harvest crashes, your PC shuts down, or the terminal is closed, do **not** export batch 002 immediately.

First rerun the same batch file:

```powershell
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-001.txt
```

This is safer because `batch-001.txt` has already been marked as `Exported`.

The database import uses upsert-style behavior, so rerunning the same batch should not create duplicate mappings if the schema conflict rules are correct.

---

## Step 6: After batch 001 finishes

Inspect the output.

Check generated folders:

```powershell
Get-ChildItem External/Titles
```

Check extracted JSON files:

```powershell
Get-ChildItem External/Titles -Recurse -Filter savepaths.extracted.json
```

Check how many auto-extracted mappings are in SQLite:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted';
```

Check disabled mappings:

```sql
SELECT COUNT(*)
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
  AND enabled = 0;
```

Auto-extracted mappings should be disabled by default:

```text
enabled = 0
```

That is expected. They need review before normal `verify` and `backup` commands should trust them.

---

## Step 7: Export batch 002

Only after batch 001 finishes, export the next batch:

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-002.txt 1000
```

Then harvest it:

```powershell
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-002.txt
```

Because batch 001 was already marked as `Exported`, batch 002 should contain the next 1000 pending AppIDs.

---

## Step 8: Repeat for later batches

Use this pattern:

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-003.txt 1000
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-003.txt
```

Then:

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-004.txt 1000
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-004.txt
```

And so on.

Recommended naming:

```text
batch-001.txt
batch-002.txt
batch-003.txt
batch-004.txt
```

Use three digits so the files sort cleanly.

---

## Useful PowerShell checks

Show first 20 AppIDs in a batch:

```powershell
Get-Content External/SteamCatalog/batch-001.txt -TotalCount 20
```

Count AppIDs in a batch:

```powershell
(Get-Content External/SteamCatalog/batch-001.txt).Count
```

List all batch files:

```powershell
Get-ChildItem External/SteamCatalog/batch-*.txt
```

Count generated PCGamingWiki title folders:

```powershell
(Get-ChildItem External/Titles -Directory).Count
```

Count extracted save-path JSON files:

```powershell
(Get-ChildItem External/Titles -Recurse -Filter savepaths.extracted.json).Count
```

---

## Useful SQLite queue checks

Pending AppIDs still waiting for export:

```sql
SELECT COUNT(*)
FROM steam_catalog_harvest_queue
WHERE queue_status = 'Pending';
```

Already exported AppIDs:

```sql
SELECT COUNT(*)
FROM steam_catalog_harvest_queue
WHERE queue_status = 'Exported';
```

Preview pending queue:

```sql
SELECT steam_app_id, name, queue_status
FROM steam_catalog_harvest_queue
WHERE queue_status = 'Pending'
ORDER BY CAST(steam_app_id AS INTEGER)
LIMIT 50;
```

Preview exported queue:

```sql
SELECT steam_app_id, name, queue_status
FROM steam_catalog_harvest_queue
WHERE queue_status = 'Exported'
ORDER BY CAST(steam_app_id AS INTEGER)
LIMIT 50;
```

---

## Important current limitation

At this stage, the queue marks AppIDs as:

```text
Exported
```

when they are written to a batch file.

The queue may not yet automatically mark them as:

```text
Harvested
NoPcgwPage
ExtractedNoPaths
FailedRetryable
FailedPermanent
```

after PCGamingWiki harvesting finishes.

That means the batch files are currently your practical progress record.

Keep these files:

```text
External/SteamCatalog/batch-001.txt
External/SteamCatalog/batch-002.txt
External/SteamCatalog/batch-003.txt
```

They show which AppIDs were sent to the harvester.

A future improvement should update the queue status from inside `PcgwHarvester`.

---

## Review extracted mappings

Auto-extracted mappings are not trusted yet.

Use this query:

```sql
SELECT
    id,
    steam_app_id,
    game_name,
    platform,
    path_template,
    path_kind,
    source_name,
    source_url,
    source_license,
    notes,
    priority,
    enabled
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
ORDER BY game_name, platform, path_template;
```

A reviewed mapping can be enabled manually:

```sql
UPDATE save_path_mappings
SET enabled = 1,
    priority = 40,
    notes = COALESCE(notes, '') || ' Reviewed manually.'
WHERE id = 123;
```

Replace `123` with the real mapping ID.

---

## Verify after enabling mappings

After enabling some reviewed mappings, run:

```powershell
dotnet run -- verify
```

The verifier checks mappings only for installed Steam games.

A mapping will not appear in verification if:

```text
the game is not installed
the AppID does not match an installed Steam manifest
the mapping is still disabled
the mapping platform is not windows
the save path does not exist on this PC
```

---

## Backup after verification

Dry-run first:

```powershell
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

Only after dry-run output looks correct:

```powershell
dotnet run -- backup "D:\GameSaveBackups"
```

---

## What to commit

Commit source code and curated or intentionally preserved dataset snapshots.

Usually safe to commit:

```text
External/SteamCatalog/steam-appids-game.txt
External/SteamCatalog/steam-catalog-game.json
External/SteamCatalog/batch-001.txt
Help/HowToHarvestMultiple.md
```

Be careful with raw PCGamingWiki harvested output:

```text
External/Titles/*/raw.wikitext
External/Titles/*/savepaths.extracted.json
External/Titles/*/metadata.json
```

Those files may be large and licensing-sensitive. Commit them only if the repository is intentionally storing the harvested/curated dataset.

Do not commit:

```text
commit.txt
*.bak
local API keys
personal logs containing secrets
```

---

## When to stop

Stop harvesting if you see:

```text
HTTP 403 Forbidden
HTTP 429 Too Many Requests
Cloudflare challenge page
repeated failed requests
```

Do not retry aggressively.

For `429`, lower the request rate before continuing.

For `403` or Cloudflare challenge responses, stop and investigate the User-Agent, request pattern, or PCGamingWiki access policy.

---

## Recommended daily workflow

A safe daily workflow is:

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-002.txt 1000

dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-002.txt
```

Then inspect:

```powershell
Get-ChildItem External/Titles -Recurse -Filter savepaths.extracted.json
```

Then later:

```powershell
dotnet run -- steam-catalog-export-next External/SteamCatalog/batch-003.txt 1000

dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/nickname; user@mail.com) .NET/8.0" External/SteamCatalog/batch-003.txt
```

Keep harvesting slow, review extracted mappings, and only enable mappings after checking them.
