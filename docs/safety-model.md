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

## What can be deleted

Backup cleanup is the only user-facing feature that deletes user backup content.
It requires a preview and confirmation and can remove only recognized,
manifest-bearing run directories inside the application backup base. It does
not delete live saves, remote backup runs, or custom-destination backups.

Other delete operations have different ownership and do not delete saves or
backup runs:

- deleting a saved remote profile removes its configuration and owned protected secrets;
- deleting a manual-backup preset removes only that preset;
- Google Drive Disconnect removes the selected profile's local OAuth token;
- WebDAV Forget password removes the selected profile's stored password only;
- failed Google Drive downloads may remove only the unique temporary file created
  by that download;
- ZIP import and similar operations may clean up only their own internal temporary files.
  Orphaned working folders (`.staging_<32 hex>`, `.download_<32 hex>`,
  `.export_<32 hex>` and `.export_<32 hex>.tmp`) are purged once per process,
  only when nothing inside them has been written for over an hour; any other
  name, including a real run that starts with a dot, is never touched.

## Integrity and confidentiality

SHA-256 manifests detect changes relative to the stored manifest. They do not
encrypt data, authenticate an author, or protect against an attacker who can
replace both the file and manifest. Backup folders and ZIP exports are not
application-encrypted. Protect them with filesystem permissions, disk encryption,
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
