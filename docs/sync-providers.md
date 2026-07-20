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
