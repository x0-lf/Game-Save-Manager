# Sync provider architecture

Game Save Manager syncs backup runs through the existing provider-neutral pipeline:

```text
ISyncProvider
    -> SyncEngine
    -> IRemoteFileSystem
```

The capability catalog describes provider behavior; the provider factory remains the only component that creates working providers. A capability never implies availability unless the descriptor also has `IsImplemented = true`.

| Provider | Implemented | Configuration | Current capabilities |
|---|---:|---|---|
| Local or mounted folder | Yes | Folder path | Folder selection, connection testing, open in native file manager |
| SFTP server (SSH) | Yes | Session credentials | Server credentials, connection testing |
| Google Drive | No | Planned interactive OAuth | Planned resumable upload, quota, folder selection, persistent authentication, logout, open location |
| WebDAV | No | Planned server credentials | Planned persistent authentication, connection testing, logout, open location |
| OneDrive | No | Planned interactive OAuth | Same planned high-level capabilities as Google Drive |

Unavailable providers never appear as normal selectable providers and cannot preview or execute sync.

## Google Drive developer preparation

Google Drive remains unavailable (`IsImplemented = false`): OAuth login, API clients, folder operations, and sync are not implemented. Developers preparing a private development Google Cloud project should follow [Google Drive Developer Setup](google-drive-developer-setup.md). The guide documents repository-safe project, consent, test-user, scope, and desktop-client configuration for later milestones; normal users do not create a Google Cloud project.

The planned client configuration is non-secret and developer-local. Personal client configuration, downloaded credential files, account information, and user tokens must never be committed. Later OAuth token data must use the existing secret-store boundary rather than profile JSON or plaintext SQLite.

## Google SDK dependency boundary

The official Google packages are direct dependencies of Infrastructure only:

```text
GameSaves.App
    -> Game Save Manager-owned interfaces and models
        -> GameSaves.Infrastructure.GoogleDrive (future implementation)
            -> Google.Apis.Auth
            -> Google.Apis.Drive.v3
```

`GameSaves.Infrastructure.GoogleDrive` is the reserved namespace for later implementation; no Google Drive service class exists yet. Core and App have no Google package reference, and regression tests reject Google SDK types in their public boundaries. Future source files containing `using Google.` belong in Infrastructure.

`SyncEngine` and `IRemoteFileSystem` remain provider-neutral and unchanged. Google Drive remains unimplemented and unavailable, so the factory creates no Google provider and the normal selector continues to expose only Local Folder and SFTP.

Later OAuth work must adapt token persistence to the existing `ISecretStore`; Google's file-based token store must not become a second persistence system. The desktop Client ID remains local developer configuration: through Milestone I, the application does not read `GAMESAVES_GOOGLE_CLIENT_ID` or load downloaded credential JSON.

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

Folder IDs are authoritative when a later milestone populates them; folder names are display-only. Google Drive remains `IsImplemented = false`: there is still no OAuth login, client-ID reader, Google credential, `DriveService`, API request, provider factory entry, or enabled selector option.

## Saved profiles and secrets

Named Local Folder and SFTP profiles contain non-secret configuration only. Selecting or saving a profile never connects, previews, or syncs. Users may also work without a saved profile.

Secret identity uses the immutable profile GUID plus a stable canonical secret name; mutable display names, account names, and remote URLs are not secret keys. The Core `ISecretStore` contract accepts byte payloads so later token caches are not forced into password strings.

On Windows, `WindowsDpapiSecretStore` uses:

- `ProtectedData` with `DataProtectionScope.CurrentUser`;
- deterministic, versioned, non-secret additional entropy based on the profile GUID and secret name;
- the existing application SQLite database;
- the `protected_sync_secrets` table, whose payload column is an encrypted BLOB;
- explicit unavailable/corrupted results that never contain secret bytes.

DPAPI ciphertext is tied to the Windows user profile and machine protection environment. Moving the database to another Windows user or machine is not expected to preserve authentication. Unreadable entries are not overwritten or deleted automatically; disconnecting or deleting the profile can remove them, after which later provider integrations will require reauthentication.

Profile deletion removes the profile's encrypted secrets and configuration only. It does not remove backup runs, remote files, sync history, SFTP known-host entries, archives, or save files. Disconnect removes encrypted authentication but keeps the saved non-secret profile.

SFTP passwords and private-key passphrases remain session-only and are not automatically written to the secret store. Google Drive, WebDAV, OneDrive, OAuth login, quota calls, and cloud folder browsing are not implemented.

Linux Secret Service and macOS Keychain secret-store implementations are future work.
