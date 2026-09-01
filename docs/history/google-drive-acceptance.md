# Google Drive Acceptance Evidence

> **Historical record — not current status.** This archive preserves completed
> Google Drive architecture, verification, and live-acceptance evidence. Some
> statements intentionally describe earlier unavailable states. They cannot
> define current provider availability or roadmap status; use the current
> [provider guide](../sync-providers.md) and [roadmap](../ROADMAP.md).

## Consolidated acceptance record

The retired developer setup guide also carried milestone-specific operator
procedures and results. Its unique result summary is preserved here so the
current setup guide can remain task-focused:

| Milestone | Controlled evidence | Recorded result |
| --- | --- | --- |
| P | Root/folder checks, paginated run discovery, bounded UTF-8 reads, create-only manifest protection, and stable-ID sync-log replacement | PASS, 2026-08-03; no forbidden mutation |
| Q | Recursive canonical paths, nested pagination, collision/cycle rejection, cancellation, and deterministic disposal | PASS, 2026-08-13; exact duplicate fixtures remained deterministic-only under `drive.file` |
| R | Streamed resumable create-only upload, parent creation, collision refusal, progress, cancellation, sanitized failures, and manifest-last engine behavior | PASS, 2026-08-16 |
| S | Temporary-file streaming download, length validation, no-overwrite placement, failure cleanup, manifest rewriting, and restore integrity | PASS, 2026-08-17 |
| T | Thin provider lifetime, factory wiring, shared-engine parity, disposal, sanitized display root, and no duplicated sync policy | PASS, 2026-08-18 |
| V | Existing Sync-tab provider selection, account/root state, preview, upload, download, conflict, selection, progress, cancellation, results, and history | PASS, 2026-08-20 |

All operator output was sanitized. No account value, test-user address, client
configuration, token, authorization URL, Drive object ID, personal path,
screenshot, or raw provider response was retained. Cleanup of controlled Drive
fixtures was performed manually when deletion was outside the milestone boundary.

The final Milestone Y coverage map and four-session record later in this archive
supersede individual milestone availability statements.

## Archived architecture narrative

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
| Google Drive | No | Yes | Interactive desktop OAuth | Account/root configuration, internal recursive file listing, create-only file upload, no-overwrite file download, and an internal provider wrapper; sync unavailable |
| WebDAV | No | No | Planned server credentials | Planned persistent authentication, connection testing, logout, open location |
| OneDrive | No | No | Planned interactive OAuth | Same planned high-level capabilities as Google Drive |

Google Drive appears for account connection but cannot preview, execute, or create an `ISyncProvider`. Unavailable providers never silently fall back.

## Google Drive developer preparation

Google Drive sync remains unavailable (`IsImplemented = false`), while account authorization is configuration-available. Developers preparing a private development Google Cloud project should follow [Google Drive Developer Setup](../google-drive-developer-setup.md). Normal users do not create a Google Cloud project.

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

The application-root milestone now populates folder IDs through a dedicated Infrastructure service; folder IDs are authoritative and folder names are display-only. The Infrastructure remote filesystem can recursively list ordinary files beneath one requested run, but Google Drive remains `IsImplemented = false` and has no provider-factory entry, upload, download, preview, or sync execution.

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

The resolver consumes an already validated `GoogleAuthorizedCredential` through a credential-scoped Infrastructure factory. Dependency registration creates no browser flow, token read, Drive request, or singleton `DriveService`. The existing `drive.file` scope remains sufficient for app-created/app-accessible objects in My Drive. The validation-only `GoogleDriveRemoteFileSystem` described below does not expose the resolver or activate synchronization.

## Google Drive remote validation boundary

`GoogleDriveRemoteFileSystem` exists as a narrow Infrastructure validation boundary. Its `ValidateAsync` method requires a saved Google Drive profile with exactly `drive.file`, silently restores or refreshes the protected authentication, and creates a short-lived authenticated session. It retrieves the configured application root directly by its authoritative Drive ID; the display name is never used to rediscover or replace it.

Validation requests only root metadata and `capabilities.canListChildren` / `capabilities.canAddChildren`. The root must be a non-trashed folder in My Drive, and both child capabilities must be true. This proves the intended future read and child-folder-creation access without creating a probe file or folder. Validation performs no list traversal, metadata replacement, upload, download, rename, move, trash, deletion, permission change, quota request, or other remote mutation.

Per-profile validation generations actively cancel superseded work. Disconnect, reconnect, confirmed revocation, and profile deletion invalidate a pending generation, so a late result cannot report a stale valid state. Missing, trashed, moved, replaced, wrong-type, shared-drive, inaccessible, or revoked roots invalidate the relevant in-memory resolver cache. Cancellation, rate limiting, quota errors, and temporary provider failures preserve otherwise safe cache entries. Child IDs remain memory-only.

Provider failures map to stable, sanitized warning categories including not connected, authorization revoked, missing or inaccessible root, rate limiting, quota exceeded, cancellation, supersession, and temporary unavailability. Quota categories come from the failed Drive request; validation does not retrieve account quota totals. Warnings never contain tokens, account email, Drive IDs, request URLs, queries, or raw Google responses.

Milestone O ended with every later remote-filesystem operation throwing an explicit unavailable error. Milestone P subsequently added the narrow listing and text-metadata operations documented below, and Milestone Q added recursive backup-file listing. There is still no Google Drive provider-factory case, backup upload, backup download, preview, or sync execution, and the provider catalog continues to report Google Drive as `IsImplemented = false`.

## Google Drive recursive file listing

Milestone Q wires only `GoogleDriveRemoteFileSystem.ListFilesAsync` through an Infrastructure-internal recursive-listing service. One requested backup-run folder is resolved beneath the configured application-root ID, then traversed iteratively using one short-lived authenticated operation context. Every visited folder uses its authoritative ID and independently consumes every direct-child page.

Returned values are ordinary blob-file paths relative to the requested run folder. They use `/` exclusively, preserve exact Unicode and case, and are sorted with ordinal comparison. The run-folder name and all Drive object IDs remain internal.

Listing fails closed—with no partial paths or partial cache commit—when it encounters exact or case-insensitive sibling collisions, file/folder collisions, malformed or inconsistent metadata, trashed/shared-drive objects, Workspace documents, shortcuts, unsupported types, repeated identities, cycles, incomplete searches, authentication/access failures, provider failures, or cancellation. Only a missing requested folder or an empty folder produces the provider-neutral empty list; a descendant disappearing during traversal remains a failure.

Because `drive.file` is a per-object grant, listing returns only objects this application created or that the user explicitly opened with it. Folders and files a user adds by hand in the Drive UI are never enumerated, even inside the app-created application root. This is a property of the least-privilege scope, confirmed live on 2026-08-09 and recorded as `D-023`. Google Picker folder authorization is the supported way to extend a grant to a user-chosen folder and its contents while keeping `drive.file`; it remains future work with no assigned milestone task.

The listing path is metadata-only and read-only. It does not download file content, inspect manifests, create or update objects, delete, move, rename, trash, change permissions, upload, or download. Google Drive remains configuration-only, absent from `SyncProviderFactory`, and disabled for Preview Sync and Sync Now.

## Google Drive create-only file upload

Milestone R wires only `GoogleDriveRemoteFileSystem.UploadFileAsync` to an
Infrastructure-internal one-file upload service. Its controlled live acceptance
was recorded on 2026-08-16 with the result PASS, so the milestone is closed.

One call uploads one local file to one canonical `/` remote path:

```text
UploadFileAsync
    -> parse the target with GoogleDriveRelativePath, rejecting empty, root,
       absolute, traversal, and doubled-separator paths
    -> open one stable read-only local FileStream and capture its length once
    -> create one short-lived authenticated operation context
    -> prepare each missing parent segment under authoritative My Drive IDs
    -> guard the create-only target twice, the second time inside the
       existing parent-ID and exact-name creation lease
    -> stream the source through the official resumable files.create path as
       opaque application/octet-stream bytes
    -> validate the completed response identity, exact name, MIME, single
       expected parent, non-trashed My Drive location, and exact size
    -> record only that validated identity in the scoped object-ID cache
    -> return the validated completed byte count
```

Uploads never update, overwrite, delete, trash, rename, move, share, or change
permissions on any object. An existing sibling matching the target under
`StringComparer.OrdinalIgnoreCase` blocks creation regardless of type; no
spelling is selected. Cancellation, an invalid response, an indeterminate
completion, or a rejected cache write is never success and never reports
completed bytes, and the operation never deletes remote state to clean up.
Every failure escapes through one sanitized boundary with a fixed category and
stable code, so no credential, account value, local path, remote name, object
ID, query, upload or session URL, token, or raw provider response can reach a
message, `ToString()`, or wrapped exception.

`SyncEngine` still owns run enumeration and uploads the root `manifest.json`
last, so an interrupted run leaves a folder that existing discovery does not
treat as a complete backup. Milestone R adds no retry, recovery, or cleanup.

`GoogleDriveSyncProvider` does not exist, Google Drive remains absent from
`SyncProviderFactory` with `IsImplemented = false`, and Preview Sync and Sync
Now remain disabled.

## Google Drive no-overwrite file download

Milestone S wires `GoogleDriveRemoteFileSystem.DownloadFileAsync` to an
Infrastructure-internal one-file download service. Its controlled
development-account live acceptance passed on 2026-08-17, so the milestone is
closed.

```text
DownloadFileAsync
    -> parse the source with GoogleDriveRelativePath, rejecting empty, root,
       absolute, traversal, and doubled-separator paths
    -> refuse an existing final file or directory before any Drive work
    -> create only the missing destination folder and open one exclusive
       temporary sibling named <file>.<guid>.gsdownload
    -> create one short-lived authenticated operation context
    -> resolve the source to one authoritative blob under the configured root
    -> read only id, name, mimeType, trashed, parents, driveId, and size
    -> stream the content straight into the temporary file
    -> validate identity, exact name, blob type, trash state, single expected
       parent, My Drive location, and the written length against that size
    -> move the temporary file to its final name without overwriting
    -> return the validated completed byte count
```

Downloads never overwrite, truncate, move, or delete an existing local file or
directory. The only local deletion is this operation's own temporary file, on
every failure and cancellation path. Cancellation, a short or long body, a
changed source, or a destination that appeared during the transfer is never
success, and every failure escapes through one sanitized boundary with a fixed
category and stable code. Lifecycle logging records only fixed stages, byte
counts, categories, and codes.

`SyncEngine` keeps run enumeration, manifest rewriting, restore verification,
and progress. A Drive-downloaded run is rewritten exactly as a Local Folder one,
SHA-256 in the manifest stays the content identity, and an interrupted run has
no manifest, so run discovery never presents it as a complete backup.

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

Google Drive's completed Milestone P boundary provides validation, root and folder existence, top-level manifest-bearing run discovery, bounded immutable text reads, provider-metadata reads and replacement, and create-only text creation. Create-only text writes normalize the Drive-relative path, ensure only its parent path, perform an exact-name search both before and after acquiring a parent-ID/name lock, and issue a create request only after both searches report no object. Any same-name file, folder, duplicate, ambiguity, or inaccessible state fails closed; existing bytes are never read, updated, or truncated. A validated create response is cached only as a profile/root-scoped authoritative file identity.

Provider metadata replacement validates the exact `.gamesave-sync/sync-log.json` allowlist before authentication, ensures only the `.gamesave-sync` parent, and serializes work by profile and canonical metadata path. Inside that lease it searches by exact name beneath the authoritative parent ID: no object creates one bounded JSON blob, while one validated file updates content through its unchanged authoritative ID. A same-name folder, duplicates, ambiguity, or an inaccessible object fails closed; no temporary Drive object, rename, move, delete, or backup-run replacement is used.

The parent-ID/name create lock and profile/path metadata lock coordinate only this application process. Google Drive does not enforce globally unique names, so cross-process duplicate names remain possible; a later authoritative lookup reports that state as ambiguity instead of choosing or deleting an object.

## Google Drive provider wrapper

Milestone T adds `GoogleDriveSyncProvider`, an internal sealed wrapper over the
existing shared `SyncEngine`, and the internal profile-scoped factory that
builds it. The milestone is closed: its live development-account acceptance ran
on 2026-08-18 and passed, driving a complete upload, a complete download back
with identical bytes, an idempotent re-run, a no-overwrite re-run, sync-log
records in both directions, and cancellation leaving no manifest without its
content. Google Drive is still not registered as a selectable sync provider.

The wrapper is pure delegation. It holds one engine over one profile-scoped
remote file system and forwards `CreatePreviewAsync`, `ExecuteAsync`, and
`GetSyncLogAsync` unchanged; run enumeration, comparison, conflict reporting,
manifest rewriting, restore verification, sync-log policy, and progress all stay
in the engine. `ProviderName` is the fixed string `Google Drive`, and
`RemoteRoot` is the sanitized profile display root, which is what reaches sync
plans and persisted transfer history, so no account address, object ID, or Drive
URL can appear there.

Construction performs no authentication and no Drive request. An empty,
unknown, or unusable profile is refused before any Drive work through the same
validator the rest of the Drive boundary uses. Disposal is idempotent, and a
disposed provider refuses every operation before reaching the engine.

The wrapper is built by an internal dependency-injection factory keyed by saved
profile ID. Milestone U then lifted that boundary into the Core
contract: `ISyncProviderFactory` declares
`CreateGoogleDriveProvider(Guid remoteProfileId)`, and `SyncProviderFactory`
forwards the profile ID to the internal factory without adding a validation,
lookup, or message of its own. Its constructor is internal and takes the Drive
factory explicitly, so no service locator is involved. Google Drive
is nevertheless still inactive, with `IsImplemented = false`, no case in the
application's provider switch, and Preview Sync and Sync Now disabled until
Milestone V.

Milestone P did not activate Google Drive synchronization. Milestone Q subsequently made `ListFilesAsync` available at the Infrastructure remote-filesystem boundary, Milestone R added create-only `UploadFileAsync`, Milestone S added no-overwrite `DownloadFileAsync`, Milestone T added `GoogleDriveSyncProvider`, and Milestone U added the Core factory case. Google Drive is still `IsImplemented = false` with no case in the application's provider switch, so it stays configuration-only and Preview Sync and Sync Now remain disabled.

## End-to-end sync UI integration

Milestone W, closed on 2026-08-20. It added no product behaviour: every
requirement below is a test over code that already existed.

The gap it closed was structural. Until W, **no test constructed `SyncViewModel`
with a real provider factory.** All five view-model test files built it with
`SyncProviderSelectionTests.RecordingSyncProviderFactory`, whose fake provider
returns a fixed one-item plan and copies nothing, so the path from a UI command,
through the Core factory, into the real engine and back into bound state was
pinned only by the Milestone V live acceptance, which is a one-off manual run
rather than something CI can fail on. Forty-one facts already covered the
provider and engine level; none of them started at a command.

All coverage below is hermetic. Local Folder runs against temporary directories
the test creates and deletes; Google Drive runs through the same
`LocalFolderRemoteFileSystem` backend `GoogleDriveSyncProviderParityTests` uses,
so the wrapper and the internal factory are real while the network is absent.
No account, no SSH server, and no browser is involved.

### Automated requirement coverage

Every method below is in `SyncUiEndToEndTests` unless another class is named.

| Requirement | Deterministic coverage |
| --- | --- |
| The view model builds and drives the real Local Folder provider | `ViewModelPreview_UsesTheRealLocalFolderProvider` |
| A view-model-driven run moves bytes in both directions | `ViewModelExecute_ActuallyMovesBytesInBothDirections` |
| Upload stays create-only and download never overwrites, through the UI | `ViewModelExecute_LeavesTheAlreadySyncedRunUntouched` |
| The confirmation gate holds against a real engine | `ViewModelExecute_WithoutConfirmation_CopiesNothing` |
| The view model builds and drives the real Google Drive provider, keyed by the saved profile | `DriveViewModelExecute_MovesBytesThroughTheRealDriveWrapper` |
| Google Drive and Local Folder leave identical bound state and identical bytes | `DriveAndLocalFolder_LeaveIdenticalStateThroughTheSameUiPath` |
| An unticked run is not copied, and is reported as deliberately skipped | `UntickedRun_IsLeftAloneByTheRealEngine` |
| Progress advances against real bytes and ends complete, carrying nothing private | `Progress_AdvancesAgainstRealBytesAndEndsComplete` |
| A real engine warning reaches the bound warnings, and nothing is deleted | `AnEngineWarning_ReachesTheBoundWarningsAndDeletesNothing` |
| The same warning carries no folder identifier on the Drive path | `TheSameEngineWarning_CarriesNoIdentifierOnTheDrivePath` |
| A confirmed run is recorded in transfer history; a preview is not | `AViewModelDrivenRun_IsRecordedInTransferHistory` |
| A blocked run records nothing, and the same plan confirmed does record | `ABlockedRun_RecordsNothing` |
| A validated Drive preview advances the profile metadata it should | `ADriveRun_AdvancesTheProfileMetadataItShould` |
| A refused selection advances no metadata and records no run | `ARefusedSelection_AdvancesNoProfileMetadata` |
| The sync log round-trips through the view model and carries nothing private | `TheSyncLog_RoundTripsThroughTheViewModelAndCarriesNothingPrivate` |
| The SFTP provider is built by the real factory without touching the network, and its display root carries no secret | `TheSftpProvider_IsBuiltByTheRealFactoryWithoutTouchingTheNetwork` |
| The SFTP provider has no seam for a hermetic remote file system | `TheSftpProvider_HasNoSeamForAHermeticRemoteFileSystem` |
| The traversal guard is in the engine every provider shares | `TheTraversalGuardProtectingSftp_LivesInTheSharedEngine`, `SyncRemotePathTraversalTests.UnsafeRemoteNames_AreRejected`, `SyncRemotePathTraversalTests.DownloadingARunWithATraversingFileName_WritesNothingOutsideTheRunFolder`, `SyncRemotePathTraversalTests.AnUnsafeNameLateInTheListing_StillWritesNothing` |
| An incomplete SFTP form is refused before any provider is built | `SelectingSftpInTheUi_BuildsOnlyTheSftpProvider` |

All twenty-two cited methods and every cited class were verified to exist in the
repository rather than assumed.

### The standing check this milestone added

Every view-model test in the file must fail when the real
`SyncProviderFactory` is swapped for
`SyncProviderSelectionTests.RecordingSyncProviderFactory`. If one still passes,
it is asserting something a provider that copies nothing already satisfies, and
it is not testing the composition.

**That check caught vacuous tests twice.** Create-only and
confirmation tests both asserted only that nothing had been copied, which
the fake satisfies trivially; both now assert the positive result first. In Task
5 both profile-metadata tests passed under the swap, because
`TryUpdateLastSuccessfulConnection` fires on any plan reporting validation
succeeded; both now also assert that the internal Drive factory was asked for a
remote boundary, which only the real path does.

The check has one recorded exception. Three tests pass under the swap by design:
`TheSftpProvider_IsBuiltByTheRealFactoryWithoutTouchingTheNetwork`,
`TheSftpProvider_HasNoSeamForAHermeticRemoteFileSystem`, and
`TheTraversalGuardProtectingSftp_LivesInTheSharedEngine`. None of them goes
through the view model; they assert type shape and factory construction
directly.

### What Milestone W did not cover, and why

**`SftpSyncProvider` has no behavioural coverage, and W did not add the seam
that would allow it.** Its constructor takes `SftpConnectionSettings` and builds
its own `SftpRemoteFileSystem`, which builds its own `SftpClient`. Unlike
`GoogleDriveSyncProvider`, which is handed an `IRemoteFileSystem` and is
therefore testable offline, there is nowhere to inject a fake, so its transfer
behaviour cannot be exercised without a real SSH server. Milestone W adds no
product behaviour, so this was reported rather than fixed.

It matters more than it looks: SFTP is the provider the 2026-08-18 security
audit found an arbitrary local file write in. That
particular defect is fixed in `SyncEngine` and is covered, and the tests above
pin that SFTP really runs on that engine, so the fix demonstrably applies. But
the provider's own behaviour remains untested.

Adding the seam is a separate, user-gated task. It is recorded in the roadmap
maintenance backlog. Until then,
`TheSftpProvider_HasNoSeamForAHermeticRemoteFileSystem` pins the absence, and
when a seam appears that test is rewritten to use it rather than deleted.

### Recorded Milestone W verification

```text
Date: 2026-08-20
Tested tree: ef3c070 plus the uncommitted Task 7 documentation
Release suite: 1,780 passed, 0 failed, 0 skipped
Release build: succeeded, 0 warnings, 0 errors, from a full
               --no-incremental rebuild
Direct package baseline: unchanged, 21 unique direct packages
Transitive entries: 248
Vulnerable: none in any of the six projects
Deprecated: xUnit 2.9.3 (Legacy)
Live acceptance: not applicable; Milestone W is hermetic by design and the
                 Milestone V live acceptance already covered the real path
```

## Bounded retry and incomplete-transfer reporting

Milestone X, closed on 2026-08-20. It is the first milestone since V to change
behaviour on the real transfer path.

The gap it closed was narrower than its title suggests. The Drive stack already
classified every failure as retryable or not, and already had thorough
cancellation and incomplete-run coverage. **What it had never done was act on
the classification:** `IsRetryable` is true for exactly `RateLimited` and
`Unavailable`, and all eleven readers of that flag only copied it into another
failure record. Nothing retried anything.

All coverage below is hermetic. A fake remote boundary fails a chosen number of
times, and a recording delay reports what the backoff asked for without spending
it, so a suite that finishes in about a second still exercises a thirty-second
backoff ceiling.

### Automated requirement coverage

| Requirement | Deterministic coverage |
| --- | --- |
| Waiting is injected, never called directly, and the composition root supplies the real one | `DelayProviderTests.TheCompositionRoot_ResolvesTheProductionDelay`, `DelayProviderTests.TheSeam_IsUsedOnlyWhereRetryIsComposed` |
| The production delay actually waits, abandons a cancelled wait, and refuses an already-cancelled token | `DelayProviderTests.TheSystemDelay_ActuallyWaits`, `DelayProviderTests.TheSystemDelay_ReturnsPromptlyWhenCancelledDuringTheWait`, `DelayProviderTests.TheSystemDelay_RefusesAnAlreadyCancelledToken` |
| A test double can record a delay without spending it, and still honours cancellation | `DelayProviderTests.TheRecordingDelay_RecordsWhatWasRequestedWithoutSpendingIt`, `DelayProviderTests.TheRecordingDelay_StillHonoursCancellation` |
| A retryable failure is retried until it succeeds | `RetryingRemoteFileSystemTests.ARetryableFailure_IsRetriedUntilItSucceeds` |
| A non-retryable failure fails on the first attempt, with no delay requested | `RetryingRemoteFileSystemTests.ANonRetryableFailure_FailsOnTheFirstAttempt` |
| Retry is bounded in attempts and in total delay | `RetryingRemoteFileSystemTests.RetryIsBounded_InAttemptsAndInTotalDelay`, `RetryingRemoteFileSystemTests.AnUnreasonableBaseDelay_StillCannotExceedTheCeiling` |
| Retry never converts create-only into overwrite | `RetryingRemoteFileSystemTests.ARetriedCreate_StillRefusesAnExistingRemoteObject`, `RetryingRemoteFileSystemTests.ARetriedUpload_UploadsTheSameFileOnceItSucceeds` |
| Members that perform no remote work are not wrapped, and an impossible configuration is refused | `RetryingRemoteFileSystemTests.ThePassThroughMembers_AreNotWrapped`, `RetryingRemoteFileSystemTests.TheDecorator_RefusesAnImpossibleConfiguration` |
| The application is the only retry authority, so its bound is the real bound | `RetryAuthorityTests.EveryDriveServiceInitializer_DisablesTheLibraryBackoff`, `RetryAuthorityTests.TheRetryBound_IsTheOnlyBoundThatApplies` |
| No server-supplied retry instruction is captured anywhere | `RetryAuthorityTests.NoServerSuppliedRetryInstruction_IsCapturedAnywhere` |
| An interrupted run is reported as incomplete, with the bytes it really copied | `IncompleteTransferReportingTests.AnInterruptedUpload_IsReportedAsIncompleteWithTheBytesItCopied` |
| A run that copied nothing is still reported as failed | `IncompleteTransferReportingTests.ARunThatCopiedNothing_IsStillReportedAsFailed` |
| An incomplete run is not a clean result | `IncompleteTransferReportingTests.AnIncompleteRun_IsNotACleanResult` |
| An incomplete run is left exactly as it was, with no cleanup and no invented manifest | `IncompleteTransferReportingTests.AnIncompleteRun_IsLeftExactlyAsItWas` |
| The new status reaches the results list as its own value | `IncompleteTransferReportingTests.TheUiRow_ShowsIncompleteAsItsOwnStatus` |
| Cancellation during a backoff is honoured, and a cancellation is never a retryable failure | `RetryingRemoteFileSystemTests.CancellationDuringABackoff_IsHonouredAndStopsTheWork`, `RetryingRemoteFileSystemTests.ACancelledOperation_IsNeverTreatedAsARetryableFailure` |
| A real backoff is abandoned rather than slept through | `RetryCancellationTests.ARealBackoff_IsAbandonedRatherThanSleptThrough` |
| A cancelled retry during a sync copies nothing and records no run | `RetryCancellationTests.ACancelledRetryDuringASync_CopiesNothingAndRecordsNoRun` |
| The wired Drive predicate retries the three retryable validation statuses and no others | `GoogleDriveRemoteFileSystemTests.TheWiredRetryPredicate_MatchesRealDriveFailuresAndOnlyTheRightOnes` |
| The engine still keeps payloads, writes no manifest, and repairs nothing when a run stops partway | `GoogleDriveSyncEngineCompatibilityTests.ManifestFailure_KeepsPayloadsAndNeverRepairsTheRun`, `GoogleDriveSyncEngineCompatibilityTests.InterruptedDownload_LeavesNoRunPresentedAsComplete` |

All thirty cited methods and every cited class were verified to exist in the
repository rather than assumed.

**One of them was added after the milestone was first written up.** Every retry
test used a synthetic exception, so nothing proved that the predicate the Drive
factory wires up matches the exception production actually throws. A predicate
naming the wrong type would have left retry silently doing nothing while every
retry test still passed. The theory drives the real
`GoogleDriveRemoteFileSystemFactory` with a service that throws the real
`GoogleDriveRemoteOperationException`, and checks all three retryable validation
statuses retry four times and three non-retryable ones fail on the first
attempt.

### Two findings carried out of the milestone

**No server-supplied retry instruction is reachable.** `Retry-After` lives on the
HTTP response. The failure mapper is handed a `GoogleApiException` and takes only
the status code and a safe reason string from it; the header never reaches that
far, and the failure record has nowhere to carry a delay even if it did.
Capturing one means observing the HTTP response at all nine Drive service
constructions and carrying the observed delay to the point where the decorator
decides how long to wait. That is its own task, and Milestone X did not do it.
`NoServerSuppliedRetryInstruction_IsCapturedAnywhere` pins the absence and is to
be rewritten, never deleted, when it is built.

**A second retry layer was in force and nobody had stated it.** The nine
`BaseClientService.Initializer` constructions set only `HttpClientInitializer`
and `ApplicationName`, so whatever backoff the client library applies by default
was running underneath the decorator added one task earlier. Two retry layers
compose by multiplication, so the thirty-second ceiling would have been the
decorator's share of the wait rather than the whole of it. All nine now disable
the library backoff.

### What the guard measurement showed

Retry changes call counts, and 116 call-count assertions exist across at least
twelve test files. **The measured blast radius was three tests, then two**, and
none of the five was a call-count assertion. Choosing to decorate
`IRemoteFileSystem` rather than each backend service is what kept it there.
Both figures were measured by implementing and running, not predicted.

### Recorded Milestone X verification

```text
Date: 2026-08-20
Tested tree: bfbed98 plus the uncommitted Task 7 documentation
Release suite: 1,813 passed, 0 failed, 0 skipped
Release build: succeeded, 0 warnings, 0 errors, from a full
               --no-incremental rebuild
Direct package baseline: unchanged, 21 unique direct packages
Transitive entries: 248
Vulnerable: none in any of the six projects
Deprecated: xUnit 2.9.3 (Legacy)
Live acceptance: not performed; hermetic fault injection only. See below.
```

### On the live gate, and what was not done

**Milestone X closes on hermetic acceptance, and no live run was performed.**
The reasoning, offered as a recommendation before the milestone closed and not
overridden: X's subject is failure handling, and the failures cannot be produced
on demand against a real account. Nobody can make Drive return `429` to order, so
a live run would exercise the happy path Milestone V already accepted and would
prove nothing new about retry. Fault injection is the only way to reach these
paths at all, and it is hermetic by nature.

What that leaves open, stated plainly rather than implied: **the retry and
incomplete-reporting paths have never run against a real Google account.** A
short live regression re-run of the Milestone V path would confirm the happy path
still works with the decorator in place, and it remains available at any time.
The Milestone Y final acceptance covers it regardless.

## Milestone Y acceptance coverage map

Milestone Y Task 3, 2026-08-20. Every one of the twenty-three README acceptance
items for Milestone Y, mapped to what already proves it and what only a real
account can.

**Automated** means the suite proves it and the live session only confirms it
against a real account. **Live-only** means no test can prove it, because the
thing being tested is the real service's behaviour. Most items are both: the
mechanism is automated and the live run confirms Google behaves as the fakes
assume.

| # | Acceptance item | Automated coverage | Live |
| --- | --- | --- | --- |
| 1 | Connect one Google account | `GoogleDriveOAuthTests`, `GoogleDriveOAuthViewModelTests` | A, and only live can prove the real consent flow |
| 2 | Restart the app and remain connected | `GoogleDriveAccountLifecycleTests`, `GoogleDriveOAuthViewModelTests` | A |
| 3 | Disconnect and remove the local token | `GoogleDriveAccountLifecycleTests` | A |
| 4 | Create or find one application root folder | `GoogleDriveRootFolderTests`, `GoogleDriveRootFolderViewModelTests`, `GoogleDriveFolderPathEnsureTests` | A |
| 5 | Detect a local-only run | `SyncEngineTests`, `SyncUiEndToEndTests.ViewModelPreview_UsesTheRealLocalFolderProvider` | B |
| 6 | Upload the selected run | `GoogleDriveUploadIntegrationTests`, `SyncUiEndToEndTests.ViewModelExecute_ActuallyMovesBytesInBothDirections` | B |
| 7 | Verify the manifest is uploaded last | `GoogleDriveSyncEngineCompatibilityTests.Upload_CreatesEveryPayloadBeforeTheRootManifest`, `SyncEngineTests.Upload_UsesTheSharedEngine_UploadsManifestLast_AndRecordsHistory` | B |
| 8 | Detect a remote-only run | `GoogleDriveSyncProviderParityTests.Preview_MatchesLocalFolderItemForItem` | C |
| 9 | Download the selected run | `GoogleDriveDownloadIntegrationTests`, `GoogleDriveSyncEngineCompatibilityTests.Download_RewritesTheManifestExactlyLikeLocalFolderDoes` | C |
| 10 | **Restore the downloaded run** | `DownloadedRunRestoreTests.ARunDownloadedFromTheRemote_CanBeRestoredToItsOriginalLocation`, `DownloadedRunRestoreTests.ADownloadedRunWhoseContentWasTampered_IsRefusedByRestore` | C |
| 11 | Identify identical runs as in sync | `GoogleDriveSyncProviderParityTests.Preview_MatchesLocalFolderItemForItem`, `SyncUiEndToEndTests.ViewModelExecute_LeavesTheAlreadySyncedRunUntouched` | C |
| 12 | Detect a same-name/different-manifest conflict | `SyncEngineTests.Preview_ReportsConflictAndIgnoresIncompleteRemoteFolders` | C |
| 13 | Never overwrite remote files | `GoogleDriveSyncProviderIntegrationTests.Execute_NeverOverwritesAnExistingRemoteRun`, `RetryingRemoteFileSystemTests.ARetriedCreate_StillRefusesAnExistingRemoteObject` | B |
| 14 | Never overwrite local runs | `GoogleDriveSyncProviderIntegrationTests.Execute_NeverOverwritesExistingLocalData` | C |
| 15 | Never delete local or remote runs | `GoogleDriveSyncProviderIntegrationTests.TheProviderPath_IssuesNoForbiddenDriveOperation`, `IncompleteTransferReportingTests.AnIncompleteRun_IsLeftExactlyAsItWas` | D |
| 16 | Cancel an active upload | `CancelSyncTests`, `GoogleDriveSyncProviderCancellationTests.CancellingDuringAnUpload_CopiesNothingAndRecordsNoRun` | D |
| 17 | Handle revoked access | `GoogleDriveAccountLifecycleTests`, `GoogleDriveRemoteValidationServiceTests` | A, and only live can prove Google's real revocation response |
| 18 | Handle a missing root folder | `GoogleDriveRemoteFileSystemTests`, `GoogleDriveRootFolderViewModelTests` | A |
| 19 | Handle quota and network errors | `GoogleDriveDownloadFailureMapperTests`, `RetryingRemoteFileSystemTests`, `GoogleDriveRemoteFileSystemTests.TheWiredRetryPredicate_MatchesRealDriveFailuresAndOnlyTheRightOnes` | D, and **only live can prove the real error shapes** |
| 20 | Record Google Drive sync in SQLite history | `SyncUiEndToEndTests.AViewModelDrivenRun_IsRecordedInTransferHistory` | B |
| 21 | App builds | Release build | 8 |
| 22 | CLI builds | Release build | 8 |
| 23 | Reviewer builds | Release build | 8 |

Every cited class and method was verified to exist in the repository rather than
assumed.

### What the mapping found

**Item 10 had no coverage at all, and was not live-only.** A downloaded run was
proved discoverable and hash-valid, and the restore service was proved to work on
a locally created run, but nothing joined the two. The last link in the chain a
user actually cares about, getting a save back off Drive, rested on inference.
Both halves are hermetic, so it was closed in this task rather than deferred to a
live session: `DownloadedRunRestoreTests` downloads through the real engine and
restores through the real `BackupRestoreService`, and also proves a downloaded
run whose content was tampered with afterwards is refused, because the SHA-256
manifest stays the authoritative content identity.

**Three items are genuinely live-only in part, and the live sessions exist for
them.** The real OAuth consent flow (1), Google's real response to a revoked
authorization (17), and the real shapes of quota and network errors (19). A fake
can only assert what it was told to return; whether Google actually returns that
is a question no test can answer.

**Everything else is automated, and the live sessions confirm rather than
discover.** That is the correct division: a live run is expensive, manual, and
unrepeatable in CI, so it should carry only what nothing else can.

## Milestone Y final acceptance result

Milestone Y closed on 2026-08-20. Google Drive synchronisation has been accepted
end to end against a real development account.

All twenty-three README acceptance items are resolved: twenty-two passed, and one
is recorded as unexecutable live with its reason. The mapping of every item to
its coverage is the "Milestone Y acceptance coverage map" section above.

### The four live sessions

| Session | Items | Result |
| --- | --- | --- |
| A, three runs | regression, 1, 2, 3, 4, 17, 18 | PASS |
| B | 5, 6, 7, 13, 20 | PASS |
| C | 8, 9, 10, 11, 12, 14 | PASS |
| D | 15, 16 | PASS; item 19 unexecutable live |

```text
Date: 2026-08-20
Tested commit: b73493386c398e64a60bc872bfacf765d662cedc
Result: PASS
Sanitized failure categories: none, in any session
Release suite: 1,820 passed, 0 failed, 0 skipped
Release build: succeeded, 0 warnings, 0 errors, --no-incremental
App, CLI, and Reviewer: each built individually, 0 warnings, 0 errors
Direct package baseline: unchanged, 21 unique direct packages
Transitive entries: 248
Vulnerable: none in any of the six projects
Deprecated: xUnit 2.9.3 (Legacy)
Harnesses: one per session, each deleted afterwards, none committed
```

### What the live sessions proved that no test could

Three things, and only three. Everything else was automated first and confirmed
live, which is the right division: a live run is expensive, manual, and
unrepeatable in CI.

- **The real OAuth consent flow.** Item 1 is evidenced by the operator's reconnect after revoking the grant, not by session A's stage A6. A6 completed in ten seconds because disconnect deliberately does not revoke the Google Account grant, so Google had nothing to ask about. After the revocation Google presented the full consent screen, requiring sign-in and explicit confirmation.
- **Google's real response to a revoked authorization.** Item 17. The application detected it, stopped reporting connected, and kept the saved profile, its root metadata, and all Drive content.
- **That the Milestone W and X changes did not break the happy path.** Session A opened with the regression Milestone X closed without: the Milestone V path previews and syncs against a real account with the retry decorator wrapping every remote operation and the client library's own backoff disabled.

### What is still not proven, stated plainly

**The shape of a real Google quota or network error.** Item 19 is unexecutable
live: provoking a quota error means deliberately exhausting a real account's
storage or a real project's API quota, and provoking a network error means
disconnecting the machine mid-transfer, which produces a failure chosen by the
operating system rather than by Google. Both are covered deterministically, and
`GoogleDriveRemoteFileSystemTests.TheWiredRetryPredicate_MatchesRealDriveFailuresAndOnlyTheRightOnes`
is precise about which failures retry and which do not. What remains unproven is
whether the mapper classifies what Google actually sends. No amount of hermetic
testing removes that, and it is the same residual risk Milestone X recorded.

### What the live sessions cost, and what that bought

Nine live runs across four sessions, and **five of them failed first on a defect
in the procedure rather than in the product.** That ratio is the
useful number, because each one was a claim that would otherwise have been made
on inference:

- A brand-new profile cannot reach Drive at all, because stored authentication is keyed by profile identifier. Session A's missing-root stage now mutates the real connected profile and restores it in a `finally`.
- A stage that observes an absent state must run while the state is absent. Session A's revoked-access stage failed twice because the operator revoked and then reconnected before it ran; the application was right both times.
- A conflict needs two **manifests** to differ, not a payload byte, because the manifest is the authoritative content identity. Session C's first draft edited a payload and the preview correctly reported the run in sync.
- The original path a manifest records must outlive the context that seeded the run, or a restore writes somewhere the test is not looking.
- `TestData.CreateBackupRun` records an original path without creating a file there, so a non-vacuity step must assert the absence rather than delete the file.

None of the five was a product defect. All five were assertions that would have
passed vacuously or failed misleadingly if written more loosely.

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
