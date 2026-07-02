# How To Harvest PCGamingWiki Save Paths

This guide explains how to use the developer-only PCGamingWiki harvester to build save-path database entries from Steam AppIDs.

The harvester is intended for **project maintainers/developers**, not normal end users. The final application should ship with a prepared database. Normal users should not need to run PCGamingWiki harvesting commands.

---

## Purpose

The PCGamingWiki harvester helps build a larger local save-path database by:

1. Taking one or more Steam AppIDs.
2. Querying PCGamingWiki through its API.
3. Resolving the PCGamingWiki page for each AppID.
4. Downloading the page wikitext.
5. Extracting likely save-path candidates.
6. Saving raw and extracted data under `/External/Titles`.
7. Importing extracted mappings into the local SQLite database as **disabled by default**.

Disabled-by-default means the harvested data is stored, but it is not trusted yet. A developer should review the mapping before enabling it.

---

## Important safety and legal notes

PCGamingWiki is a community-run project. Use its API politely.

Rules for this project:

* Use a clear custom User-Agent with contact information.
* Keep requests below PCGamingWiki's published limit.
* Use serial requests, not aggressive parallel scraping.
* Cache downloaded data locally.
* Do not bypass Cloudflare or bot protections.
* Do not repeatedly retry blocked requests.
* Do not upload user save files.
* Keep source URL and license/provenance metadata.
* Treat PCGamingWiki data as attribution/licensing-sensitive.

The harvester should be used only for building and reviewing the project database.

---

## Current recommended API route

Use the MediaWiki Cargo API route:

```text
Steam AppID
→ cargoquery Infobox_game.Steam_AppID HOLDS "<appid>"
→ get PageID and page name
→ action=parse&pageid=<pageid>&prop=wikitext
→ extract Save game data location section
→ save extracted candidates
```

Do not rely on the Redirect API as the primary route. In testing, the Redirect API could return Cloudflare `403 Forbidden` responses from .NET, while Cargo API lookups worked.

---

## Output location

The recommended output folder is:

```text
External/Titles
```

Example generated structure:

```text
External/Titles/
├─ index/
│  └─ steam-appids.input.json
├─ 12345-Some_Game/
│  ├─ metadata.json
│  ├─ raw.wikitext
│  └─ savepaths.extracted.json
```

File meanings:

```text
metadata.json
```

Stores page ID, page name, Steam AppIDs, source URL, source license, hash, and harvest timestamp.

```text
raw.wikitext
```

Stores the raw PCGamingWiki page wikitext for audit/debug/re-extraction.

```text
savepaths.extracted.json
```

Stores extracted save-path candidates in the same JSON format used by the save-path import pipeline.

---

## Database location

The SQLite database is stored at:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

The harvester writes to these tables:

```text
game_titles
external_pcgamingwiki_pages
external_harvest_runs
save_path_mappings
```

Auto-extracted mappings are inserted into:

```text
save_path_mappings
```

with:

```text
enabled = 0
```

That means normal `verify` and `backup` commands will ignore them until reviewed and enabled.

---

## Required command format

Use this User-Agent format:

```text
clientname/version (contact URL; contact email) framework/version
```

Example:

```text
SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0
```

Avoid generic User-Agents.

Good:

```text
SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0
```

Bad:

```text
.NET HttpClient
```

Bad:

```text
Mozilla/5.0
```

Bad:

```text
SaveGameManager
```

---

## Harvest one Steam AppID

Run this from the project folder containing the `.csproj` file:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 674020
```

Expected successful output:

```text
Resolving PCGamingWiki page for Steam AppID 674020 using Cargo API...
Resolved AppID 674020 -> <PageName> (<PageID>)
[1/1] <PageName> (674020): <number> mapping(s)

PCGamingWiki harvest finished:
 - AppIDs requested: 1
 - Titles processed: 1
 - Titles failed/missing: 0
 - Mappings extracted: <number>
```

If `Mappings extracted` is `0`, the page may still have useful data, but the extractor may not understand that page format yet. Check the generated `raw.wikitext` manually.

---

## Harvest several AppIDs directly

You can pass multiple AppIDs:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 413150 674020 1245620
```

This will process each AppID one by one.

---

## Harvest from an AppID file

Create a file:

```text
appids.txt
```

Example:

```text
413150
674020
1245620
108710
```

Then run:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" appids.txt
```

The command also accepts comma-separated or space-separated values.

Example:

```text
413150,674020,1245620
```

---

## Harvest installed Steam games

This command first discovers installed Steam games locally, then harvests PCGamingWiki data for those installed AppIDs:

```bash
dotnet run -- pcgw-harvest-installed External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 10
```

The last argument limits the number of installed games to harvest.

Example:

```text
10
```

means only the first 10 discovered installed AppIDs are harvested.

Use this for safe testing before larger runs.

---

## Recommended test order

Start small.

First test one known AppID:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 674020
```

Then test a few AppIDs:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 413150 674020 1245620
```

Then test installed games:

```bash
dotnet run -- pcgw-harvest-installed External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 10
```

Then test a file:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" appids.txt
```

Do not start with thousands of AppIDs until the small tests produce correct output.

---

## Rate limiting

The project should use conservative settings:

```text
Requests per minute: 20
Pause every requests: 100
Pause duration: 1 minute
Serial requests only
```

This is intentionally below PCGamingWiki's published 30 requests/minute limit.

Approximate timing:

```text
1,000 requests at 20/minute ≈ 50 minutes
10,000 requests at 20/minute ≈ 8.3 hours
54,556 requests at 20/minute ≈ 45.5 hours
```

If also pausing 1 minute every 100 requests, add more time.

Do not run full-scale harvests repeatedly.

---

## Review extracted mappings

Open the SQLite database in DB Browser for SQLite or another SQLite tool.

Useful query:

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

Auto-extracted mappings should show:

```text
enabled = 0
```

This is expected.

---

## Enable a reviewed mapping

After manually checking a mapping, enable it:

```sql
UPDATE save_path_mappings
SET enabled = 1,
    priority = 40,
    notes = COALESCE(notes, '') || ' Reviewed manually.'
WHERE id = 123;
```

Replace:

```text
123
```

with the real mapping ID.

After enabling, normal verification can use it:

```bash
dotnet run -- verify
```

---

## Verify enabled mappings

After reviewing and enabling mappings, run:

```bash
dotnet run -- verify
```

The verifier only checks mappings for installed Steam games.

A mapping will not appear in verification if:

* the game is not installed,
* the Steam AppID does not match an installed manifest,
* the mapping is still disabled,
* the platform is not `windows`,
* the database is in a different user profile.

---

## Dry-run backup after review

Only after verification looks correct, test backup without copying files:

```bash
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

A path is included in backup only if it is verified and passes the confidence threshold.

The dry-run should show:

```text
Copied: False
```

No files should be copied during dry-run.

---

## Real backup after dry-run

Only after dry-run output looks correct, run:

```bash
dotnet run -- backup "D:\GameSaveBackups"
```

Check the backup folder after completion.

---

## Troubleshooting

### `HTTP 403 Forbidden`

A `403` means the request was blocked.

Possible causes:

* bad or generic User-Agent,
* blocked IP,
* too much traffic,
* Cloudflare challenge,
* using the Redirect API instead of Cargo API.

Do not retry aggressively.

Use Cargo API lookup as the primary route. If Cargo API also returns `403`, stop and contact PCGamingWiki through their preferred contact channel.

---

### `HTTP 429 Too Many Requests`

A `429` means you hit the rate limit.

Stop the run, wait, and lower the request rate.

Recommended response:

```text
RequestsPerMinute = 10
```

or lower for the next run.

---

### `no PCGamingWiki page found`

Possible causes:

* the Steam AppID is not present in PCGamingWiki,
* the game page has no Steam AppID in the PCGamingWiki infobox,
* the AppID belongs to DLC/tool/demo/music instead of a normal game,
* the Cargo query returned no rows.

This is not always an error. Store it as missing and review later.

---

### `0 mapping(s)` extracted

This means the page was resolved and downloaded, but no save-path candidates were extracted.

Possible causes:

* the page has no save data section,
* the section uses a format the extractor does not understand yet,
* the paths use PCGamingWiki templates not yet mapped by the extractor,
* the game stores saves in Steam Cloud only or has unknown location.

Check:

```text
External/Titles/<pageId>-<PageName>/raw.wikitext
```

and improve `PcgwSavePathExtractor.cs` if needed.

---

### `verify` shows nothing after harvest

This is expected if mappings are still disabled.

Check:

```sql
SELECT id, steam_app_id, game_name, path_template, enabled
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted';
```

If `enabled = 0`, review and enable the mapping manually.

---

### Database does not contain harvested mappings

Check that you are using the same Windows user account and database path:

```text
C:\Users\<YourUser>\AppData\Local\GameSave\gamesave.db
```

Also check that `Mappings extracted` was greater than zero.

---

### Output folder is empty

Check that the command is run from the project folder and that the output path is valid:

```text
External/Titles
```

The folder should contain:

```text
index/
<pageId>-<PageName>/
```

---

## What to commit

Usually commit source code and help files.

Do not commit large harvested raw datasets unless the project has made a deliberate licensing/storage decision.

Usually do not commit:

```text
External/Titles/*/raw.wikitext
External/Titles/*/savepaths.extracted.json
External/Titles/*/metadata.json
```

unless this repository is specifically meant to store the curated database.

Consider adding generated harvest output to `.gitignore` while the format is unstable.

Example:

```gitignore
Manager/GameSaves/External/Titles/
```

If you later create a reviewed canonical dataset, store it separately from raw harvested data.

---

## Developer workflow

Recommended workflow:

```text
1. Harvest one AppID.
2. Check generated raw.wikitext.
3. Check savepaths.extracted.json.
4. Check save_path_mappings rows in SQLite.
5. Review mapping manually.
6. Enable approved mapping.
7. Run verify.
8. Run backup-dry-run.
9. Improve extractor if extraction was wrong.
10. Repeat with a small batch.
```

Do not jump directly to a full PCGamingWiki-scale harvest until:

* one-AppID harvest works,
* installed-games harvest works,
* extraction quality is acceptable,
* disabled/enabled review flow is clear,
* rate limiting is confirmed,
* generated data storage policy is decided.

---

## Useful commands summary

Harvest one AppID:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 674020
```

Harvest multiple AppIDs:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 413150 674020 1245620
```

Harvest from file:

```bash
dotnet run -- pcgw-harvest-appids External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" appids.txt
```

Harvest installed games:

```bash
dotnet run -- pcgw-harvest-installed External/Titles "SaveGameManager/0.1 (https://github.com/user; user@mail.com) .NET/8.0" 10
```

Review extracted mappings:

```sql
SELECT id, steam_app_id, game_name, platform, path_template, enabled
FROM save_path_mappings
WHERE source_name = 'PCGamingWiki-AutoExtracted'
ORDER BY game_name;
```

Enable reviewed mapping:

```sql
UPDATE save_path_mappings
SET enabled = 1,
    priority = 40,
    notes = COALESCE(notes, '') || ' Reviewed manually.'
WHERE id = 123;
```

Verify:

```bash
dotnet run -- verify
```

Dry-run backup:

```bash
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

---

## Final note

The PCGamingWiki harvester is a database-building tool, not a user feature.

Normal users should use:

```bash
dotnet run -- discover
dotnet run -- verify
dotnet run -- backup-dry-run "D:\GameSaveBackups"
```

Developers use:

```bash
dotnet run -- pcgw-harvest-appids ...
```

to expand and improve the project database over time.
