# Sync Providers

This guide owns current provider behavior, capabilities, limitations, tests,
and performance guidance. Synchronization operates on completed backup runs;
the shared invariants are defined in the [safety model](safety-model.md).

## Implementation matrix

| Behavior | Local Folder | SFTP | Google Drive | WebDAV | OneDrive | MEGA |
| --- | --- | --- | --- | --- | --- | --- |
| Available | Yes | Yes | Yes | No | Yes, with a developer-supplied client ID | No (withdrawn) |
| Authentication | Filesystem access | Password or private key over SSH | System-browser OAuth with PKCE | Not implemented | System-browser OAuth with PKCE and `state` (`Files.ReadWrite.AppFolder`) | Not implemented |
| Secret storage | None | Password and passphrase are session-only | OAuth token in protected secret store | None | OAuth token in protected secret store (DPAPI) | None |
| Folder selection | Native local folder picker or typed path | Typed remote path | Creates or discovers one app folder; no arbitrary picker | Unavailable | Sandboxed application folder (`drive/special/approot`); no arbitrary picker | Unavailable |
| Connection/status check | Yes | Yes | Yes | Blocked | Yes | Blocked |
| Quota display | No | No | No current UI | No | Yes (used and total) | No |
| Open-location control | Opens local folder | No | Opens the app folder in the browser | No | No | No |
| Upload backup runs | Yes | Yes | Yes | No | Yes (upload sessions above 4 MiB) | No |
| Archive containers (.7z, .zip) | Yes | Yes | Yes | No | Yes | No |
| Download backup runs | Yes | Yes | Yes | No | Yes | No |
| Overwrite runs | Never | Never | Never | N/A | Never (enforced server-side) | N/A |
| Delete runs | Never | Never | Never | N/A | Never | N/A |
| Provider-specific tests | Shared engine and UI coverage | Shared engine coverage over an injectable remote seam | Extensive deterministic coverage and recorded live acceptance | Availability guards | Deterministic coverage through stub HTTP handlers (Graph and OAuth) | Availability guards |

The capability catalog describes intended provider potential. The live UI is
narrower: Google Drive does not currently display quota or offer arbitrary
folder selection, even though cloud capabilities are declared for future UI
work.

## Shared behavior

All implemented providers use the same Sync engine. Preview compares backup-run
names and manifest identity and reports upload, download, in-sync, conflict, or
warning. Users select individual copy actions, start execution with a separate
button named for what it copies, and receive live progress and per-run results.

With "Transfer runs as compressed archive" on, a run is sent as one `.7z`
container at the remote root plus a `<name>.7z.manifest.json` sidecar that
carries its identity. The sidecar is written last, so a container without one
is reported as an interrupted upload, never as a run. Containers download back
as folder runs. Every implemented provider supports this; on a cloud provider
it turns hundreds of requests per run into two.

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
coverage. Since MAINT-001 the SFTP provider runs the shared engine over an
injectable remote file system, so its upload and download paths are tested
against an in-memory double.

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
and quota UI are not implemented.

Runs are stored as folders, or as `.7z` containers beside them when archive
transfer is on. The container listing is one child listing of the backup
folder, files only; a Drive-native object named like an archive, or two
containers whose names differ only by case, fails the preview closed rather
than being offered as a run that could not be restored.

The Sync page can open that folder in the system browser. It is capability
driven: the action is offered for any provider whose descriptor declares
`SupportsOpenRemoteLocation`, and it is refused for a root that is missing,
trashed, moved out of reach, duplicated, or behind an account that needs to
reconnect. The folder's display name is what the UI shows; the authoritative
folder ID is never displayed, bound, or logged, and only ever reaches the
browser URL the command hands to the shell. Opening a location reads nothing
and transfers nothing.

The `drive.file` scope is not involved in that URL, and does not restrict it.
`https://drive.google.com/drive/folders/<id>` is a user-facing Drive web
address, not an API call: it is authorised by whatever Google account the
browser is signed into, and the folder belongs to the user in their own My
Drive. A narrow API scope therefore neither grants nor withholds it. The
consequence worth stating is the other one: if the browser is signed into a
different Google account than the one connected here, Drive shows that account's
"no access" page rather than the folder.

Launching is delegated to the operating system. The command starts the target
with the platform shell (`UseShellExecute`), which means the user's default
browser opens a Drive URL and the user's default file manager opens a local
folder. If no handler is registered, or the shell refuses, the app reports that
the location could not be opened and does nothing else.

Google Drive uploads and downloads stream data, preserve shared engine ordering,
report progress, support cancellation, and use bounded retries for classified
transient failures: rate limiting and temporary unavailability only; permanent
states such as a missing client configuration are not retried. Server
`Retry-After` instructions are not consumed for Drive: the Google client does
not surface the header to the failure mapper, and the MAINT-003 attempt to
capture it never delivered and was removed. The real shape of quota and forced network failures was not produced
during live acceptance, so the deterministic mapper coverage has not been
confirmed against those two real error shapes. This affects retry classification,
not the create-only/no-delete data policy.

Developer OAuth configuration is documented separately in the
[developer-only setup guide](google-drive-developer-setup.md). Closed chronology
and evidence are [historical records](history/google-drive-acceptance.md), not
the source of current provider status.

## Microsoft OneDrive

Microsoft OneDrive requests sandboxed permissions via Microsoft Graph:

```text
Files.ReadWrite.AppFolder offline_access User.Read
```

No client ID ships with the App. A developer registers a Microsoft application
(public client, redirect `http://localhost`, personal Microsoft accounts, which
is the `/consumers` endpoint) and puts its application (client) ID in the
`GAMESAVES_ONEDRIVE_CLIENT_ID` environment variable, process or user scope.
Without a valid GUID there, OneDrive reports that it is not configured and
Connect is not offered.

### Safety & Sandboxing Invariants

1. **Sandboxed App Folder:** All sync operations target the sandboxed
   application folder (`drive/special/approot`). The application never requests broad
   Drive scopes (`Files.ReadWrite`, `Files.ReadWrite.All`) and has no visibility into
   the user's other OneDrive content. Saved scopes outside the three above are refused.
2. **Interactive OAuth with PKCE:** Authentication uses the system browser, a
   loopback listener on a free port, PKCE, and a random `state` that the callback
   must return. A callback on the wrong path or with the wrong state is refused and
   the listener keeps waiting, for at most five minutes. The page shown to the
   browser is static; nothing from the callback is echoed back.
3. **Protected Secret Storage:** OAuth tokens are encrypted at rest via Windows
   DPAPI through `ISecretStore` under `SecretKey(remoteProfileId, SecretNames.OneDriveTokenData)`.
   A token that cannot be stored fails the connect before the profile is changed.
   Token records mask their values in `ToString()`.
4. **Token Refresh:** Access tokens are refreshed within 5 minutes of expiry.
   Only an `invalid_grant` answer asks the user to reconnect; any other refresh
   failure is reported as the service being unavailable.
5. **Create-Only Uploads:** Uploads ask Graph to fail on a name conflict
   (`@microsoft.graph.conflictBehavior=fail`), and a 409 becomes the create-only
   refusal, so a file that appears between preview and upload is never replaced.
   Files over 4 MiB use an upload session in 10 MiB chunks; the chunk requests go
   to the session URL without the bearer token. `manifest.json` is uploaded last.
6. **Zero Deletion:** Synchronization never deletes local or remote runs. Only
   `.gamesave-sync/sync-log.json` may be replaced. A partial download created by a
   failed call is removed so a retry can create it again; existing local files are
   never opened for writing.
7. **Archive Containers Supported:** Like every other provider, OneDrive stores
   `.zip` and `.7z` backup archives.
8. **Paging and Retries:** Folder listings follow every `@odata.nextLink` page
   (only links on the Graph host are followed). HTTP 429, 5xx, network errors and
   timeouts are retried with bounded backoff, honouring a `Retry-After` that fits
   the retry budget.
9. **No account data in history:** The remote root recorded in plans and
   history is the constant `OneDrive: AppRoot (GameSave Manager)`, never the
   account email. Error messages are fixed sentences without response bodies.
10. **Quota is informational:** The Sync page shows used and total storage after
    a connect. A full drive no longer blocks validation; an upload that does not
    fit fails like any other failed item.

## MEGA

MEGA is catalogued but not available. The first client (OBS-012/OBS-013) did
not implement MEGA's login or encryption protocol: its key derivation and
upload path did not match MEGA's, downloads were not decrypted, and quota read
fields MEGA does not return. It could not work against a real account, and
every connect attempt sent a weak password verifier to MEGA's servers. It was
withdrawn rather than left offering a Connect button that could never succeed.

The `SyncProviderKind.Mega` value stays because it is persisted. A saved MEGA
profile loads as unavailable, the same way a WebDAV profile does. A future
implementation must use MEGA's real protocol and must never persist
password-derived key material.

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

### Why the Google Drive provider can be slow

The Drive provider talks to an HTTP API, one object at a time. Two things
dominate how long a sync takes, and neither is a fault:

- **File count, not total size.** Every file in a run is its own request, and
  every run folder must be listed before it can be compared. A run of many
  small files takes longer than one large file of the same total size.
- **Google's own rate limits.** The Drive API applies per-user request limits.
  When the App is throttled it backs off and retries the classified transient
  failures rather than failing the run, so a large sync can spend real time
  waiting rather than transferring.

A sync that is slow is still a sync that is running. Progress reporting is per
file, cancellation is honoured, and cancelling leaves a partial run rather than
damage, because uploads only create files and downloads never overwrite one.
Treat a stalled-looking transfer as slow until an operation result or a warning
says otherwise.

No throughput figure is promised here. Measured behavior depends on the account,
the network, the file count, and Google's current limits, and this project has
no benchmark that would make a number honest.

### The Local Folder alternative for bulk transfers

For a first sync, or any transfer of many or large runs, the supported
alternative is to let Google's own desktop client own the transfer:

1. Install Google Drive for desktop and let it mount or mirror a local folder.
2. In Sync, choose the **Local or mounted folder** provider.
3. Point it at a folder inside that mounted Drive location.

The App then does what it does best - preview, compare manifests, copy runs in
manifest-last order, record the result - against what is, to it, an ordinary
directory. Google's client moves those bytes to the cloud on its own schedule,
with its own caching and resumption. The safety model is unchanged: create-only
upload, no-overwrite download, nothing deleted.

Two consequences are worth stating plainly. The App reports a run as copied once
it reaches the local mounted folder; whether the desktop client has finished
uploading it to Google is that client's business, not something the App can
observe. And the two paths are not interchangeable for the same data: a run
synced through the mounted folder lives wherever that folder points, not in the
`GameSave Manager Backups` folder the Drive provider owns.

### What must stay intact, whichever path is used

A backup run is a directory that is self-describing, and every path above relies
on that:

- The run folder keeps its name. It is the identity both sides compare on.
- `manifest.json` stays in the run folder root, unedited. It carries the game,
  the file count, the total size, and the hashes that decide in sync from
  conflicting.
- The `files` subtree keeps its relative layout. Flattening it, re-zipping it, or
  renaming files inside it makes the run unrecognisable.
- The manifest is written last, on purpose. A run folder without a readable
  manifest is reported as incomplete rather than treated as a backup, which is
  what an interrupted upload leaves behind.

Moving runs by hand in a browser can satisfy all of that, and the App will pick
them up on the next preview if it does. It is still unmanaged: nothing previews
it, nothing orders it, nothing records it, and a partially uploaded run looks
exactly like an interrupted one until its manifest arrives.

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
