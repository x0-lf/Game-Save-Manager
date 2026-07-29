# Sync provider architecture

Game Save Manager syncs backup runs through the existing provider-neutral pipeline:

```text
ISyncProvider
    -> SyncEngine
    -> IRemoteFileSystem
```

The capability catalog describes provider behavior; the provider factory remains the only component that creates working sync providers. Configuration availability is separate from `IsImplemented`.

| Provider | Sync implemented | Configuration available | Configuration | Current capabilities |
|---|---:|---:|---|---|
| Local or mounted folder | Yes | Yes | Folder path | Folder selection, connection testing, open in native file manager |
| SFTP server (SSH) | Yes | Yes | Session credentials | Server credentials, connection testing |
| Google Drive | No | Yes | Interactive desktop OAuth | Planned resumable upload, quota, folder selection, persistent authentication, logout, open location |
| WebDAV | No | No | Planned server credentials | Planned persistent authentication, connection testing, logout, open location |
| OneDrive | No | No | Planned interactive OAuth | Same planned high-level capabilities as Google Drive |

Google Drive appears for account connection but cannot preview, execute, or create an `ISyncProvider`. Unavailable providers never silently fall back.

## Google Drive developer preparation

Google Drive sync remains unavailable (`IsImplemented = false`), while account authorization is configuration-available. Developers preparing a private development Google Cloud project should follow [Google Drive Developer Setup](google-drive-developer-setup.md). Normal users do not create a Google Cloud project.

The planned client configuration is non-secret and developer-local. Personal client configuration, downloaded credential files, account information, and user tokens must never be committed. Later OAuth token data must use the existing secret-store boundary rather than profile JSON or plaintext SQLite.

## Google SDK dependency boundary

The official Google packages are direct dependencies of Infrastructure only:

```text
GameSaves.App
    -> Game Save Manager-owned interfaces and models
        -> GameSaves.Infrastructure.GoogleDrive
            -> Google.Apis.Auth
            -> Google.Apis.Drive.v3
```

Core and App have no Google package reference, and regression tests reject Google SDK types in their public boundaries. Google SDK source remains in `GameSaves.Infrastructure.GoogleDrive`.

`SyncEngine` and `IRemoteFileSystem` remain provider-neutral. The factory creates no Google provider; Local Folder and SFTP remain the only sync-capable choices.

OAuth token persistence adapts Google `IDataStore` to the existing `ISecretStore`; `FileDataStore` is never used. The desktop Client ID is read only from local `GAMESAVES_GOOGLE_CLIENT_ID`, preferring a process value and then the persistent Windows user value. When the generated Desktop OAuth client requires its non-confidential client secret for token exchange, the same precedence is used for developer-local `GAMESAVES_GOOGLE_CLIENT_SECRET`. Neither value is persisted or displayed, and downloaded credential JSON is not loaded.

## Google Drive connection settings boundary

Milestone I adds pure Game Save Manager models for representing future Google Drive connection configuration without introducing OAuth or Drive API behavior. Field ownership remains explicit:

```text
SyncRemoteProfile
    -> remote profile ID
    -> account display name
    -> root folder display name
    -> root folder ID

GoogleDriveSyncRemoteSettings
    -> optional account email
    -> requested OAuth scope

GoogleDriveConnectionSettings
    -> combined runtime view
    -> connection status
    -> whether protected OAuth data exists

ISecretStore
    -> protected OAuth token bytes
```

The provider-settings serializer uses an explicit Google Drive DTO containing only schema version 1, the optional account email, and the exact `https://www.googleapis.com/auth/drive.file` scope. Access tokens, refresh tokens, client IDs, credential objects, connection status, and `HasStoredToken` never enter profile JSON.

`GoogleDriveConnectionSettingsService` builds the runtime view from the saved profile and checks only the exact `SecretNames.OAuthTokenData` key through `ISecretStore.ExistsAsync`; it does not read or deserialize token bytes. A stored token produces `StoredAuthenticationAvailable`, not `Connected`, because existence does not prove validity. Connection status and token presence are not persisted as authoritative profile data.

The application-root milestone now populates folder IDs through a dedicated Infrastructure service; folder IDs are authoritative and folder names are display-only. Google Drive remains `IsImplemented = false` and has no provider-factory entry or sync operations.

## Google Drive OAuth boundary

```text
GameSaves.App
    -> IGoogleDriveOAuthService
        -> GameSaves.Infrastructure.GoogleDrive
            -> GoogleWebAuthorizationBroker / GoogleAuthorizationCodeFlow
            -> LocalServerCodeReceiver
            -> GoogleSecretDataStore
                -> ISecretStore
```

Interactive authorization opens the system browser, uses a random loopback listener and PKCE, and requests exactly `https://www.googleapis.com/auth/drive.file`. The profile GUID is the stable Google-library user key. `GoogleSecretDataStore` allowlists a version-1 token DTO and maps it to `SecretNames.OAuthTokenData`; it never creates a plaintext token file or clears another profile's or provider's secrets.

Silent restore never opens a browser. `UserCredential` refreshes stale access tokens through the official flow and writes refreshed data back through `ISecretStore`. Confirmed revoked or invalid authorization produces `ReauthenticationRequired`, removes exactly that profile's invalid Google OAuth token when possible, and preserves the saved profile. Unreadable authentication remains explicitly removable without first deserializing it. Connected status is reported only after a minimal Drive `about.get` request for `user(displayName,emailAddress)` succeeds.

The App displays safe connection state and account metadata. Cancellation, denial, browser/callback failures, corrupt storage, and refresh failures map to stable, non-secret results. Authorization does not create a root folder or enable preview/execution.

## Google Drive account lifecycle

```text
Connect
    -> interactive protected authorization

Restore
    -> silent token restore and refresh

Reconnect
    -> interactive replacement authorization

Disconnect
    -> delete the selected profile's local OAuth token
    -> clear saved account identity
    -> preserve profile, root metadata, backups, and Drive data

External revocation
    -> detect confirmed invalid authorization
    -> remove the invalid local token when possible
    -> preserve last known account context
    -> require explicit reconnect
```

Reconnect stages replacement authentication until Drive validates the newly authorized account. Cancellation, denial, and pre-validation failure therefore leave the previous protected token and account metadata unchanged. If a different account is authorized, account identity is replaced while any future root-folder identity is preserved but treated as requiring validation by the later root-folder milestone.

Disconnect is deliberately local and works offline. It requires explicit confirmation, removes only `SecretNames.OAuthTokenData` for the selected profile, and does not call Google's revocation endpoint. It does not delete the Google Account, revoke the grant in Google Account settings, delete Drive files, delete backup data, delete history, or delete the saved remote profile. Programmatic remote grant revocation is not part of Milestone K.

The lifecycle state machine distinguishes no saved profile, disconnected, stored-but-unchecked authentication, connecting, validated connection, reauthentication required, unavailable infrastructure, and failure. Token existence alone never displays Connected. Even a validated Google account cannot preview or execute sync because the catalog still reports Google Drive as `IsImplemented = false`.

## Google Drive application root folder

Milestone L manages exactly one visible folder and nothing beneath it:

```text
My Drive/
└── GameSave Manager Backups/
```

The saved `SyncRemoteProfile.RemoteFolderId` is authoritative. `RemoteRootDisplayName` is refreshed from Drive for display only, so renaming the folder keeps the same identity. Moving a valid folder elsewhere within My Drive returns `Moved` but remains linked by ID; the App does not move it back or create a replacement.

```text
Validated account
    -> validate stored root ID
        -> valid: reuse by ID
        -> renamed: update display name
        -> moved in My Drive: continue by ID
        -> missing/trashed/invalid: require explicit recreation

No stored ID
    -> search accessible top-level My Drive folder
        -> one result: reuse
        -> zero results: explicit setup may create
        -> multiple results: stop as ambiguous
```

`InspectAsync` is read-only: after successful Restore, Connect, or Reconnect it validates the stored ID or discovers one unique app-accessible top-level candidate, but it never creates a folder. With `drive.file`, discovery covers folders in this OAuth application's per-file authorization set—especially roots previously created by the App—not every arbitrary same-name folder in the user's Drive. A future user-selected arbitrary folder should use Google Picker while retaining `drive.file`; full Drive scope is not required. `EnsureAsync` is the explicit initial setup action. It searches twice under per-profile operation coordination before creating `GameSave Manager Backups` directly under My Drive. `RecreateAsync` accepts an explicit confirmation type, searches again, reuses a unique candidate, and creates only when no candidate exists.

Deleted, trashed, wrong-type, inaccessible, and shared-drive roots retain their stale saved metadata until replacement succeeds. Existing Drive objects are never moved, renamed, restored, trashed, or deleted. Duplicate matches are never selected arbitrarily. Disconnect and external revocation preserve root metadata; reconnect validates it against the newly connected account before it can be treated as ready.

The folder wrapper requests only the metadata needed for each operation, constrains discovery and direct-root membership checks to the `drive` space and `user` corpus, excludes shared-drive items, and follows every page token. Discovery's exact `'root' in parents` query and creation's explicit `parents = ["root"]` establish the initial top-level location without a separate `files.get("root")` dependency. Stored IDs are checked against the paginated app-visible root listing to distinguish Ready from Moved. Sanitized diagnostics contain only the operation, HTTP status, allowlisted Google reason, stable error code, and retryability; they never contain request URLs, IDs, account values, response bodies, or OAuth data. The hidden application-data space and its OAuth scope are forbidden for user backup runs.

Milestone L itself added no generic path resolver, child folder, backup-run hierarchy, Picker, quota call, upload, download, `GoogleDriveRemoteFileSystem`, or sync provider. A Connected account with a Ready root still cannot preview or execute sync because `GoogleDrive.IsImplemented` remains false.

## Google Drive object/path resolver

Milestone N adds an Infrastructure-only resolver beneath the configured application-root ID. Game Save Manager remote paths use `/` as their only separator; an empty path means the configured root. Segment text preserves case and exact Unicode, and names containing apostrophes or backslashes remain valid Drive names. Leading or trailing separators, empty segments, `.` and `..`, NUL, and unsafe control characters are rejected without using host-filesystem normalization.

```text
configured root ID
    -> exact-name child query under the current parent ID
        -> zero matches: NotFound
        -> one validated match: continue using its authoritative ID
        -> multiple matches: Ambiguous; select nothing
```

Query construction is isolated in `GoogleDriveQueryBuilder`. It escapes backslashes before apostrophes and lets the Google client library perform URL encoding. Exact-child searches include the parent ID, exact name, and `trashed = false`, use the `drive` space and `user` corpus, exclude shared drives, request only required metadata, and follow every `nextPageToken`. The completed query is not logged because it contains object IDs and user-selected names.

`EnsureFolderPathAsync` reuses unique existing folders and creates only missing parent folders. Per-parent/name asynchronous coordination repeats the lookup before creation, preventing duplicate creation races within this process. Same-name files, duplicate folders, invalid create responses, trashed objects, and shared-drive objects stop resolution; nothing is selected arbitrarily, overwritten, renamed, moved, trashed, or deleted.

The object-ID cache is memory-only and scoped to the saved profile and configured root. Keys preserve case and include the parent ID, exact child name, and expected object kind. Only unique, type-checked, non-trashed My Drive objects with validated parent membership are cached. Every cross-call cache hit is checked again by authoritative ID. Missing, renamed, moved, trashed, wrong-type, or shared-drive entries are evicted; a stale folder clears the root scope because descendants may also be invalid. Reconnect, disconnect, root replacement, profile deletion, and confirmed authorization revocation have explicit Infrastructure invalidation reasons. Child IDs are never stored in SQLite, profile JSON, or a second cache file.

The resolver consumes an already validated `GoogleAuthorizedCredential` through a credential-scoped Infrastructure factory. Dependency registration creates no browser flow, token read, Drive request, or singleton `DriveService`. The existing `drive.file` scope remains sufficient for app-created/app-accessible objects in My Drive. No `GoogleDriveRemoteFileSystem`, Google Drive provider-factory case, backup-run listing, upload, download, preview, or sync execution exists yet.

## Remote metadata write semantics

The remote filesystem contract separates immutable backup-run content from intentionally mutable provider metadata:

```text
Create-only backup content
    -> CreateTextFileIfMissingAsync
    -> never truncate or replace an existing file

Provider metadata
    -> ReadProviderMetadataAsync
    -> ReplaceProviderMetadataAsync
    -> restricted to .gamesave-sync/sync-log.json
```

General `ReadTextFileAsync` remains the read operation for immutable run content such as `<run>/manifest.json`. `SyncEngine` uses the provider-metadata methods only for `.gamesave-sync/sync-log.json`, so log history can be replaced after appending while run manifests and backup files remain create-only. Mutable-path validation rejects absolute and drive-qualified paths, traversal, empty segments, run folders, and every path outside the exact metadata allowlist.

Local Folder creates immutable text with `FileMode.CreateNew`. Metadata replacement writes and flushes a unique temporary sibling before replacing the final name; mounted or network filesystems may provide weaker atomicity than a local filesystem. SFTP also uses exclusive `FileMode.CreateNew`, writes metadata to a temporary sibling, and prefers the server's POSIX rename extension. Servers without replacement rename use an explicit non-atomic direct fallback restricted to validated provider metadata, followed by temporary-file cleanup. Neither provider deletes or replaces backup-run content.

Google Drive does not implement this remote filesystem contract yet. Its Infrastructure object/path resolver supplies future ID resolution and safe parent-folder creation only; no Drive provider, backup-run listing, metadata implementation, upload, download, preview, or execution exists.

## Saved profiles and secrets

Named profiles contain non-secret configuration only. Selecting a saved Google Drive profile may silently validate already-protected authentication, but it never opens a browser; saving any profile never starts authentication. Profile selection and saving never preview or execute sync. Users may also work without a saved profile.

Secret identity uses the immutable profile GUID plus a stable canonical secret name; mutable display names, account names, and remote URLs are not secret keys. The Core `ISecretStore` contract accepts byte payloads so later token caches are not forced into password strings.

On Windows, `WindowsDpapiSecretStore` uses:

- `ProtectedData` with `DataProtectionScope.CurrentUser`;
- deterministic, versioned, non-secret additional entropy based on the profile GUID and secret name;
- the existing application SQLite database;
- the `protected_sync_secrets` table, whose payload column is an encrypted BLOB;
- explicit unavailable/corrupted results that never contain secret bytes.

DPAPI ciphertext is tied to the Windows user profile and machine protection environment. Moving the database to another Windows user or machine is not expected to preserve authentication. Unreadable entries are not overwritten or deleted automatically; disconnecting or deleting the profile can remove them, after which later provider integrations will require reauthentication.

Profile deletion removes the profile's encrypted secrets and configuration only. It does not remove backup runs, remote files, sync history, SFTP known-host entries, archives, or save files. Disconnect removes encrypted authentication but keeps the saved non-secret profile.

SFTP passwords and private-key passphrases remain session-only and are not automatically written to the secret store. Google Drive sync, WebDAV, OneDrive, quota calls, and cloud folder browsing are not implemented.

Linux Secret Service and macOS Keychain secret-store implementations are future work.
