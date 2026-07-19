# Remote Sync Providers — Comparison and Recommendation

Status: research document, no remote provider is implemented yet (by design).
The `ISyncProvider` abstraction and `LocalFolderSyncProvider` are done; this
document compares the candidate remote backends and proposes an order.

---

## 1. What a remote actually has to do

`ISyncProvider` syncs **backup runs** (immutable, manifest-bearing folders).
Every remote backend needs exactly five primitives:

| Primitive | Used for |
| --------- | -------- |
| List folders in the remote root | Discover remote runs |
| Check existence of a folder/file | Never-overwrite guarantee |
| Read a small file | `manifest.json`, `sync-log.json` |
| Write a file (create directories as needed) | Upload run files, append sync log |
| Download a file | Download run files |

Notably **not** required: delete, rename\*, move, partial writes, locking.
This is a deliberately tiny surface — the sync engine (planning, manifest
comparison, conflict detection, log handling) stays shared.

\* Rename is optional but useful for atomic-ish uploads (see 2.3).

## 2. Step 0 — prerequisites shared by every provider

These come **before** any individual provider and are each a small change:

### 2.1 Extract the engine from `LocalFolderSyncProvider`

Today the provider mixes the sync logic with direct `File`/`Directory` calls
(~20 call sites). Refactor into:

```
IRemoteFileSystem  (internal, Infrastructure)
  Task<IReadOnlyList<string>> ListRunFolderNamesAsync()
  Task<bool> ExistsAsync(path)
  Task<string?> ReadTextAsync(path)          // null when missing
  Task WriteTextAsync(path, content)
  Task UploadFileAsync(localPath, remotePath)
  Task DownloadFileAsync(remotePath, localPath)

SyncEngine (shared: preview building, manifest equivalence,
            conflict detection, sync-log handling, history recording)

LocalFolderRemoteFileSystem : IRemoteFileSystem   // wraps System.IO
WebDavRemoteFileSystem, SftpRemoteFileSystem, ... // later, ~5 methods each
```

After this, each new provider is "implement six methods + credential UI".

### 2.2 Credential storage

Remote providers need secrets (passwords, app passwords, key passphrases,
OAuth refresh tokens). Rules:

- **Never** store secrets in `gamesave.db` or any plaintext file.
- On Windows: encrypt with per-user DPAPI
  (`System.Security.Cryptography.ProtectedData`, `DataProtectionScope.CurrentUser`)
  and store the blob in `%LOCALAPPDATA%\GameSave\secrets\`.
  Only the same Windows user on the same machine can decrypt.
- Cross-platform later: swap DPAPI for libsecret (Linux) / Keychain (macOS)
  behind an `ISecretStore` interface — same pattern as the rest of the app.
- The UI never re-displays a stored secret, only "credential saved / clear".

### 2.3 Upload ordering = crash safety for free

The listing rule "a run is only a run if `manifest.json` exists" already
protects against half-synced state — **if the engine uploads `manifest.json`
last**. A connection dropped mid-upload leaves a manifest-less folder that
every device ignores (and a re-sync can complete or a cleanup can remove).
Codify this ordering in the shared engine. Where the protocol has rename
(SFTP, WebDAV MOVE), additionally upload the manifest under a temp name and
rename it into place.

### 2.4 Sync-log concurrency

Today the log is read-modify-write, last-writer-wins — fine for one person,
racy if two devices sync at the same second. WebDAV and Graph support ETags
(`If-Match`) for optimistic concurrency; SFTP does not. Acceptable plan:
keep last-writer-wins, use ETags where available, and treat the log as
informational metadata (it is never load-bearing for correctness).

---

## 3. The providers

### 3.1 WebDAV / Nextcloud

| | |
| --- | --- |
| Protocol | HTTP(S): `PROPFIND` (list), `GET`, `PUT`, `MKCOL` (mkdir), `HEAD` (exists) |
| .NET options | Thin `HttpClient` implementation (~300 lines for our 6 methods) or `WebDav.Client` (MIT). No BCL client, but PROPFIND is just an HTTP verb with a small XML body. |
| Endpoint | Nextcloud/ownCloud: `https://host/remote.php/dav/files/<user>/<path>`; any generic WebDAV server also works. |
| Credentials | Username + **app password** (Nextcloud → Settings → Security). App passwords are per-device, revocable, and work with 2FA enabled — the user's real password is never stored. Generic WebDAV: Basic auth over HTTPS. |
| Secret storage | App password → DPAPI store (2.2). |
| Mapping to ISyncProvider | 1:1 — remote folders are folders, files are files. ETags available for the sync log. |
| Hurdles | Server quirks (some WebDAV servers are sloppy with PROPFIND depth/encoding); require HTTPS by default with an explicit opt-in for HTTP on LAN. |
| Testability | Excellent: integration tests against a tiny in-test HTTP listener faking the 5 verbs; manual tests against a Docker Nextcloud. |
| Effort / risk | **Small / low.** |

### 3.2 SFTP / SSH

| | |
| --- | --- |
| Protocol | SFTP subsystem of SSH-2. |
| .NET options | **SSH.NET** (`Renci.SshNet`, MIT) — the most battle-tested library in this whole document. `ListDirectory`, `UploadFile`, `DownloadFile`, `CreateDirectory`, attribute checks — everything needed. |
| Credentials | Username + password, **or** private key file (optionally passphrase-protected). Key-file auth can point at the user's existing `~/.ssh` keys — then the app stores no secret at all (passphrase prompted per session or stored via 2.2). |
| Host identity | **Must verify the host key**: trust-on-first-use — show the fingerprint on first connect, store it, and hard-fail with a loud warning if it ever changes (this is the SSH security model; skipping it would allow silent man-in-the-middle). |
| Mapping to ISyncProvider | 1:1 like WebDAV. Bonus: SFTP has rename → clean temp-name-then-rename manifest commits. |
| Hurdles | Host-key UX (fingerprint prompt) is the only novel UI. No ETags for the log. |
| Testability | Docker OpenSSH server for integration tests. |
| Effort / risk | **Small / low.** Audience fit is excellent (VPS, TrueNAS, Unraid, home servers all speak SFTP). |

### 3.3 OneDrive (Microsoft Graph) — first OAuth provider

**How OAuth works for a desktop app, in brief:** the app is a *public
client* — it cannot keep a secret, so it uses the **authorization code flow
with PKCE**: the app opens the system browser to Microsoft's login page,
listens on `http://localhost:<port>` for the redirect, exchanges the
one-time code (plus the PKCE verifier) for an **access token** (~1 hour) and
a **refresh token** (long-lived). The refresh token is the real credential:
it must be stored encrypted (2.2) and lets the app get new access tokens
silently. The user's password never touches the app.

| | |
| --- | --- |
| API | Microsoft Graph `/me/drive` (JSON over HTTPS). |
| .NET options | **MSAL.NET** (`Microsoft.Identity.Client`, MIT) handles the whole flow + token cache; Graph calls via `HttpClient` or the Graph SDK. |
| App registration | One-time, free: Azure Entra ID app registration → gives a **client ID** that ships in the source. That is normal for public clients (PKCE protects the flow); forks should register their own ID because API quota is per-registration. |
| The killer feature | Scope **`Files.ReadWrite.AppFolder`**: the app gets its own folder (`Apps/GameSaveManager/`) and can see **nothing else** in the user's OneDrive. Minimal consent screen, no admin approval for personal accounts, works without publisher verification (users just see an "unverified publisher" note on the consent page). |
| Mapping to ISyncProvider | Good: Graph supports **path-based addressing** (`/drive/special/approot:/runname/files/...`), so the folder model maps directly. Simple upload for files < 4 MB, upload session above (save files occasionally exceed 4 MB → implement the session path). ETags available. |
| Hurdles | OAuth plumbing (browser + loopback listener), token cache encryption, Graph throttling (429 + Retry-After → retry policy). |
| Testability | No practical fake server; integration tests need a real test account. Engine tests still cover the logic via `IRemoteFileSystem` fakes. |
| Effort / risk | **Medium / medium.** |

### 3.4 Google Drive

Same OAuth model as OneDrive (public client, PKCE, loopback redirect —
Google *requires* the loopback flow for installed apps), but with more
friction:

| | |
| --- | --- |
| .NET options | `Google.Apis.Drive.v3` + `Google.Apis.Auth` (Apache-2.0). |
| Scope choice | Use **`drive.file`** (non-sensitive: the app only sees files it created) — functionally the same idea as OneDrive's app folder and it avoids the **restricted-scope verification + third-party security assessment (CASA)** that the full `drive` scope triggers. |
| The traps | A Google Cloud project in **"Testing" status caps at 100 users and — critically — refresh tokens expire after 7 days**, forcing weekly re-login until the app is published. Publishing with even non-sensitive scopes still involves brand verification to remove the "unverified app" warning screen. |
| Mapping to ISyncProvider | The most annoying of the four: Drive is **ID-based, not path-based** — names are not unique, "folders" are metadata, every path lookup is a query (`name = X and parent = Y`), and the app must handle duplicate names defensively. Doable, but the mapping layer is genuinely more code than Graph's. |
| Effort / risk | **Medium-high / medium.** Do it after OneDrive so the OAuth plumbing already exists. |

### 3.5 Mega

| | |
| --- | --- |
| API | No conventional public REST API; Mega's protocol is custom with **client-side end-to-end encryption** (keys derived from the password). Official SDK is C++; the .NET option is the community `MegaApiClient` (MIT). |
| Credentials | **Email + full account password** — there are no scoped tokens or app passwords. The app would hold a credential equal to the user's entire account, and the E2EE key derivation means that's unavoidable by design. |
| Risks | Community library breaks when Mega changes internals (history of this); third-party-client friction with Mega's terms; account throttling. |
| Verdict | **Do not implement natively.** The risk/benefit is the worst of the list. Mega users are better served by the escape hatch below — document it, revisit only if there's real demand. |

### 3.6 The escape hatch that already works: synced folders

`LocalFolderSyncProvider` already gives *every* cloud today, with zero
credentials in this app: point the Sync tab at a folder managed by the
provider's own client (OneDrive folder, Google Drive for Desktop, MEGAsync,
Dropbox, Syncthing) or an **rclone mount** (rclone speaks WebDAV, SFTP,
OneDrive, Drive, Mega and ~70 others). Native providers improve UX and
remove the third-party dependency — but nobody is blocked meanwhile. Worth a
short section in the README.

---

## 4. Side-by-side

| | WebDAV/Nextcloud | SFTP/SSH | OneDrive | Google Drive | Mega |
| --- | --- | --- | --- | --- | --- |
| Library | thin HTTP or WebDav.Client (MIT) | SSH.NET (MIT) | MSAL.NET (MIT) | Google.Apis (Apache-2.0) | MegaApiClient (community) |
| Credential | user + revocable app password | password or key file (+ host key TOFU) | OAuth refresh token | OAuth refresh token | full account password |
| Blast radius if leaked | one revocable token | one account/key (revocable) | app folder only | app-created files only | **entire account** |
| Registration needed | none | none | Azure app reg (free) | Google Cloud project + verification hurdles | none |
| Path model fit | 1:1 folders | 1:1 folders | path addressing (good) | ID-based lookups (clunky) | via community lib |
| Offline/integration testable | yes (fake server / Docker) | yes (Docker) | real account only | real account only | real account only |
| Effort | S | S | M | M-H | H |
| Risk | low | low | medium | medium | high |

## 5. Recommended order and why

0. **Engine refactor + `ISecretStore` (DPAPI)** — small, unblocks everything,
   and the refactor is pure code movement validated by the existing 177-check
   suite.
1. **WebDAV / Nextcloud** — best credential story of the non-OAuth options
   (revocable app passwords, 2FA-compatible), plain HTTPS that can be
   integration-tested without any cloud account, ETags for the sync log, and
   it serves the self-hosted audience this project clearly has. Matches the
   roadmap's stated order.
2. **SFTP/SSH** — reuses ~90% of what step 1 built; SSH.NET is the most
   mature library available; covers VPS/NAS users. The only new concept is
   host-key trust-on-first-use UX.
3. **OneDrive** — the first OAuth provider, chosen over Google because the
   app-folder scope needs no verification gauntlet, MSAL handles the token
   lifecycle, and Graph's path addressing maps cleanly onto the run-folder
   model.
4. **Google Drive** — after OneDrive, reusing the OAuth plumbing; use the
   `drive.file` scope and go through publishing early to escape the 7-day
   refresh-token trap.
5. **Mega — skip.** Full-password credential, fragile community library,
   no scoped access. Document the rclone/MEGAsync + local-folder path instead.
