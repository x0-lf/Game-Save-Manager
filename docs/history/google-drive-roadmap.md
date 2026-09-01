# Google Drive Milestone Chronology

> **Historical record — not current status.** This archive preserves the A-Z
> sequence and what was true when each milestone closed. Archived availability
> statements do not control the current [roadmap](../ROADMAP.md) or
> [provider guide](../sync-providers.md).

## A-Z chronology

| Milestone | Closed outcome |
| --- | --- |
| A | Established regression protection for discovery, transfer, backup, restore, archives, history, Local Folder, and SFTP. |
| B | Replaced the two-provider Boolean with stable `LocalFolder`, `Sftp`, `GoogleDrive`, `WebDav`, and `OneDrive` kinds while preserving old settings. |
| C | Added named non-secret remote profiles in SQLite. Saved-profile functionality is complete. |
| D | Added the provider capability catalog and UI metadata boundary. |
| E | Added the byte-oriented, platform-neutral secret-store contract. |
| F | Implemented Windows current-user DPAPI storage backed by encrypted SQLite BLOBs. |
| G | Documented private Google Cloud development setup and repository ignore protections. |
| H | Added official Google packages only to Infrastructure and pinned SDK-boundary tests. |
| I | Added allowlisted Google connection settings with no persisted token values. |
| J | Implemented installed-app OAuth in the system browser with loopback, PKCE, `drive.file`, protected tokens, and silent restore. |
| K | Added Connect, Reconnect, Disconnect, revocation handling, and connection state. |
| L | Added the visible `My Drive/GameSave Manager Backups` root with authoritative ID identity and confirmed replacement. |
| M | Split create-only backup content from allowlisted mutable sync metadata. |
| N | Added normalized Drive path resolution, pagination, ambiguity rejection, safe parent creation, and validated ID caching. |
| O | Added validation-only remote plumbing with sanitized error mapping and no probe mutation. |
| P | Added run discovery, bounded text reads, create-only text, and sync-log metadata operations; live accepted 2026-08-03. |
| Q | Added recursive, paginated, read-only file listing with collision and cycle rejection; live accepted 2026-08-13. |
| R | Added streamed, resumable, create-only uploads with manifest-last engine ordering; live accepted 2026-08-16. |
| S | Added streamed temporary-file downloads, no-overwrite placement, failure cleanup, and restore integrity; live accepted 2026-08-17. |
| T | Added the thin `GoogleDriveSyncProvider` wrapper over the shared engine; live accepted 2026-08-18. |
| U | Added the provider factory route without UI policy or secret leakage. |
| V | Enabled Google Drive in the existing Sync tab; live accepted 2026-08-20. |
| W | Added end-to-end UI-to-factory-to-engine regression coverage without duplicating sync policy. |
| X | Added bounded retry, user cancellation, incomplete-transfer reporting, and single retry authority. `Retry-After` remained open. |
| Y | Completed final automated mapping and four controlled live sessions on 2026-08-20. Twenty-two items passed; real quota/network error shapes were unexecutable. |
| Z | Updated then-current setup, scope, token, folder, safety, package, and acceptance documentation. Screenshots remained deferred. |

## Acceptance dates

| Area | Recorded result |
| --- | --- |
| Listing and text metadata | PASS, 2026-08-03 |
| Recursive listing | PASS, 2026-08-13 |
| Create-only upload | PASS, 2026-08-16 |
| No-overwrite download | PASS, 2026-08-17 |
| Provider wrapper | PASS, 2026-08-18 |
| Sync UI and final end-to-end acceptance | PASS, 2026-08-20 |

The detailed coverage map, historical test counts, commits, live-session notes,
and residual evidence are preserved in
[Google Drive acceptance evidence](google-drive-acceptance.md).

## Carried-forward maintenance

The chronology identified three concrete follow-ups now owned by the current roadmap:

- MAINT-001: make the SFTP provider testable through an injected remote boundary;
- MAINT-002: migrate away from xUnit 2.9.3; and
- MAINT-003: honor server `Retry-After` guidance.

The remaining pre-Google feature list was converted to stable IDs in the
[current roadmap](../ROADMAP.md). This archive cannot open, close, or reprioritize them.
