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

`SyncEngine` and `IRemoteFileSystem` remain provider-neutral and unchanged. The factory creates no Google provider; Local Folder and SFTP remain the only sync-capable choices.

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

Folder IDs are authoritative when a later milestone populates them; folder names are display-only. Google Drive remains `IsImplemented = false` and has no provider-factory entry, root-folder behavior, or sync operations.

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

Silent restore never opens a browser. `UserCredential` refreshes stale access tokens through the official flow and writes refreshed data back through `ISecretStore`. Invalid refresh credentials produce `ReauthenticationRequired` without deleting the saved profile or encrypted token. Connected status is reported only after a minimal Drive `about.get` request for `user(displayName,emailAddress)` succeeds.

The App displays safe connection state and account metadata. Cancellation, denial, browser/callback failures, corrupt storage, and refresh failures map to stable, non-secret results. Authorization does not create a root folder or enable preview/execution.

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
