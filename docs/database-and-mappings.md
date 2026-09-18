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

Do not publish migration, rollback, corruption-recovery, or repair instructions
yet. That work is blocked as DOC-011 until DATA-001 defines supported schema
compatibility, backups, failure modes, and recovery ownership. Use copies of the
database for investigation and do not edit a user's only copy.

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
