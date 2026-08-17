# Google Drive Developer Setup

This guide is for developers working on Game Save Manager. Normal end users do not need to create a Google Cloud project. Completing these steps prepares a development project only: it does not make Google Drive sync functional. Google Drive account authorization and an Infrastructure-only read-only recursive file-listing primitive are available for development, while upload, download, preview, and synchronization remain later roadmap milestones.

Code done during Milestone J reads `GAMESAVES_GOOGLE_CLIENT_ID` from the local process environment or, on Windows, directly from the persistent user environment and performs installed-desktop OAuth in the system browser. A process-scoped value takes precedence. Tokens are stored only through the protected `ISecretStore`, and account validation uses the minimal account lookup described below. Later root, text-metadata, and recursive-listing operations remain short-lived and narrowly scoped. The application never loads a downloaded credential file. Builds and automated tests require no personal Google configuration.

Never use or commit a personal OAuth token for repository development, and never send a Google Account password to the application. Authorization uses Google's supported system-browser flow for installed desktop applications, a loopback callback, and PKCE.

## Prerequisites

You need:

- a Google Account with access to Google Cloud Console;
- a Google Drive-enabled account that you are authorized to use for testing;
- a local checkout of Game Save Manager;
- a developer contact email that you actively monitor; and
- an understanding that this is a development project, not a production OAuth application.

Use a separate Cloud project for development and testing instead of reusing a future production project. A suitable project name is `Game Save Manager Development`. The OAuth app name should accurately represent this project and must not imply ownership or endorsement by Google.

Use placeholders in notes, examples, issues, and pull requests:

```text
YOUR_GOOGLE_CLOUD_PROJECT_ID
YOUR_DEVELOPER_EMAIL
YOUR_TEST_GOOGLE_ACCOUNT
YOUR_DESKTOP_CLIENT_ID
```

Do not commit a real project ID, account identifier, or credential.

## Create the development project

Follow Google's current [Create projects](https://docs.cloud.google.com/resource-manager/docs/creating-managing-projects) guidance:

1. Open [Google Cloud Console](https://console.cloud.google.com/).
2. Open the project selector in the console toolbar.
3. Select **New project**.
4. Enter `Game Save Manager Development`, or another clearly development-specific name.
5. Select the appropriate organization or folder when the account belongs to one. A personal account can use **No organization** when that choice is available.
6. Create the project.
7. Wait for creation to finish, then confirm that the new project is selected.
8. Record the project name in private developer notes.
9. Keep its actual project ID out of committed examples.

> **Always verify the selected Google Cloud project before enabling APIs or creating OAuth clients.**

All later API, consent, audience, scope, and client settings must be configured under this same selected project.

## Enable only the Google Drive API

Follow Google's current [API enablement](https://docs.cloud.google.com/apis/docs/getting-started#enabling_apis) flow:

1. Confirm the development project is selected.
2. Open **APIs & Services**.
3. Open **Library**.
4. Search for **Google Drive API**.
5. Select **Google Drive API**.
6. Select **Enable**.
7. Confirm it appears among the project's enabled APIs.

Do not enable Google Picker, Sheets, Docs, People, Gmail, service-account APIs, or any unrelated API for this milestone. Add another API only when a later milestone has a concrete requirement.

## Configure Google Auth Platform

Google's current configuration is organized under **Google Auth Platform**, including **Branding**, **Audience**, **Data Access**, **Clients**, and **Verification Center**. If the platform is not configured yet, **Branding** presents a **Get Started** flow. Google's [OAuth consent configuration guide](https://developers.google.com/workspace/guides/configure-oauth-consent) is the authority if console labels change.

### Branding and contact information

1. Open **Google Auth Platform** > **Branding**.
2. Select **Get Started** when prompted.
3. Set **App name** to `Game Save Manager`.
4. Select a monitored **User support email**.
5. In **Contact Information**, enter `YOUR_DEVELOPER_EMAIL` using an address you monitor.
6. Review and accept Google's API Services User Data Policy when prompted, then create the configuration.

The initial setup requires the app name, user support email, audience, and developer contact information. An app logo, homepage, privacy-policy URL, and terms-of-service URL can remain unset during private development when the console does not require them. Do not invent URLs. A production or verification submission has additional branding, verified-domain, homepage, and policy requirements; preparing or submitting that material is outside Milestone G.

Google can send OAuth policy, verification, configuration, and security notices to the registered contact addresses. Keep them current and monitored.

### Audience

The repository's default development setup is:

```text
External + Testing
```

Use **External** for a personal account or a public-development project, and leave the publishing status in **Testing**. An eligible Google Workspace project owned by a Cloud organization may offer an **Internal** audience, limited to that organization.

Changing the application to Production and completing OAuth verification are not part of this milestone. Do not use **Verification Center** to submit a production application as part of these instructions.

### Development test users

For an External application in Testing:

1. Open **Google Auth Platform**.
2. Open **Audience**.
3. Find **Test users**.
4. Select **Add users**.
5. Add `YOUR_TEST_GOOGLE_ACCOUNT` and, only when needed, `SECOND_TEST_ACCOUNT_IF_NEEDED`.
6. Save the configuration.
7. Verify that each intended test account appears in the list.

Only add people who have agreed to test the application. Never commit test-user email addresses, passwords, shared plaintext credentials, or account screenshots. Contributors should use their own explicitly authorized test account, not a maintainer's personal account.

## Declare the planned scope

The roadmap plans this scope:

```text
https://www.googleapis.com/auth/drive.file
```

Google describes `drive.file` as per-file access for files the application creates or files the user makes available to it. It is narrower than full Drive access and is listed as non-sensitive in Google's [Drive scope reference](https://developers.google.com/workspace/drive/api/guides/api-specific-auth).

In **Google Auth Platform** > **Data Access**, use **Add or Remove Scopes** to review or declare this scope for the development configuration. Google requires scopes to be declared in the console and requested by application code. Game Save Manager now requests this exact scope and no other Google or OpenID scope.

Do not configure these broader scopes unless a later milestone demonstrates that they are unavoidable:

```text
https://www.googleapis.com/auth/drive
https://www.googleapis.com/auth/drive.readonly
```

Full Drive access is not planned.

## Create the Windows desktop OAuth client

Follow Google's current [OAuth client credential](https://developers.google.com/workspace/guides/create-credentials#desktop-app) instructions:

1. Confirm the correct development project is selected.
2. Open **Google Auth Platform**.
3. Open **Clients**.
4. Select **Create client**.
5. Choose **Desktop app**.
6. Name it `Game Save Manager Desktop Development`.
7. Create the client.
8. Record the generated Client ID in private local configuration.
9. Download the JSON only if it is needed for local development.
10. Store any downloaded file outside the repository.

Do not create Web application, Android, iOS, Chrome extension, or service-account credentials for this milestone. Google recommends a separate client ID for each platform; this guide covers the current Windows desktop development client only.

### Installed-app security model

Google's [OAuth guide for desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app) states that installed applications cannot keep embedded secrets confidential. A desktop client secret is not an application password and must never be used as proof that a request came from an authentic copy of Game Save Manager.

The implementation relies on the supported installed-app flow, system-browser authorization, PKCE, the library's loopback redirect and state validation, and secure token storage. Account access and refresh tokens are real secrets and use the existing protected secret store.

Repository policy is deliberately stricter than the installed-client confidentiality model:

- never commit downloaded OAuth credential JSON;
- never commit a developer client secret;
- never use a personal development Client ID in committed examples;
- never commit user tokens; and
- never commit test-user account information.

## Local desktop OAuth client configuration

The application reads the development Client ID from:

```text
GAMESAVES_GOOGLE_CLIENT_ID
```

Set it for the current PowerShell session:

```powershell
$env:GAMESAVES_GOOGLE_CLIENT_ID = "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com"
```

Or set a persistent Windows user environment variable:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_GOOGLE_CLIENT_ID",
    "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com",
    "User")
```

Game Save Manager reads the persistent Windows user value directly, so an existing Explorer or terminal process does not need to inherit the newly configured value. Restart the App itself after changing the variable so its connection panel is initialized from the current configuration.

> Code which was done during Milestone J reads this variable when Google Drive account authorization is requested. It is never copied into profile JSON, SQLite, logs, or the UI.

Google documents `client_secret` as optional for the installed-app token exchange, but a generated Desktop OAuth client can require its generated value when used through the selected Google .NET client library. If the App reports that it could not complete the authorization exchange, configure the value from that same Desktop OAuth client locally:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_GOOGLE_CLIENT_SECRET",
    "YOUR_DESKTOP_CLIENT_SECRET",
    "User")
```

Game Save Manager reads this optional value from the process environment or, on Windows, directly from the persistent user environment. It passes the value only to the installed-app authorization flow; it is never copied into profile JSON, SQLite, logs, result formatting, or the UI. Restart the App after changing it.

There is no committed default Client ID or client secret. A desktop application cannot keep its client secret confidential, so this value is not treated as an application password or proof that the application is authentic. PKCE remains enabled and provides the installed-app authorization protection. Do not add a real Client ID or client secret to source code, project files, README, JSON, tests, CI, screenshots, or error messages. Official end-user releases must inject their release OAuth configuration through the build or release environment.

## What account connection does

After authorization, Infrastructure makes one short-lived Google Drive `about.get` request and asks only for `user(displayName,emailAddress)`. The returned display name and optional email are saved as non-secret profile metadata. Access and refresh token bytes remain in `ISecretStore`; connection status and token-presence flags are runtime state and are not persisted in profile JSON.

Account connection does not create a Drive folder, show quota, upload or download a backup, or enable Google Drive sync preview or execution. After a validated connection, the App performs a read-only application-root inspection: it validates a saved folder ID or discovers one unique app-accessible top-level `GameSave Manager Backups` folder. Creation requires the explicit **Set Up Drive Folder** action.

## Test the Google account lifecycle

Use only an explicitly authorized development test account. Keep account details, screenshots, tokens, and OAuth configuration outside the repository.

1. Start Game Save Manager, select a saved Google Drive profile, and confirm stored authentication restores without opening a browser.
2. Choose **Reconnect**, complete the system-browser authorization, and confirm the validated account and Connected state return to the App.
3. Start Reconnect again and cancel it. The previous valid account and protected token should remain connected.
4. Start Reconnect again and deny access. The previous valid account and protected token should remain unchanged.
5. Select **Confirm removing locally stored Google Drive authentication**, then choose **Disconnect**.
6. Confirm the App shows Not connected after removing the local protected token. The saved profile, root-folder metadata, local backups, sync history, and Drive files must remain.
7. Restart the App and confirm that profile remains disconnected until Connect is explicitly selected.
8. Connect again, then remove the application's access from the test account's Google Account settings.
9. Restart the App or select the profile so silent restoration runs. Confirm it reports that authorization expired or was revoked, does not open a browser, removes the invalid local token when possible, and offers Reconnect.
10. Throughout the test, confirm Google Drive preview/execution stays disabled and no folder, upload, or download operation occurs.

Ordinary **Disconnect** is local-only and works offline. It does not call Google's token-revocation endpoint, revoke the grant in Google Account settings, delete the Google Account, or delete Drive files. Remote programmatic grant revocation is outside Milestone K.

## Test the application root folder

Use only the private development test account and keep account details, Drive folder IDs, screenshots, and tokens outside the repository.

1. Connect or restore a saved Google Drive profile and confirm no folder is created automatically.
2. Select **Set Up Drive Folder** and confirm exactly one visible `GameSave Manager Backups` folder appears directly under My Drive.
3. Restart the App and reconnect the same account. Confirm the same folder is reused and no duplicate appears.
4. Rename the folder in Google Drive, select **Check Drive Folder**, and confirm the new display name appears while the saved identity remains linked.
5. Move the folder into another folder in My Drive, check it again, and confirm the App reports **Moved** without creating or moving anything.
6. Trash the folder and confirm the App reports it as missing or trashed without creating a replacement.
7. Restore the folder from trash, check again, and confirm the original folder is reused.
8. Remove or trash it again. Confirm **Recreate Drive Folder** remains blocked until the replacement confirmation is selected.
9. Confirm recreation searches for one unique existing top-level candidate first and creates a replacement only when none exists.
10. If duplicate top-level candidates are visible to this OAuth application, confirm the App reports ambiguity and selects neither.
11. Disconnect and reconnect. Confirm the saved root metadata survives disconnect and is validated again after reconnect.
12. Throughout the test, confirm no child backup-run folder, upload, download, preview, or sync operation occurs.

The first Drive version supports My Drive only. Shared drives, Google Picker, a full folder browser, and custom root selection are not part of this milestone. User backup runs are never placed in hidden application storage.

The `drive.file` scope exposes files and folders created by the App or otherwise made available to it; it does not provide an arbitrary browser over every user-created Drive item. An app-created root can therefore be rediscovered without full Drive access. If arbitrary folder selection is added later, Google Picker is the preferred mechanism while retaining `drive.file`.

Sanitized live development-account verification confirmed that explicit setup reaches **Ready**, persists the app-created root identity, and reuses that root after restart without requesting a broader scope. No account value, folder ID, screenshot, or OAuth value is recorded in the repository.

Development diagnostics for a failed root-folder request are intentionally sanitized. A bug report may include the operation name, HTTP status, allowlisted Google reason, stable error code, and retryable flag. Never include an account email, folder ID, Client ID, client secret, authorization URL, raw response, access token, or refresh token.

## Test the validation-only remote boundary

The Milestone O boundary is read-only. Use an explicitly authorized development account and inspect My Drive before and after each check so that any unexpected mutation is visible.

1. Restore or connect a saved Google Drive profile and confirm its application root is **Ready**.
2. Invoke the validation boundary from a focused development harness or debugger using the selected profile ID. Validation must complete without opening a browser.
3. Confirm the configured root is resolved by its saved ID and that no probe folder, probe file, metadata file, upload, permission change, or other Drive object appears.
4. Rename the root and validate again. The same saved identity must remain valid and only its display name may change through the separate root-folder inspection flow.
5. Move the root beneath another My Drive folder and validate again. The root remains authoritative by ID, while the result reports the established moved state and clears dependent in-memory path entries.
6. Trash the root and validate again. Validation must fail safely, clear the relevant in-memory cache, and must not create a replacement. Restore the root and confirm a later validation succeeds.
7. Revoke the application's authorization through Google Account settings and validate again. Reauthentication must be required without opening a browser automatically; the saved profile and root metadata remain.
8. Confirm operations beyond the Milestone O validation boundary remain unavailable throughout that milestone's checks.
9. Review only sanitized status and warning output. No account value, token, client configuration, folder ID, request URL, query, or raw provider response may appear.

Quota and rate-limit statuses are classified from ordinary failed Drive requests. Validation does not call Drive quota fields or display quota totals.

The provider-neutral remote write contract distinguishes create-only backup content from mutable `.gamesave-sync/sync-log.json` metadata. Local Folder and SFTP implement those semantics. Google Drive has an Infrastructure-only `/`-relative object/path resolver for exact-name lookup, paginated discovery, safe missing-parent creation, duplicate rejection, and validated in-memory ID caching. Its completed Milestone P remote boundary supports root and folder checks, top-level run discovery, bounded text reads, create-only text, and allowlisted provider-metadata reads and exact-ID replacement. It operates only on app-accessible My Drive objects under the existing `drive.file` grant; it does not require broader consent. Child IDs are not persisted in a new database or file.

## Verify Milestone P listing and text metadata

Use only a development account and controlled objects beneath an isolated child folder of the configured application root. Do not place personal backup data in the acceptance area.

1. Restore a connected profile silently and confirm `RootExistsAsync` succeeds without opening a browser.
2. Confirm `FolderExistsAsync` resolves a controlled nested folder beneath the authoritative root.
3. Add one controlled run folder with valid UTF-8 `manifest.json`, one without a manifest, and one with malformed manifest text.
4. Confirm `ListRunFolderNamesAsync` returns the manifest-bearing folders, follows every page, and ignores the folder without a manifest.
5. Confirm `ReadTextFileAsync` returns the valid manifest exactly and the existing `SyncEngine` converts the malformed manifest into `RemoteRunUnreadable` without hiding the valid run.
6. Confirm creating the valid manifest again fails and leaves its existing bytes unchanged.
7. Confirm missing `.gamesave-sync/sync-log.json` metadata reads as absent, the first replacement creates it, and the second replacement updates the same authoritative file ID.
8. Confirm no recursive `ListFilesAsync`, backup upload, backup download, delete, move, rename, trash, or permission operation occurs.
9. Confirm the requested OAuth scope remains exactly `https://www.googleapis.com/auth/drive.file` and inspect sanitized output for the absence of personal account values, object IDs, tokens, queries, or raw responses.

This acceptance was completed with a development account on 2026-08-03. The controlled verification passed, including unchanged metadata identity across replacement and no forbidden Drive mutation. Clean was intentionally not added because deletion and trash operations are outside Milestone P; remove controlled acceptance objects manually in the Drive UI after inspection if desired.

Milestone P did not activate synchronization. Milestone Q subsequently made recursive `ListFilesAsync` available at the Infrastructure remote-filesystem boundary, and Milestone R added create-only `UploadFileAsync`. `DownloadFileAsync` remains explicitly unavailable; `GoogleDriveSyncProvider` does not exist; Google Drive remains configuration-only with `IsImplemented = false`; and Preview Sync and Sync Now remain disabled.

## Verify Milestone Q recursive file listing

The default automated suite uses fake authentication and Drive APIs; it never requires a personal account or mutates external Drive data. Milestone Q acceptance verifies canonical run-relative `/` paths, direct and nested files, empty folders, pagination at every depth, defensive trash handling, exact/case/type collisions, unsupported Workspace objects and shortcuts, repeated identities and cycles, cancellation, deterministic credential/client disposal, read-only request boundaries, unavailable upload/download, absent provider-factory wiring, `IsImplemented = false`, and disabled Preview Sync/Sync Now.

| Acceptance area | Existing deterministic coverage |
| --- | --- |
| Canonical direct/nested paths, separators, ordering, empty folders | `GoogleDriveRecursiveRelativePathTests`, `GoogleDriveOneLevelFileListingServiceTests`, `GoogleDriveRecursiveFileListingServiceTests` |
| Every page at every depth and real remote-filesystem wiring | `GoogleDriveRecursivePaginationIntegrationTests` |
| Trash, exact/case/type collisions, unsupported objects, shortcuts, cycles, repeated IDs | `GoogleDriveFolderChildEnumerationServiceTests`, `GoogleDriveOneLevelFileListingServiceTests`, `GoogleDriveRecursivePaginationIntegrationTests` |
| Cancellation, no partial result, credential/client disposal, cache atomicity | `GoogleDriveRemoteOperationContextTests`, `GoogleDriveRunFolderResolverTests`, `GoogleDriveOneLevelFileListingServiceTests`, `GoogleDriveRecursivePaginationIntegrationTests` |
| Metadata-only/no-mutation boundary and unavailable upload/download | `GoogleDriveObjectApiTests`, `GoogleDriveRemoteFileSystemTests`, `GoogleDriveSyncEngineCompatibilityTests` |
| No provider activation and disabled Preview Sync/Sync Now | `GoogleDriveRemoteFileSystemTests`, `GoogleSdkBoundaryTests`, `GoogleDriveOAuthViewModelTests`, `SyncProviderCapabilityTests` |
| Existing Milestones A-P | Full `Manager/Manager.sln` regression suite |

Live development-account acceptance is intentionally separate because it creates, trashes, and manually removes controlled test objects. Use only a dedicated development test account and one controlled backup-run folder beneath the configured application root. Do not use personal saves, record object IDs, or automate cleanup through the application.

> **Scope constraint confirmed on 2026-08-09.** `https://www.googleapis.com/auth/drive.file` is a per-object grant. This application can enumerate only objects it created or that the user explicitly opened with it. A folder or file added by hand in the Drive UI is **never** returned by `files.list` for this application, even inside the app-created application root. A controlled fixture built manually is therefore invisible to `ListFilesAsync`, and the listing correctly reports the run folder as missing. Steps 2, 7, 8, and 9 below assume manual creation and cannot be executed as written. See `D-023` in `docs/decisions.md`.
>
> Consequences for this checklist:
>
> - every object a live check must see has to be created by the application itself, which currently means folders and small text files through the existing create-only primitives, because binary upload arrives only in Milestone R;
> - manually trashing an **application-created** file still works, because trashing does not revoke the grant, so the trash-exclusion check stays viable;
> - exact-duplicate and case-only-collision fixtures cannot be produced at all: a Drive-UI copy is user-created and therefore invisible, and the create-only text service deliberately refuses to create a colliding sibling. Those two requirements keep their deterministic automated coverage listed above;
> - Google Picker folder authorization would let a user grant folder-and-contents access while keeping `drive.file`, but it is a separate future feature and must not be added to close Milestone Q;
> - never broaden the OAuth scope to make this checklist easier.
>
> Changing how this checklist obtains its fixture relaxes the read-only rule for the acceptance harness and requires explicit user authorization before any agent acts on it.
>
> **Authorization recorded 2026-08-13:** the user selected Option 2. A temporary harness may create one controlled fixture through the existing application create-only folder and small-text primitives, run all reachable live checks, and use deterministic automated coverage for the exact/case collision checks that `drive.file` makes unexecutable. This does not authorize binary upload, broader scope, Picker, automated trash/delete/cleanup, provider activation, or Milestone R work.

For a temporary local acceptance harness, store the private run-folder name and expected file count only in these Windows user environment variables:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_Q21_RUN_FOLDER", "<private run-folder name>", "User")

[Environment]::SetEnvironmentVariable(
    "GAMESAVES_Q21_EXPECTED_FILE_COUNT", "<expected file count>", "User")
```

The harness may read Process first and then User scope, but must never print, serialize, snapshot, or commit either value. Environment-variable names are non-secret; their values remain private local acceptance data. Remove any scratch harness before final verification.

1. Restore the selected profile silently and confirm no browser opens and the OAuth scope remains exactly `https://www.googleapis.com/auth/drive.file`.
2. Through the temporary harness and existing application create-only folder/text primitives, create one controlled run folder containing `manifest.json`, ordinary files at the run root, deeply nested ordinary files, and empty folders.
3. Invoke `GoogleDriveRemoteFileSystem.ListFilesAsync` from a focused development harness or debugger using that saved profile and controlled run-relative folder.
4. Confirm every returned value is relative to the controlled run folder, excludes the run-folder name, and uses `/` as its only separator.
5. Confirm direct and deeply nested files are present and empty folders produce no entry.
6. Place enough controlled objects at the run root and a nested folder to exercise multiple API pages; confirm every expected file is returned.
7. Trash one controlled file in the Drive UI, list again, and confirm the trashed file is absent. Restore or remove it manually afterward.
8. Do not attempt exact-duplicate or case-only-collision fixtures live. Re-run their deterministic automated coverage and record that those live stages are unexecutable under `drive.file`.
9. Confirm the automated collision checks fail closed with sanitized `GoogleDriveFileListingAmbiguous` and `GoogleDriveFileListingCaseCollision` categories and no partial result.
10. Cancel one listing while it is active and confirm no partial result is accepted and a later clean listing still succeeds.
11. Reset the harness observer after authorized fixture creation, then inspect Drive activity and the focused harness: listing must issue no content download, create, update, delete, move, rename, trash, permission, upload, or download request.
12. Confirm `UploadFileAsync` and `DownloadFileAsync` remain unavailable, Google Drive remains absent from `SyncProviderFactory`, `IsImplemented` remains false, and Preview Sync/Sync Now remain disabled.
13. Review sanitized output and logs. They must contain no account value, object or parent ID, page token, query, URL, complete relative path, token, client secret, or raw provider response.

Record only the date, pass/fail result, tested application commit, and sanitized failure categories in the project handoff. Never record the account, folder name/ID, returned personal paths, screenshots, OAuth configuration, or raw logs.

### Recorded Milestone Q acceptance result

```text
Date: 2026-08-13
Tested commit: ba83dead168b15e958807882b703aa0920770770
Result: PASS
Sanitized failure categories: none
```

Requirements that the `drive.file` scope made unexecutable live, recorded as categories rather than skipped silently:

- exact-duplicate sibling fixture, expected sanitized category `GoogleDriveFileListingAmbiguous`;
- case-only-collision sibling fixture, expected sanitized category `GoogleDriveFileListingCaseCollision`.

Both retain deterministic automated coverage in `GoogleDriveOneLevelFileListingServiceTests` and `GoogleDriveRecursivePaginationIntegrationTests`, which passed 60 tests during acceptance. Neither was attempted live and neither was faked. See `D-023` in `docs/decisions.md` for why a live fixture for them cannot exist under this scope.

The controlled fixture was created by the application through the existing create-only folder and text primitives. The only manual Drive-UI actions were trashing one application-created file and restoring it; the application issued no trash, restore, delete, rename, move, share, or permission request at any point. The complete sanitized attempt record, including the reusable harness design, is `docs/q21-live-attempt-log.md`.

## Verify Milestone R create-only uploads

Milestone R adds one create-only streamed binary upload behind
`GoogleDriveRemoteFileSystem.UploadFileAsync`. The default automated suite is
hermetic: it uses fake authentication, a fake Drive object client, and a fake
media API, and never touches a real account, browser, token, or network.

### Automated requirement coverage

| Milestone R requirement | Deterministic coverage |
| --- | --- |
| Internal upload request/result contracts, safe formatting, and Core/App isolation | `GoogleDriveUploadContractTests` |
| Stable local source validation, read-only sharing, captured length, disposal, and no local path in errors | `GoogleDriveUploadSourceTests` |
| Fakeable media-client boundary, one short-lived `DriveService`, deterministic disposal | `GoogleDriveMediaUploadClientTests.Factory_CreatesDistinctShortLivedSdkClients`, `GoogleDriveMediaUploadClientTests.SdkClient_DisposesOwnedDriveServiceExactlyOnce` |
| Restricted resumable `files.create` request, exact response fields, no conversion or indexing flags, default chunking | `GoogleDriveMediaUploadClientTests.SdkAdapter_BuildsRestrictedCreateRequestAndMapsOnlyProjectState`, `GoogleDriveMediaUploadClientTests.SdkAdapter_UsesResumableCreateWithDefaultChunkingForEverySize` |
| Completed-response validation for identity, exact name, opaque MIME, single expected parent, non-trashed My Drive location, and exact size | `GoogleDriveUploadResponseValidatorTests` |
| Create-only target guard across exact, case-only, type, and cross-page collisions, rechecked inside the creation lease | `GoogleDriveCreateOnlyUploadTargetGuardTests`, `GoogleDriveUploadIntegrationTests.Upload_RefusesEveryWindowsEquivalentTarget`, `GoogleDriveUploadIntegrationTests.Upload_RefusesACollisionThatOnlyAppearsOnALaterPage` |
| Case-safe parent preparation through authoritative IDs, validated create responses, and no colliding parent | `GoogleDriveUploadParentPreparationServiceTests`, `GoogleDriveUploadIntegrationTests.Upload_TravelsTheWholeCompositionAndCreatesNestedParents` |
| One-file orchestration that preserves each stage category and touches no run, provider, or UI state | `GoogleDriveUploadServiceTests.UploadAsync_ComposesOneCompleteFileUpload`, `GoogleDriveUploadServiceTests.ServiceSource_HasNoRunProviderWiringOrAdjacentWork` |
| Streamed upload with no eager buffering, bounded reads, and zero-byte support | `GoogleDriveUploadServiceTests.UploadAsync_PassesOpenedStreamDirectlyWithoutMaterializing`, `GoogleDriveUploadServiceTests.UploadAsync_LargeStreamUsesBoundedReadsWithoutEagerCopy`, `GoogleDriveUploadServiceTests.UploadAsync_PreservesZeroByteStreamPositionAndLifetime` |
| Validated completed byte accounting and the unchanged `SyncProgress` contract | `GoogleDriveUploadServiceTests.UploadAsync_ReturnsOpenedLengthInsteadOfPlannedLength`, `SyncEngineTests` |
| Cancellation at every boundary, never a provider failure or partial success | `GoogleDriveUploadServiceTests.UploadAsync_ForwardsCallerTokenToEveryAsyncBoundary`, `GoogleDriveUploadServiceTests.CancellationAtEitherTargetGuard_StopsBeforeUpload`, `GoogleDriveUploadIntegrationTests.Upload_CancellationLeavesNoRemoteFileAndNoCacheEntry` |
| Indeterminate completion that never becomes success, cache, retry, or cleanup | `GoogleDriveUploadServiceTests.LostCompletionResponse_ReturnsIndeterminateWithoutRetry`, `GoogleDriveUploadServiceTests.LateResponseAfterBytesAccepted_ProducesCancellationOnly` |
| Exactly-once disposal of stream, lease, context, credential, and media client on all outcomes | `GoogleDriveUploadServiceTests.UploadAsync_DisposesEveryOwnedResourceExactlyOnce`, `GoogleDriveUploadServiceTests.Cancellation_WaitsForLateProviderReturnThenDisposesAllWork` |
| Cache commit only after validation, precise eviction, and affected-profile invalidation | `GoogleDriveUploadServiceTests.CacheRejection_NeverReturnsUploadSuccess`, `GoogleDriveUploadServiceTests.ConfirmedMissingTarget_EvictsOnlyItsPreciseStaleEntry`, `GoogleDriveUploadServiceTests.Reauthentication_InvalidatesOnlyAffectedProfile`, `GoogleDriveObjectIdCacheTests` |
| One sanitized failure boundary, the fixed category matrix, distinct stable codes, and no private value in any message, `ToString()`, or wrapped exception | `GoogleDriveUploadFailureMapperTests` |
| Zero, small, boundary, larger-than-5-MiB, and multi-chunk sizes on one resumable path with exact bytes and monotonic progress | `GoogleDriveUploadServiceTests.EverySize_UsesOneResumableBoundaryAndPreservesExactBytes`, `GoogleDriveMediaUploadClientTests.ChunkProgress_NeverRegressesAndOnlyCompletesAtFullLength` |
| Only `UploadFileAsync` wired, returning the validated completed byte count, with download and provider activation still unavailable | `GoogleDriveRemoteFileSystemTests`, `GoogleDriveUploadIntegrationTests.Composition_KeepsDownloadAndProviderActivationUnavailable` |
| `SyncEngine` manifest-last ordering and preserved incomplete runs | `GoogleDriveSyncEngineCompatibilityTests.Upload_CreatesEveryPayloadBeforeTheRootManifest`, `GoogleDriveSyncEngineCompatibilityTests.PayloadFailure_LeavesAnIncompleteRunWithoutAManifest`, `GoogleDriveSyncEngineCompatibilityTests.ManifestFailure_KeepsPayloadsAndNeverRepairsTheRun` |
| Dependency injection to media client integration and no forbidden Drive operation | `GoogleDriveUploadIntegrationTests` |
| Existing Milestones A-Q, Local Folder, and SFTP behaviour | Full `Manager/Manager.sln` regression suite |

### Recorded Milestone R automated verification

```text
Date: 2026-08-16
Tested commit: 5a7f56b8e341bb540a1a7a935dcb8d527c068ec0
Release suite: 1,431 passed, 0 failed, 0 skipped
Release build: succeeded, 2 known Reviewer warnings, 0 errors
Direct package baseline: unchanged
Vulnerable: SSH.NET 2024.2.0, High, GHSA-q939-rpr3-3284
Deprecated: xUnit 2.9.3 (Legacy)
```

### Recorded Milestone R live acceptance attempt

```text
Date: 2026-08-16
Tested commit: 5a7f56b8e341bb540a1a7a935dcb8d527c068ec0, plus the temporary harness
Result: FAIL
Sanitized failure categories: R20_MANIFEST_LAST:GoogleDriveUploadMimeTypeMismatch
```

Stages that passed live: silent authentication restore with no browser and a
reachable configured root; zero-byte, small, larger-than-5-MiB, and deeply
nested create-only uploads returning exact byte counts with their missing
parents created; exact-name and case-only retries refused without overwriting;
cancellation of an active upload reporting no success; and download plus
provider activation still unavailable.

The single failure was real and is the reason live acceptance exists. Google
Drive stored its own media type for the uploaded `manifest.json` instead of
echoing the requested `application/octet-stream`, and the upload response
validator required an exact echo, so a valid completed create was rejected.
Every `.bin` payload in the same run passed, which isolates the cause to the
provider assigning a type for a known extension. Because `SyncEngine` uploads
the root `manifest.json` last for every run, every real Drive sync would have
failed at its manifest.

The validator now requires the completed response to remain an ordinary
uploaded blob, judged by the existing classification policy, rather than an
exact opaque-type echo. Folders, Google Workspace documents, shortcuts, and
malformed types still fail closed with the unchanged
`GoogleDriveUploadMimeTypeMismatch` code. See `D-025` in `docs/decisions.md`.
The request still asks for `application/octet-stream`, unchanged from `D-019`.

The re-run used a new controlled run-folder name, because the attempt above
already created its folder, its payloads, and its manifest object; those
objects are harmless, the application never removes them, and deleting them is
a manual Drive-UI action. Its result is recorded next.

### Recorded Milestone R live acceptance result

```text
Date: 2026-08-16
Tested commit: 48d4d4db33a9e369d69b7e55a07df83eeee8516b
Result: PASS
Sanitized failure categories: none
```

Every stage passed: silent authentication restore with a reachable configured
root; zero-byte, small, larger-than-5-MiB, and deeply nested create-only
uploads returning exact byte counts with their missing parents created;
exact-name and case-only retries refused without overwriting; cancellation of
an active upload reporting no success; the run becoming discoverable only after
its `manifest.json` existed; and download plus provider activation still
unavailable.

Two checklist observations are operator-visual and were not separately
reported: whether a browser window opened, and whether Drive activity showed
any operation other than creates. Both are guaranteed structurally rather than
observationally. Ordinary operations never open a browser under `D-005`, and
the upload composition can only issue get, list, folder-create, and one media
create, which
`GoogleDriveUploadIntegrationTests.UploadComposition_IssuesNoForbiddenDriveOperation`
proves at the interface level.

The controlled objects from both attempts remain in the development account.
The application never removes them; deleting them is a manual Drive-UI action.

**Milestone R is closed.**

### Live upload acceptance checklist

This checklist requires explicit user authorization before any agent acts on
it, because it writes controlled synthetic objects to a real development
account. It is **not** part of the automated suite. It was run twice on
2026-08-16: the first attempt failed as recorded above, and the re-run after
the `D-025` fix passed as recorded below.

Use only an explicitly authorized development test account and one controlled
run folder beneath the configured application root. Use synthetic data only,
never a personal save.

1. Restore the selected profile silently and confirm no browser opens and the requested scope remains exactly `https://www.googleapis.com/auth/drive.file`.
2. Upload one zero-byte file, one small file, one file larger than 5 MiB, and one deeply nested file, and confirm each returns its exact byte count.
3. Confirm every missing parent segment was created once, in parent-first order, beneath the authoritative root.
4. Retry one upload to an existing exact name and confirm it is refused with no overwrite, update, or rename.
5. Retry one upload to a case-only variant of an existing name and confirm the same refusal.
6. Cancel one upload in progress and confirm no success is reported, no cache entry is kept, and no delete or trash request is issued.
7. Upload a complete run through the existing sync path and confirm `manifest.json` is created last.
8. Interrupt a run before its manifest and confirm the remote folder stays present, is not discoverable as a completed run, and is never cleaned up automatically.
9. Inspect Drive activity and confirm no update, delete, trash, rename, move, share, permission, download, or provider-activation request occurred.
10. Review sanitized output and confirm it contains no account value, object or parent ID, page token, query, upload or session URL, local path, remote name, token, or raw provider response.

Remove controlled objects manually in the Drive UI afterwards; the application
has no cleanup behaviour and must never gain one for acceptance convenience.

Record only the date, tested commit, pass or fail, and sanitized failure
categories. Milestone R stays open until that live result exists.

## Verify Milestone S no-overwrite downloads

Milestone S adds one streamed, verified, no-overwrite download behind
`GoogleDriveRemoteFileSystem.DownloadFileAsync`. The default automated suite is
hermetic: fake authentication, a fake Drive object client, and a fake media
API, with no real account, browser, token, or network.

### Automated requirement coverage

| Milestone S requirement | Deterministic coverage |
| --- | --- |
| Internal download request/result contracts, safe formatting, and Core/App isolation | `GoogleDriveDownloadContractTests` |
| Destination refused when occupied, missing directory created, one exclusive temporary sibling, no local path in errors | `GoogleDriveDownloadDestinationTests` |
| Fakeable media boundary, one short-lived `DriveService`, one download per client, deterministic disposal | `GoogleDriveMediaDownloadClientTests.Factory_CreatesDistinctShortLivedSdkClients`, `GoogleDriveMediaDownloadClientTests.SdkClient_DisposesOwnedDriveServiceExactlyOnce` |
| Read-only media request and metadata limited to the validated field set | `GoogleDriveMediaDownloadClientTests.SdkAdapter_BuildsAReadOnlyMediaRequest`, `GoogleDriveMediaDownloadClientTests.SdkAdapter_RequestsOnlyTheMetadataDownloadValidates` |
| Source resolved to one authoritative blob; missing, ambiguous, case-colliding, wrongly typed, and unsupported sources fail closed | `GoogleDriveDownloadSourceResolverTests`, `GoogleDriveDownloadIntegrationTests.Download_FailsClosedForEveryUnsafeSource` |
| Streamed transfer with no payload copy, bounded writes, and zero-byte support | `GoogleDriveDownloadStreamingTests.Streaming_HandsTheDestinationStreamToTheClientUnchanged`, `GoogleDriveDownloadStreamingTests.ContentThatFailsAnEagerCopy_StillDownloads`, `GoogleDriveDownloadStreamingTests.LargeContent_IsWrittenInBoundedChunksWithExactBytes` |
| Completed length validated against the authoritative source size before placement | `GoogleDriveDownloadCompletionValidatorTests`, `GoogleDriveDownloadResilienceTests.TruncatedBody_FailsClosedWithoutPlacing` |
| Atomic placement that never overwrites an existing file or directory | `GoogleDriveDownloadPlacementTests.ValidatedTemporaryFile_MovesToItsFinalName`, `GoogleDriveDownloadPlacementTests.DestinationThatAppearedDuringTheTransfer_IsNeverOverwritten`, `GoogleDriveDownloadIntegrationTests.Download_RefusesAnExistingDestinationAndLeavesItUntouched` |
| Temporary-file removal on every failure path, scoped to this operation's own file | `GoogleDriveDownloadCleanupTests`, `GoogleDriveDownloadCleanupTests.Cleanup_RefusesAnyPathThatIsNotOneOfItsTemporaryFiles` |
| Cancellation at every boundary with the caller token forwarded everywhere | `GoogleDriveDownloadServiceTests.Cancellation_AtEveryBoundaryLeavesNoLocalFile`, `GoogleDriveDownloadServiceTests.DownloadAsync_ForwardsTheCallerTokenToEveryProviderCall` |
| One sanitized failure boundary, distinct stable codes, and no private value in any message, `ToString()`, or log | `GoogleDriveDownloadFailureMapperTests`, `GoogleDriveDownloadFailureMapperTests.EscapingFailureSurfaces_ExposeNoPrivateValue`, `GoogleDriveDownloadFailureMapperTests.LifecycleLogging_WritesOnlyStagesCodesAndByteCounts` |
| Exactly-once disposal of stream, client, context, and credential on every outcome | `GoogleDriveDownloadResilienceTests.EveryOutcome_ReleasesEachOwnedResourceExactlyOnce`, `GoogleDriveDownloadResilienceTests.LateReturningProvider_LeavesNoBackgroundWorkBehind` |
| Size matrix from zero bytes to larger than 10 MiB on one boundary with exact bytes | `GoogleDriveDownloadResilienceTests.EverySize_TakesTheSameBoundaryAndPreservesExactBytes`, `GoogleDriveDownloadIntegrationTests.Download_PreservesExactBytesForEverySize` |
| Disk-full, network interruption, and interrupted-run data safety | `GoogleDriveDownloadResilienceTests.DiskFullDuringTransfer_LeavesNoLocalFileAndStaysSanitized`, `GoogleDriveDownloadResilienceTests.NetworkInterruption_IsReportedAsTemporarilyUnavailable`, `GoogleDriveDownloadResilienceTests.OrphanTemporaryFileFromAnInterruptedRun_IsNeverTouched`, `GoogleDriveDownloadResilienceTests.InterruptedRun_LeavesTheExistingLocalRunUntouched` |
| Only `DownloadFileAsync` wired, returning validated bytes, with provider activation still absent | `GoogleDriveRemoteFileSystemTests`, `GoogleDriveDownloadIntegrationTests.DownloadAndUpload_ShareOneRemoteBoundaryWithoutInterfering` |
| Manifest rewriting identical to Local Folder, SHA-256 kept as content identity, interrupted runs never presented as complete | `GoogleDriveSyncEngineCompatibilityTests.Download_RewritesTheManifestExactlyLikeLocalFolderDoes`, `GoogleDriveSyncEngineCompatibilityTests.DownloadedRun_IsDiscoverableAndPassesSha256Verification`, `GoogleDriveSyncEngineCompatibilityTests.TamperedRemoteContent_DownloadsButFailsSha256Verification`, `GoogleDriveSyncEngineCompatibilityTests.InterruptedDownload_LeavesNoRunPresentedAsComplete`, `GoogleDriveSyncEngineCompatibilityTests.RemoteRunWithoutAManifest_IsNeverOfferedForDownload` |
| Dependency injection to media client integration and no forbidden Drive operation | `GoogleDriveDownloadIntegrationTests`, `GoogleDriveDownloadIntegrationTests.DownloadComposition_IssuesNoForbiddenDriveOperation` |
| Existing Milestones A-R, Local Folder, and SFTP behaviour | Full `Manager/Manager.sln` regression suite |

### Recorded Milestone S automated verification

```text
Date: 2026-08-16
Tested tree: Milestone S Tasks 1-16 on top of 174bc7b
Release suite: 1,631 passed, 0 failed, 0 skipped
Release build: succeeded, 0 errors, 5 known pre-existing backlog warnings
               (3x CA1416 in RegistrySteamLocator.cs, 2x obsolete
               Avalonia TextBox.Watermark in the Reviewer); a full
               --no-incremental rebuild is the number recorded here
Direct package baseline: unchanged
Banned legacy packages: none present
Vulnerable: SSH.NET 2024.2.0, High, GHSA-q939-rpr3-3284
Deprecated: xUnit 2.9.3 (Legacy)
Sandbox limitation: none; all four package commands completed
```

### Live download acceptance, recorded

This checklist needed explicit user authorization, because it reads a real
development account and writes controlled files to a local temporary folder. It
is **not** part of the automated suite. The user authorized and ran it on
2026-08-17 and it passed on the first attempt:

```text
Date: 2026-08-17
Tested commit: 174bc7b0a1d7730c8ebdf68f4bb89d0239eb602c, plus the uncommitted
               Milestone S Tasks 6-16 working tree
               (that tree is now the commit bf49729e52888e2c2ba9d614e6555b47e295f76b)
Result: PASS
Sanitized failure categories: none
```

Downloads only read Drive, and they never overwrite a local file, so this run
is less invasive than the Milestone R upload acceptance. Use only an explicitly
authorized development test account and controlled synthetic objects.

1. Restore the selected profile silently and confirm no browser opens and the requested scope remains exactly `https://www.googleapis.com/auth/drive.file`.
2. Download one zero-byte file, one small file, one file larger than 5 MiB, and one deeply nested file, and confirm each returns its exact byte count and matches the remote content.
3. Confirm each download lands at its final name only after completion, and that no `.gsdownload` temporary file remains anywhere in the destination folder.
4. Retry one download to a destination that already exists and confirm it is refused with no overwrite and the existing bytes unchanged.
5. Cancel one download in progress and confirm no final file appears, no temporary file remains, and the existing local data is untouched.
6. Download a complete run through the existing sync path and confirm the manifest is rewritten, the run appears in the Backups list, and its SHA-256 verification passes.
7. Interrupt a run download before its manifest and confirm the partial run is not offered as a complete backup and nothing is cleaned up automatically.
8. Inspect Drive activity and confirm only metadata and media reads occurred: no create, update, delete, trash, rename, move, share, permission, or provider-activation request.
9. Review sanitized output and confirm it contains no account value, object or parent ID, page token, query, media URL, local path, remote name, token, or raw provider response.

Steps 1 to 5, 8, and 9 were exercised by the run: silent authentication
restore with a reachable configured root, every file in the controlled run
downloaded with its placed length matching its reported byte count, no
surviving `.gsdownload` temporary file anywhere in the destination, a second
download to an existing destination refused as
`GoogleDriveDownloadDestinationExists` with the existing bytes unchanged, a
cancelled download placing no file, and Google Drive still reporting
`IsImplemented = false`. Steps 6 and 7 are covered deterministically by
`GoogleDriveSyncEngineCompatibilityTests`, because a full sync-path run
download needs the provider wrapper that Milestone T adds.

Whether a browser opened and whether Drive activity showed any non-read
operation are operator-visual observations that were not separately reported,
so neither is claimed as observed. Both are guaranteed structurally: ordinary
operations never open a browser under `D-005`, and the download composition can
only issue a metadata get, a bounded child list, and one media get.

The temporary harness used for the run was deleted afterwards and never
committed. Controlled objects remain in the development account; the
application never removes them. Milestone S is closed.

## Handle downloaded credential files

If Google offers a downloaded OAuth client JSON:

1. Download it only to a local temporary or developer configuration directory.
2. Never place it anywhere in the repository working tree.
3. Never rename it into a committed appsettings file.
4. Never send it through an issue, pull request, chat, screenshot, or CI log.
5. Delete unused copies.
6. Treat the Client ID as configuration.
7. Do not treat the desktop client secret as a reliable confidential credential.
8. Never store user OAuth tokens in the downloaded client file.

The recommended local directory is outside the repository:

```text
%LOCALAPPDATA%\GameSave\Developer\GoogleOAuth\
%LOCALAPPDATA%\GameSave\Developer\GoogleOAuth\desktop-client.json
```

Application token data belongs in the existing secure secret store, not beside this file.

## Repository ignore protections

The project-specific `.gitignore` section blocks common downloaded and developer-local file locations:

- `**/client_secret_*.json` protects Google's common downloaded client filename pattern anywhere in the working tree.
- `Manager/GameSaves.App/credentials.json` protects a common but disallowed credential filename.
- `Manager/GameSaves.App/google-oauth-client.local.json` protects a local desktop-client configuration.
- `Manager/GameSaves.App/google-drive.local.json` protects a local Drive configuration.
- `Manager/GameSaves.App/.google-oauth/` protects local OAuth working data.
- `Manager/GameSaves.App/google-oauth-token-cache/` protects a local token-cache directory.

The preferred workflow is still to store OAuth files outside the repository. Ignore rules are only a second line of defence and do not make a credential safe.

## If sensitive data is exposed

If a credential, token, or personal account value is accidentally committed:

1. Stop using the exposed credential or token.
2. Revoke affected user OAuth tokens where applicable.
3. Delete or rotate the affected OAuth client when appropriate.
4. Remove the file from the current Git tree.
5. Treat it as exposed even if the commit was quickly reverted.
6. Review repository history and any remote forks or copies.
7. Do not assume adding the path to `.gitignore` repairs the exposure.
8. Never paste the exposed value into an issue while asking for help.

Rewriting Git history can reduce continued exposure, but it does not revoke a credential or token.

## Before committing Google Drive work

- [ ] No `client_secret_*.json` file is tracked
- [ ] No downloaded OAuth credential JSON is tracked
- [ ] No access token is present
- [ ] No refresh token is present
- [ ] No personal Google account email is present
- [ ] No personal Google Drive folder ID is present
- [ ] Examples contain placeholders only
- [ ] Local OAuth files are ignored
- [ ] `git diff` contains no credentials
- [ ] `git status --ignored` shows local OAuth files as ignored

## Official references

- [Create and manage Google Cloud projects](https://docs.cloud.google.com/resource-manager/docs/creating-managing-projects)
- [Enable Google Cloud APIs](https://docs.cloud.google.com/apis/docs/getting-started#enabling_apis)
- [Configure OAuth consent and scopes](https://developers.google.com/workspace/guides/configure-oauth-consent)
- [Create a desktop OAuth client](https://developers.google.com/workspace/guides/create-credentials#desktop-app)
- [OAuth 2.0 for desktop applications](https://developers.google.com/identity/protocols/oauth2/native-app)
- [OAuth authorization best practices](https://developers.google.com/identity/protocols/oauth2/resources/best-practices)
- [Choose Google Drive API scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [Get Drive file metadata by ID](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/get)
- [List Drive files with queries and pagination](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list)
- [Create and populate Drive folders](https://developers.google.com/workspace/drive/api/guides/folder)
