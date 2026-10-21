# Safety Model

This guide owns the user-data invariants. Security reporting and threat scope
belong to the [security policy](../SECURITY.md).

## Required invariants

1. **Preview before execution.** Transfer, restore, cleanup, and sync first
   produce a plan or dry run. Execution is a separate confirmed action.
2. **Copy, never move.** Transfer, backup, restore, and sync do not remove the source.
3. **No silent overwrite.** Existing targets are skipped by default.
4. **Backup before supported overwrite.** Transfer and restore overwrite only
   after the existing target has been backed up and recorded with SHA-256 data.
   A failed safety backup refuses the overwrite.
5. **Path containment.** Planning and execution reject traversal, rooted
   relative paths, and writes outside the intended root.
6. **Backup-run synchronization only.** Providers synchronize completed backup
   runs, never live save directories.
7. **Immutable run content.** Remote payloads and manifests are create-only.
   Only `.gamesave-sync/sync-log.json` uses the separate mutable metadata operation.
8. **Manifest last.** A remote folder is not a complete backup run until its
   manifest is present. Interrupted folders remain incomplete.
9. **No automatic conflict resolution.** Same-name runs with different manifest
   identities are reported and not copied.
10. **Auditable outcomes.** Operations report copied, skipped, blocked, incomplete,
    and failed items and record executed runs in SQLite where applicable.
11. **Copied is not verified.** A finished transfer claims only that the copy
    ran. A run is described as verified in sync only after both sides have been
    read again and their manifest identities agree. Revalidation reuses the
    provider's own dry-run comparison: it copies, moves, deletes, overwrites,
    and repairs nothing, it can be cancelled, and neither its failure nor its
    cancellation alters or removes the transfer result already recorded.
    Locally listed backup runs start unverified. Payload verification hashes
    every file the manifest lists and fails on content the manifest does not
    list or on a link inside the run folder; archive entries are hashed no
    further than their declared size and the import bounds. A run that cannot
    be read is reported as unverified, not as tampered.
12. **All-or-nothing schema migration.** Pending migrations run in one
    `BEGIN IMMEDIATE` transaction with a single commit; any failure rolls back
    every step and leaves the previous version recorded. Before migrating, an
    online SQLite snapshot is written to `%LOCALAPPDATA%\GameSave\backups\` as a
    manual recovery point; it excludes `protected_sync_secrets`. Corrupted or
    locked databases (`SQLITE_BUSY`) refuse migration before any mutation.
13. **Untrusted mappings stay untrusted.** Harvested, AI-assisted, heuristic,
    and unapproved JSON-import candidates are stored `Pending` and disabled, and
    a re-import from those sources may update only rows that are still
    `Pending`: it never rewrites an approved, rejected, needs-fix, or curated
    mapping. `import --approve` is the one explicit approval path; the audited
    rule lives in `SavePathMappingWriter.cs`. Invalid candidates (unknown
    platform or path kind, non-numeric or zero AppID) are dropped, not stored.
14. **A compressed run exists only verified.** A new run is packed into a
    temporary container that is read back and hashed against its manifest
    before it is renamed into place; an existing file of that name is never
    replaced, and any failure leaves the run as a folder run. Restoring from a
    container extracts it into a private staging copy under the import bounds
    and verifies every file before any live file is replaced.
15. **Unattended runs are opt-in and refuse rather than guess.** Nothing runs
    unattended until the user saves a scheduled backup, or schedules a saved
    sync profile, from a clean preview. The app registers, updates, or removes
    a job's Windows task only when the user saves, applies a schedule, or
    removes the job; reading task status never changes a task, and loading the
    app, importing a profile, or migrating the database never creates or
    repairs one. A task runs as the user who saved it, only while that user is
    signed in, with least privilege and no stored password. Removing a job
    removes its task first, keeps the job if Windows refuses, and never
    deletes backups, remote runs, or History. Every run repeats the preview's
    checks and refuses without writing when one fails. A scheduled backup
    checks the destination (never created), the game, the profile, and a clean
    manual backup preview; it is a manual backup, so it only adds a new run. A
    scheduled sync reads its saved profile again and checks that the profile
    still exists, the local backup base exists, the provider can run
    unattended (SFTP never can: its credentials are session-only), the stored
    sign-in exists, the provider reports healthy, and a fresh preview has no
    errors; it is the ordinary sync engine, so upload and download stay
    create-only and conflicts are reported and skipped. One run per backup job,
    and one run per sync profile whatever job started it, holds an OS file lock
    that a crash releases. The headless host replaces the interactive Google
    Drive and OneDrive sign-ins with refusals, so an unattended run never opens
    a browser or prompts; stored tokens stay in the DPAPI secret store and only
    refresh silently. Every outcome, refusals included, is recorded in History;
    an executed sync is recorded once, by the engine, under the profile's name.
16. **Installing and uninstalling never touch user data.** The release
    package installs the program files for the current user only, under
    `%LOCALAPPDATA%\Programs\Game Save Manager`, without elevation, and the
    packaging script never starts the application. Neither the installer nor
    the uninstaller reads, writes, or deletes anything under
    `%LOCALAPPDATA%\GameSave\` (database, backups, settings, DPAPI secrets,
    scheduled-run lock files). An upgrade replaces program files in place at
    the same path, so scheduled tasks keep pointing at the installed program;
    the uninstaller removes only what it installed and tells the user where
    the data still is.
17. **Updates are opt-in, verified before anything runs, and never applied
    in place by the app.** The app contacts the release feed only when the
    user switched the startup check on or pressed Check now, and then with one
    anonymous HTTPS GET that carries no account, token or identifier; the
    headless `--run-job`/`--run-sync` host never checks. Only a strictly newer
    version is offered. A downloaded installer is written to
    `%LOCALAPPDATA%\GameSave\updates\` as a `.partial` file, hashed while
    written, and kept only when its SHA-256 matches the digest published with
    the release; a mismatched, truncated or cancelled file is deleted and the
    reason shown. Install now exists only for a verified file, starts the
    installer's own wizard and closes the app; the app never copies,
    overwrites or deletes anything in its own install folder, and the
    `updates\` directory is the only place in the data folder it touches,
    where it may delete only its own `*.partial` files older than a day.

## What can be deleted

Backup cleanup is the only user-facing feature that deletes user backup content.
It requires a preview and confirmation and can remove only recognized,
manifest-bearing run directories inside the application backup base, and the
run archives (`.zip`, `.7z`) there. It does not delete live saves, remote
backup runs, or custom-destination backups.

Other delete operations have different ownership and do not delete saves or
backup runs:

- deleting a saved remote profile removes its configuration and owned protected secrets;
- deleting a manual-backup preset removes only that preset;
- Google Drive Disconnect removes the selected profile's local OAuth token;
- WebDAV Forget password removes the selected profile's stored password only;
- failed Google Drive downloads may remove only the unique temporary file created
  by that download;
- packing a compressed backup run removes that run's own staged, manifest-less
  folder, and only after the container holding the same files has been read
  back and verified; restoring from a container removes its private staging copy;
- ZIP import and similar operations may clean up only their own internal temporary files.
  Orphaned working folders (`.staging_<32 hex>`, `.download_<32 hex>`,
  `.export_<32 hex>` and `.export_<32 hex>.tmp`) are purged once per process,
  only when nothing inside them has been written for over an hour; any other
  name, including a real run that starts with a dot, is never touched;
- the update check may remove only `*.partial` files in
  `%LOCALAPPDATA%\GameSave\updates\` that have not been written to for a day,
  plus the one partial file of a download that failed or was cancelled; the
  verified installer it keeps there is never deleted by the app.

## Integrity and confidentiality

SHA-256 manifests detect changes relative to the stored manifest. They do not
encrypt data, authenticate an author, or protect against an attacker who can
replace both the file and manifest. Backup folders, compressed runs, and ZIP
exports are not application-encrypted. Protect them with filesystem permissions, disk encryption,
remote access control, and independent retention.

Google Drive and Local Folder synchronization do not overwrite remote or local
runs. SFTP protects transport when SSH and the accepted host key are trustworthy;
at-rest protection remains the server operator's responsibility.

## Trust boundaries

- Only mappings with `Approved` review status participate in trusted transfer behavior.
- SFTP credentials and key passphrases are session-only.
- Google tokens use the protected secret store; ordinary profile rows contain
  only non-secret configuration and display metadata.
- External provider errors are reduced to sanitized categories before reaching the UI.

If a change would weaken one of these rules, treat it as a security-sensitive
design change and follow [CONTRIBUTING.md](../CONTRIBUTING.md).
