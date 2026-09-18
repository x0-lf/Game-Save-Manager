# Database and Save-Path Mappings

This guide owns the current database location, mapping trust lifecycle, and CLI
overview. Exact command syntax is authoritative in `GameSaves -- help`.

## Current database

The desktop App, CLI, and Reviewer use:

```text
%LOCALAPPDATA%\GameSave\gamesave.db
```

The database contains mapping and review data, non-secret remote profiles,
protected secret BLOBs, manual-backup presets, transfer and sync history, and
catalog or harvesting state used by the current tools.

Schema upgrades are managed automatically via the versioned migration engine
(`ISchemaMigrator`, DATA-003). Upgrades take atomic pre-migration backups before
applying changes and safely roll back on failure. Direct manual editing of
production databases is strongly discouraged; use copies of the database for
investigation.

## Mapping trust lifecycle

1. The CLI discovers or harvests a candidate with source and license provenance.
2. Automated candidates enter the database disabled and `Pending`.
3. A developer reviews the candidate in `GameSaves.Reviewer` against its source.
4. Incorrect candidates become `Rejected`; promising but unsafe candidates
   become `NeedsFix`.
5. Only a checked candidate becomes `Approved` and enabled.
6. Verification expands the template for the current platform and installed game.
7. Transfer and backup flows trust only approved mappings and still apply path
   validation, preview, and execution-time containment.

Approval is a trust boundary, not a statement that every machine has the path.
A correct mapping may verify as absent when the game, platform, profile, edition,
or save state differs.

## Curated mapping distribution (DATA-002)

Approved save-path mappings are versioned in the repository and distributed directly
within the compiled application assembly as an embedded resource (`curated-mappings.json`).
Private user state (custom mappings, profile settings, encrypted secret BLOBs, and
backup/transfer history) strictly remains outside Git in the local SQLite database.

### Seeding on startup and first run

When the desktop application or CLI boots for the first time or creates a new database:

1. `SchemaInitializingAppDatabasePathProvider` ensures all tables and review columns exist.
2. `CuratedMappingSeeder` loads the versioned curated dataset from the assembly manifest
   (`GameSaves.Infrastructure.Resources.curated-mappings.json`) and merges it into the
   database within an isolated transaction.
3. Matching records in the `game_titles` table are populated or refreshed with canonical titles.

### Merge precedence rules

The seeder applies deterministic precedence rules to guarantee that project updates never
destroy or overwrite local user customizations:

| Condition | Database State | Action |
| --- | --- | --- |
| **New Curated Mapping** | Missing from local database | Inserted with `review_status = 'Approved'`, `enabled = 1`, and `source_name = 'CuratedSeed'`. |
| **User Custom Mapping** | `source_name != 'CuratedSeed'` | **Preserved untouched**. The seeder skips the entry entirely and never overwrites user custom paths. |
| **User State Modification** | `source_name == 'CuratedSeed'` AND (`enabled == 0` OR `review_status != 'Approved'`) | **User choice preserved**. The seeder skips re-enabling or re-approving mappings the user disabled or altered. |
| **Canonical Metadata Update** | `source_name == 'CuratedSeed'` AND `enabled == 1` AND `review_status == 'Approved'` | **Updated**. If notes, license, source URL, or priority changed in the curated release, metadata is updated without resetting user data. |
| **Unchanged Curated Mapping** | Identical to bundled release | **No-op**. Marked unchanged with zero redundant writes. |

### Manual and CLI seeding

Users and maintainers can re-apply or preview curated mapping seeding using the CLI:

```powershell
# Initialize database and seed bundled curated mappings
dotnet run --project Manager/GameSaves/GameSaves.csproj -- init-db

# Seed or refresh curated mappings from bundled assembly resource
dotnet run --project Manager/GameSaves/GameSaves.csproj -- seed-curated

# Seed mappings from an external custom JSON seed file
dotnet run --project Manager/GameSaves/GameSaves.csproj -- seed-curated path/to/custom-seed.json
```

## Schema migration, backup, and rollback engine (DATA-003)

The database schema evolves through versioned, deterministic migrations defined in
`GameSaves.Infrastructure.Data.Migrations`. Applied migrations are recorded in the
`schema_migrations` table (`id`, `name`, `applied_utc`).

### Migration safety workflow

When migrations are executed (automatically on application startup via
`SchemaInitializingAppDatabasePathProvider`, or manually via CLI):

1. **Pre-flight integrity verification:** Executes `PRAGMA quick_check;`. If the database
   is corrupted or locked (`SQLITE_BUSY`), migration is aborted immediately without modifying state.
2. **Pending migration detection:** Compares applied migrations in `schema_migrations` with
   registered `ISchemaMigration` implementations. If no migrations are pending, execution
   completes with zero disk writes.
3. **Pre-migration safety snapshot:** If migrations are pending, an online backup is captured
   using the SQLite online backup API to:
   ```text
   %LOCALAPPDATA%\GameSave\backups\gamesave-pre-migration-{timestamp}-{guid}.db
   ```
   If the safety backup fails, migration is rejected immediately.
4. **Transactional migration execution:** Each pending migration is executed inside an
   isolated transaction. If an exception occurs:
   - The active transaction is rolled back immediately.
   - Database connection pools are cleared.
   - The pre-migration backup snapshot is restored over the database file, guaranteeing
     zero partial schema state or data corruption.
5. **Backup retention:** Backup snapshots exceeding the default retention limit (10) are
   pruned to avoid unbounded disk consumption.

### CLI migration commands

Maintainers and CLI users can inspect, plan, and execute database migrations:

```powershell
# Show current schema version and pending migrations
dotnet run --project Manager/GameSaves/GameSaves.csproj -- migrate-status

# Generate a dry-run migration plan with integrity check (read-only, no mutations)
dotnet run --project Manager/GameSaves/GameSaves.csproj -- migrate-dry-run

# Run pending migrations with automatic pre-migration backup snapshot
dotnet run --project Manager/GameSaves/GameSaves.csproj -- migrate

# Run against an explicit custom database file
dotnet run --project Manager/GameSaves/GameSaves.csproj -- migrate path/to/gamesave.db
```

## Importing game titles and save paths from JSON (OBS-014)

Maintainers and users can import new game titles and candidate save path mappings from
external JSON files using the `IMappingImportService` (`MappingImportService`).

### Supported JSON formats

The import engine accepts:

1. **Document Object format:**
   ```json
   {
     "schemaVersion": 1,
     "titles": [
       {
         "steamAppId": "400",
         "title": "Portal",
         "platformHint": "windows",
         "sourceName": "CommunitySource"
       }
     ],
     "mappings": [
       {
         "steamAppId": "400",
         "gameName": "Portal",
         "platform": "windows",
         "pathTemplate": "%LOCALAPPDATA%/Portal/Saves",
         "pathKind": "Directory",
         "sourceName": "CommunitySource",
         "priority": 100
       }
     ]
   }
   ```
2. **Flat mappings array:** A JSON array of mapping items directly at root.
3. **Flat titles array:** A JSON array of title items directly at root.
4. **Flexible casing:** Property names can use either camelCase (`steamAppId`, `gameName`, `pathTemplate`) or snake_case (`steam_app_id`, `game_name`, `path_template`). App IDs can be strings or integers.

### Validation & trust rules

- **Required fields:** `steamAppId` and `title` for titles; `steamAppId`, `platform`, and `pathTemplate` for mappings.
- **Platform allowlist:** `windows`, `linux`, `macos`, `steamdeck` (case-insensitive).
- **Default Pending status:** In accordance with the project safety model, all imported candidate mappings default to `review_status = 'Pending'` and `enabled = 0`. They remain disabled and excluded from transfer/backup execution until reviewed in `GameSaves.Reviewer` or imported with explicit administrative approval (`--approve`).
- **Approval preservation:** If an existing mapping in the database is already `Approved`, re-importing metadata does not downgrade its status unless the `pathKind` changed (which invalidates the previous review).
- **Duplicate detection:** Duplicate mappings matching `(steam_app_id, platform, path_template)` and duplicate titles matching `steam_app_id` are detected, leaving existing records unchanged and reporting exact metrics.

### CLI import commands

```powershell
# Import mappings or titles from JSON (defaults to Pending review and disabled)
dotnet run --project Manager/GameSaves/GameSaves.csproj -- import candidates.json

# Import and auto-approve trusted mappings
dotnet run --project Manager/GameSaves/GameSaves.csproj -- import curated.json --approve

# Using the import-json alias
dotnet run --project Manager/GameSaves/GameSaves.csproj -- import-json mappings.json
```

## CLI overview

The `GameSaves` project owns:

- Steam root, library, installed-game, and profile discovery;
- mapping import, expansion, verification, and backup checks;
- Steam catalog fetch and harvest-queue management; and
- controlled PCGamingWiki harvesting and mapping import.

Inspect current commands from the repository root:

```powershell
dotnet run --project Manager/GameSaves/GameSaves.csproj -- help
```

The harvesting commands use the network and must follow PCGamingWiki rate,
licensing, identification, and stop rules. They are developer workflows, not
normal end-user setup.

## Project-local procedures

- [Test mappings](../Manager/GameSaves/Help/HowToTest.md)
- [Harvest mappings](../Manager/GameSaves/Help/HowToHarvest.md)
- [Harvest multiple batches](../Manager/GameSaves/Help/HowToHarvestMultiple.md)
- [Review harvested mappings](../Manager/GameSaves.Reviewer/Help/HowToReviewMappings.md)

Keep these guides beside their executables. Update them when command behavior or
the review workflow changes, while leaving exact CLI syntax to built-in help.
