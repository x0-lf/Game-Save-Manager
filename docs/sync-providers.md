# Sync Providers

This guide owns current provider behavior, capabilities, limitations, tests,
and performance guidance. Synchronization operates on completed backup runs;
the shared invariants are defined in the [safety model](safety-model.md).

## Implementation matrix

| Behavior | Local Folder | SFTP | Google Drive | WebDAV | OneDrive | MEGA |
| --- | --- | --- | --- | --- | --- | --- |
| Available | Yes | Yes | Yes | No | Yes | Spiked (In Development) |
| Authentication | Filesystem access | Password or private key over SSH | System-browser OAuth with PKCE | Not implemented | System-browser OAuth with PKCE (`Files.ReadWrite.AppFolder`) | Email + password key derivation with optional TOTP 2FA |
| Secret storage | None | Password and passphrase are session-only | OAuth token in protected secret store | None | OAuth token in protected secret store (DPAPI) | Session token and master key in protected secret store (DPAPI) |
| Folder selection | Native local folder picker or typed path | Typed remote path | Creates or discovers one app folder; no arbitrary picker | Unavailable | Sandboxed application folder (`drive/special/approot`); no arbitrary picker | Dedicated app root folder (`GameSave Manager Backups`); no arbitrary picker |
| Connection/status check | Yes | Yes | Yes | Blocked | Yes | Yes (spiked) |
| Quota display | No | No | No current UI | No | Yes (Total, Used, Remaining) | Yes (Total, Used, Remaining) |
| Open-location control | Opens local folder | No | Opens the app folder in the browser | No | No | No |
| Upload backup runs | Yes | Yes | Yes | No | Yes | Yes (chunked with AES-128-CTR and MAC) |
| Download backup runs | Yes | Yes | Yes | No | Yes | Yes (streaming decryption) |
| Overwrite runs | Never | Never | Never | N/A | Never | Never (create-only guard) |
| Delete runs | Never | Never | Never | N/A | Never | Never (zero deletion) |
| Provider-specific tests | Shared engine and UI coverage | Shared engine coverage; provider seam gap | Extensive deterministic coverage and recorded live acceptance | Availability guards | Extensive deterministic coverage (offline mocks for Graph API & OAuth) | Extensive deterministic coverage (MegaSpikeTests offline mocks) |

The capability catalog describes intended provider potential. The live UI is
narrower: Google Drive does not currently display quota or offer arbitrary
folder selection, even though cloud capabilities are declared for future UI
work.

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
and quota UI are not implemented.

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
transient failures. Server `Retry-After` instructions are not consumed; see
MAINT-003. The real shape of quota and forced network failures was not produced
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
Files.ReadWrite.AppFolder offline_access
```

### Safety & Sandboxing Invariants

1. **Sandboxed App Folder:** All sync operations strictly target the sandboxed
   application folder (`drive/special/approot`). The application never requests broad
   Drive scopes (`Files.ReadWrite`, `Files.ReadWrite.All`) and has zero visibility into
   the user's personal documents, photos, or other OneDrive content.
2. **Interactive OAuth with PKCE:** Authentication uses the system browser, loopback
   redirect listener (`http://localhost:<port>/`), and PKCE (Proof Key for Code Exchange)
   with code verifier and challenge.
3. **Protected Secret Storage:** OAuth tokens (access token and refresh token) are
   encrypted at rest via Windows DPAPI through `ISecretStore` under profile-scoped keys
   `SecretKey(remoteProfileId, SecretNames.OneDriveTokenData)`.
4. **Automatic Token Refresh:** Access tokens are automatically refreshed within 5
   minutes of expiry using the stored refresh token.
5. **Create-Only Uploads:** Backup runs are uploaded strictly in create-only mode.
   Existing remote files are never overwritten; `manifest.json` is always uploaded last
   to prevent incomplete runs from being identified as valid backups.
6. **Zero Deletion:** Synchronization never deletes existing local or remote runs.
   A same-name run with different contents is treated as an immutable conflict and left untouched.
7. **Archive Containers Supported:** Like Local Folder and SFTP, OneDrive supports
   compressed `.zip` backup archives (`SupportsArchiveContainers => true`).
8. **Storage Quota & Health:** Live storage quota is fetched from Microsoft Graph
   `/me/drive` (`total`, `used`, `remaining` bytes) and displayed directly in the UI,
   with a low-storage warning when remaining quota falls below 10%.

## MEGA (Spike Architecture — OBS-012)

Task `OBS-012` establishes the architectural spike and proves the integration boundary,
cryptographic guarantees, licensing decisions, and safety invariants for MEGA cloud synchronization:

### 1. Dependency & Licensing Evaluation

- **Option A (External `MegaApiClient`):** The widely known third-party library `MegaApiClient`
  is MIT licensed, but depends on `Newtonsoft.Json` and legacy cryptographic abstractions.
  Adopting it would introduce external dependency bloat, transitive package complexity, and
  potential friction with GSM's trimmed .NET 10 `System.Text.Json` architecture.
- **Option B (Native Internal Client `IMegaApiClient` / `MegaApiClient` — Selected):**
  GSM implements a clean, native internal client using built-in .NET 10 primitives
  (`System.Security.Cryptography`, `System.Text.Json`, `HttpClient`). This achieves:
  * Zero new third-party dependencies and zero license/copyleft contamination.
  * Direct high-performance crypto (`Aes`, `Rfc2898DeriveBytes.Pbkdf2`, `HMACSHA256`).
  * 100% deterministic testability with injectable `HttpMessageHandler` doubles without live network requirements.

### 2. Cryptographic & Protocol Architecture

1. **Key Derivation:** Client derives a 128-bit master password key using PBKDF2 with SHA-512
   and email salt (`Rfc2898DeriveBytes.Pbkdf2`), and computes user hash `uh` for session negotiation.
2. **Master Key Decryption:** Upon successful session exchange (`{"a": "us"}`), the encrypted
   master key `k` is decrypted using the derived password AES key.
3. **Two-Factor Authentication (TOTP):** If an account has 2FA enabled, MEGA returns error `-26`
   (`EMFAREQUIRED`). The client detects this condition (`MegaAuthenticationStatus.TwoFactorRequired`)
   and prompts for the 6-digit TOTP pin (`mfa`).
4. **Chunked Uploads with CBC-MAC:** Files are uploaded in standard MEGA chunks (128 KB doubling
   up to 1 MB) with AES-128-CTR streaming encryption and running CBC-MAC checksum calculation.
5. **Node Attributes Encryption:** Folder and file names are serialized into JSON attributes
   prefixed with `MEGA{"n":"name"}`, padded, and encrypted with AES-128-CBC.

### 3. Safety & Secret Protection Invariants

1. **DPAPI Secret Protection:** Session tokens and derived master keys are stored encrypted at rest
   via Windows DPAPI through `ISecretStore` under `SecretKey(profileId, SecretNames.MegaSessionData)`.
2. **Zero Plaintext Secret Exposure:** `MegaSessionToken.ToString()` strictly masks session tokens
   (`***`) and completely omits master key bytes to prevent accidental credential leakage in logs or diagnostics.
3. **Dedicated Root Folder:** Synchronizations strictly target a dedicated application folder
   (`GameSave Manager Backups`) inside the user's cloud drive root (`MegaNodeType.Root`).
4. **Create-Only Upload Guard:** `MegaRemoteFileSystemSpike.UploadRunAsync` verifies that a run with
   the requested name does not exist prior to initiating upload. Existing runs cannot be overwritten.
5. **Manifest-Last Placement:** All payload files are uploaded before `manifest.json`. An interrupted
   upload leaves an incomplete run that is ignored rather than misidentified as a valid backup.
6. **Zero Deletion:** Sync operations never call node deletion on existing backup runs.
7. **Storage Quota Inspection:** Live quota is queried via `{"a": "uq", "strg": 1}` (`mpos` used bytes,
   `msto` total bytes), with remaining capacity calculated and a warning triggered when free space is under 10%.

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
