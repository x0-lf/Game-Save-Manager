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
