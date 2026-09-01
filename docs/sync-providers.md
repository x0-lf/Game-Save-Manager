# Sync Providers

This guide owns current provider behavior, capabilities, limitations, tests,
and performance guidance. Synchronization operates on completed backup runs;
the shared invariants are defined in the [safety model](safety-model.md).

## Implementation matrix

| Behavior | Local Folder | SFTP | Google Drive | WebDAV | OneDrive |
| --- | --- | --- | --- | --- | --- |
| Available | Yes | Yes | Yes | No | No |
| Authentication | Filesystem access | Password or private key over SSH | System-browser OAuth with PKCE | Not implemented | Not implemented |
| Secret storage | None | Password and passphrase are session-only | OAuth token in protected secret store | None | None |
| Folder selection | Native local folder picker or typed path | Typed remote path | Creates or discovers one app folder; no arbitrary picker | Unavailable | Unavailable |
| Connection/status check | Yes | Yes | Yes | Blocked | Blocked |
| Quota display | No | No | No current UI | No | No |
| Open-location control | Opens local folder | No | No current UI | No | No |
| Upload backup runs | Yes | Yes | Yes | No | No |
| Download backup runs | Yes | Yes | Yes | No | No |
| Overwrite runs | Never | Never | Never | N/A | N/A |
| Delete runs | Never | Never | Never | N/A | N/A |
| Provider-specific tests | Shared engine and UI coverage | Shared engine coverage; provider seam gap | Extensive deterministic coverage and recorded live acceptance | Availability guards | Availability guards |

The capability catalog describes intended provider potential. The live UI is
narrower: Google Drive does not currently display quota, offer arbitrary folder
selection, or expose an open-in-browser control, even though cloud capabilities
are declared for future UI work.

## Shared behavior

All implemented providers use the same Sync engine. Preview compares backup-run
names and manifest identity and reports upload, download, in-sync, conflict, or
warning. Users select individual copy actions, confirm execution separately,
and receive live progress and per-run results.

Uploads are create-only and place `manifest.json` last. Downloads never replace
an existing local run. Neither direction deletes a run. A same-name run with
different content is a conflict and remains untouched. `.gamesave-sync/sync-log.json`
is the only mutable remote metadata path.

Saved remote profiles contain non-secret settings only. Selecting, saving,
renaming, or deleting a profile never starts a connection or sync. Profile
deletion removes only the profile configuration and its owned secrets; it does
not delete local backups, remote runs, history, archives, saves, or SFTP host trust.

## Local Folder

Local Folder targets an ordinary local, network-mounted, or provider-mounted
directory. The native picker and open-location control are available. Access is
whatever the operating system grants to the current process.

This is also the simplest way to use a desktop sync client: select a directory
inside a Google Drive for desktop, OneDrive, Nextcloud, or similar mounted tree.
Game Save Manager then synchronizes safely with the local directory, while that
separate client owns cloud transfer, authentication, retries, and status.

## SFTP

SFTP supports password or private-key-file authentication. Passwords and key
passphrases remain in memory for the current session and clear when relevant
profile state changes. They are not persisted automatically.

Host keys use trust on first use. The first connection presents the SHA-256
fingerprint for explicit trust; later changes fail until the stored host key is
deliberately forgotten. The connection check does not copy data.

The shared engine, path-containment fixes, and UI behavior have deterministic
coverage. The SFTP provider still constructs its concrete connection and lacks
an injectable remote seam, so its upload/download paths do not have isolated
provider-level behavioral tests. This is MAINT-001 in the [roadmap](ROADMAP.md).

## Google Drive

Google Drive requests exactly:

```text
https://www.googleapis.com/auth/drive.file
```

Authentication uses the system browser, loopback callback, PKCE, and the
protected secret store. Connect and Reconnect are explicit. Disconnect removes
the selected profile's local token and account identity but preserves its
profile, root metadata, local backups, history, and Drive content. External
revocation is detected and requires an explicit reconnect.

The App creates or discovers one visible folder:

```text
My Drive/GameSave Manager Backups
```

Its Drive ID is authoritative, so a rename or move within My Drive remains
linked. Missing, trashed, invalid, unsupported, or ambiguous roots are not
silently replaced. Shared drives, full Drive browsing, arbitrary folder picking,
quota UI, and open-in-browser UI are not implemented.

Google Drive uploads and downloads stream data, preserve shared engine ordering,
report progress, support cancellation, and use bounded retries for classified
transient failures. Server `Retry-After` instructions are not consumed; see
MAINT-003. The real shape of quota and forced network failures was not produced
during live acceptance, so the deterministic mapper coverage has not been
confirmed against those two real error shapes. This affects retry classification,
not the create-only/no-delete data policy.

Developer OAuth configuration is documented separately in the
[developer-only setup guide](google-drive-developer-setup.md). Closed chronology
and evidence are [historical records](history/google-drive-acceptance.md), not
the source of current provider status.

## Performance choices

Choose the path that matches who should own synchronization:

| Approach | What happens | Best fit |
| --- | --- | --- |
| Google Drive provider | Game Save Manager lists, compares, uploads, downloads, retries, and records runs through the Drive API | App-managed, auditable backup-run sync |
| Local Folder inside Google Drive for desktop | Game Save Manager syncs to a local directory; Google's desktop client moves those files | Existing desktop-client caching and transfer ownership |
| Manual browser movement | The user uploads or downloads files in a browser outside the App | Occasional manual transport only |

Browser movement is not application-managed synchronization: the App cannot
preview it, enforce manifest-last ordering, record it, or report its completion.
Provider or desktop-client slowness is a performance concern, not evidence of
save corruption. Verify manifests and operation results before diagnosing data loss.

## Provider Definition of Done

A new or changed provider is complete only when:

- its stable kind, capability metadata, configuration surface, and factory path agree;
- credentials and tokens follow the documented secret-storage policy;
- connection, cancellation, paging, progress, upload, download, and failure
  behavior have deterministic coverage at the narrowest available boundary;
- no-overwrite, no-delete, manifest-last, conflict, and path-containment rules remain proven;
- required platform, display, network, or credential-backed scenarios are run
  and sanitized, or explicitly reported as unverified;
- current behavior and limitations are updated in this guide;
- developer-only setup changes are updated in the owning setup guide; and
- dependency or license changes are recorded in `THIRD-PARTY-NOTICES.md`.
