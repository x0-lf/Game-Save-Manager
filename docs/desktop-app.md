# Desktop Application

This guide owns desktop workflows, navigation, and UI states. Requirements and
launch instructions are in [Getting Started](getting-started.md).

## Startup and navigation

The App opens immediately and loads the Dashboard, Installed games, Profiles,
Transfer profiles inputs, Manual backup inputs, Backups, and History in the
background. Each page shows its own loading, empty, ready, warning, blocked, or
failure state instead of requiring a first manual refresh.

The navigation rail contains nine tabs:

| Tab | Purpose |
| --- | --- |
| Dashboard | Steam discovery summary and first-run actions |
| Installed games | Installed games and approved, pending, or needs-fix mapping state |
| Profiles | Detected Steam profiles and transfer source/target selection |
| Transfer profiles | Preview and execute local profile-to-profile copies |
| Manual backup | Create a new timestamped backup run |
| Backups | Inspect, verify, restore, archive, import, and clean up backup runs |
| Sync | Configure a provider and synchronize completed backup runs |
| History | Inspect executed transfer, restore, backup, cleanup, and sync results |
| Settings | Appearance, accessibility, behavior, layout, providers, data, and diagnostics |

`Ctrl+1` through `Ctrl+9` address that canonical order. Settings can reorder or
hide eligible tabs and choose the startup tab. Dashboard and Settings stay
visible. The rail can sit on the left, right, or top and can collapse to icons.

The rail's Scan or Refresh action is page-sensitive: it invokes the active
page's existing command and is absent on Settings. A page that is already busy
keeps the same command disabled in both places.

## Workspace layout and recovery

Page sections can be moved between regions, resized, collapsed, hidden, or
floated into their own windows. Entire tabs can also be detached. Closing a
detached tab window reattaches it.

Use a section menu to reset only that page. Use Settings > Layout > Reset
workspace, followed by its confirmation action, to restore all default page
layouts and reattach detached tabs. Settings also exposes hidden sections and
tabs so hiding content never makes it permanently unreachable. Saved workspace
layouts are explicit snapshots and are not applied automatically.

## Transfer profiles

1. Choose distinct source and target Steam profiles.
2. Choose the Steam userdata game folder, approved mappings, or both.
3. Build the preview. Equivalent paths are deduplicated.
4. Resolve blockers or explicitly choose to skip blocked items.
5. Review file counts, sizes, conflicts, targets, and overwrite state.
6. Confirm execution. Overwrite remains off unless explicitly enabled.

The Steam userdata path is validated independently of the mapping database.
Only approved mappings can supply additional paths. When Safe Mode overwrite is
enabled, each target is backed up before replacement; a backup failure blocks
that file.

## Manual backup

Choose a profile, installed game, source set, and destination. The destination
can be typed or selected with the native folder picker. Every execution creates
a fresh timestamped directory with mirrored source paths and a SHA-256
`manifest.json`; it does not replace a previous run.

Named presets store the destination and source choices. Applying or deleting a
preset never starts a backup and never deletes backup data. Runs written under
the application backup base appear in Backups; custom destinations remain
self-contained but are not indexed there.

## Backups and restore

Backups discovers manifest-bearing runs and displays their files, sizes, game,
profiles, and timestamp. Restore always begins with a preview and can target:

- original recorded locations;
- the matching game's userdata folder for a selected profile; or
- one approved, enabled mapping that resolves to exactly one path.

Existing files are skipped by default. An explicitly enabled restore overwrite
first backs up the current target. Hash-mismatched or missing backup files are
not restored.

ZIP export creates a self-contained archive. Import validates extraction,
rewrites manifest paths to the imported location, and never overwrites an
existing run. Cleanup is the only user-backup deletion feature; its exact
boundary is owned by the [safety model](safety-model.md).

## Sync

Select a saved or unsaved remote profile, configure Local Folder, SFTP, or
Google Drive, and run Check Connection & Sync Status. Saving or selecting a
profile does not connect, preview, or sync.

Preview identifies uploads, downloads, in-sync runs, conflicts, warnings, and
incomplete remote folders. Each copy action can be selected independently.
Execution requires confirmation, reports byte and run progress, and can be
cancelled. Conflicts, deselected items, and existing targets remain untouched.

Provider-specific authentication, controls, and limitations belong to the
[sync provider guide](sync-providers.md).

## UI state conventions

- **Loading:** the current command is disabled while work is in flight.
- **Empty:** the page explains what is missing, such as Steam, profiles, runs,
  or a selected provider, and offers the relevant next action where possible.
- **Ready:** configuration is sufficient to preview or inspect.
- **Warning:** work may proceed only where the plan remains safe.
- **Blocked:** the page states which required selection, confirmation, path,
  authentication, or invariant is missing.
- **Failure:** a sanitized message is shown; credentials, raw provider payloads,
  account IDs, and Drive object IDs are not displayed.

Screenshots are deferred until UI content and release material stabilize; see
[DOC-021](ROADMAP.md).
