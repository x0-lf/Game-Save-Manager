using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using System.Collections.Concurrent;

namespace GameSaves.Infrastructure.GoogleDrive
{
    public sealed class GoogleDriveRootFolderService : IGoogleDriveRootFolderService
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IGoogleDriveAuthorizedSessionFactory _sessionFactory;
        private readonly IGoogleDriveRootFolderApi _folderApi;
        private readonly IUtcClock _clock;
        private readonly ConcurrentDictionary<Guid, byte> _activeOperations = new();

        internal GoogleDriveRootFolderService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IGoogleDriveAuthorizedSessionFactory sessionFactory,
            IGoogleDriveRootFolderApi folderApi,
            IUtcClock clock)
        {
            _profileRepository = profileRepository;
            _secretStore = secretStore;
            _sessionFactory = sessionFactory;
            _folderApi = folderApi;
            _clock = clock;
        }

        public Task<GoogleDriveRootFolderResult> InspectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                remoteProfileId,
                RootOperation.Inspect,
                GoogleDriveRootFolderRecreationConfirmation.NotConfirmed,
                cancellationToken);

        public Task<GoogleDriveRootFolderResult> EnsureAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                remoteProfileId,
                RootOperation.Ensure,
                GoogleDriveRootFolderRecreationConfirmation.NotConfirmed,
                cancellationToken);

        public Task<GoogleDriveRootFolderResult> RecreateAsync(
            Guid remoteProfileId,
            GoogleDriveRootFolderRecreationConfirmation confirmation,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                remoteProfileId,
                RootOperation.Recreate,
                confirmation,
                cancellationToken);

        private async Task<GoogleDriveRootFolderResult> RunAsync(
            Guid profileId,
            RootOperation operation,
            GoogleDriveRootFolderRecreationConfirmation confirmation,
            CancellationToken cancellationToken)
        {
            if (profileId == Guid.Empty)
                return ProfileNotFound(profileId);

            if (operation == RootOperation.Recreate &&
                confirmation != GoogleDriveRootFolderRecreationConfirmation.Confirmed)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.RecreationConfirmationRequired,
                    profileId,
                    RequiresRecreationConfirmation: true,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.ConfirmationRequired,
                    Message: "Confirm creating or selecting a replacement Google Drive root folder first.");
            }

            if (!_activeOperations.TryAdd(profileId, 0))
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.OperationInProgress,
                    Message: "Another Google Drive root-folder operation is already running for this profile.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncRemoteProfile? profile = SafeGetProfile(profileId);

                if (profile is null)
                    return ProfileNotFound(profileId);

                if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
                {
                    return new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Failed,
                        profileId,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.WrongProvider,
                        Message: "The selected remote profile is not a Google Drive profile.");
                }

                if (profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings ||
                    settings.SchemaVersion != GoogleDriveSyncRemoteSettings.CurrentSchemaVersion ||
                    !string.Equals(
                        settings.RequestedScope,
                        GoogleDriveAuthorizationScopes.DriveFile,
                        StringComparison.Ordinal))
                {
                    return new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Failed,
                        profileId,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.AuthenticationRequired,
                        Message: "The saved Google Drive profile settings are invalid.");
                }

                GoogleDriveAuthorizedSession session;

                try
                {
                    session = await _sessionFactory.RestoreAsync(
                        profile,
                        cancellationToken);
                }
                catch (GoogleDriveAuthorizedSessionException ex)
                {
                    return MapSessionFailure(profileId, ex.Failure);
                }

                using (session.Credential)
                {
                    if (!string.IsNullOrWhiteSpace(profile.RemoteFolderId))
                    {
                        GoogleDriveRootFolderResult validation =
                            await ValidateStoredFolderAsync(
                                profile,
                                session.Credential,
                                cancellationToken);

                        if (validation.Succeeded || operation != RootOperation.Recreate)
                            return validation;

                        if (!validation.RequiresRecreationConfirmation)
                            return validation;
                    }
                    else if (operation == RootOperation.Recreate)
                    {
                        return new GoogleDriveRootFolderResult(
                            GoogleDriveRootFolderStatus.Unconfigured,
                            profileId,
                            DisplayName: profile.RemoteRootDisplayName,
                            ErrorCode: GoogleDriveRootFolderErrorCodes.Missing,
                            Message: "No saved Google Drive root folder exists. Use initial folder setup instead.");
                    }

                    return operation switch
                    {
                        RootOperation.Inspect => await DiscoverAsync(
                            profile,
                            session.Credential,
                            createWhenMissing: false,
                            repeatSearchBeforeCreate: false,
                            cancellationToken),
                        RootOperation.Ensure => await DiscoverAsync(
                            profile,
                            session.Credential,
                            createWhenMissing: true,
                            repeatSearchBeforeCreate: true,
                            cancellationToken),
                        RootOperation.Recreate => await DiscoverAsync(
                            profile,
                            session.Credential,
                            createWhenMissing: true,
                            repeatSearchBeforeCreate: false,
                            cancellationToken),
                        _ => throw new InvalidOperationException()
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Cancelled,
                    Message: "The Google Drive root-folder operation was cancelled. No backup data was changed.");
            }
            catch
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Failed,
                    Message: "The Google Drive root folder could not be checked.");
            }
            finally
            {
                _activeOperations.TryRemove(profileId, out _);
            }
        }

        private async Task<GoogleDriveRootFolderResult> ValidateStoredFolderAsync(
            SyncRemoteProfile profile,
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            GoogleDriveFolderMetadata folder;

            try
            {
                folder = await _folderApi.GetFolderByIdAsync(
                    credential,
                    profile.RemoteFolderId!,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (GoogleDriveRootFolderApiException ex) when (
                ex.Failure == GoogleDriveRootFolderApiFailure.NotFound)
            {
                TouchSuccessfulConnection(profile.Id);
                return InvalidStoredFolder(
                    profile,
                    GoogleDriveRootFolderStatus.Missing,
                    GoogleDriveRootFolderErrorCodes.Missing,
                    "The saved Google Drive root folder is missing or inaccessible. Confirm replacement before creating or selecting another folder.");
            }
            catch (GoogleDriveRootFolderApiException ex)
            {
                return await MapApiFailureAsync(profile, ex.Details, cancellationToken);
            }

            TouchSuccessfulConnection(profile.Id);

            if (folder.Trashed)
            {
                return InvalidStoredFolder(
                    profile,
                    GoogleDriveRootFolderStatus.Trashed,
                    GoogleDriveRootFolderErrorCodes.Trashed,
                    "The saved Google Drive root folder is in the trash. Restore it or explicitly confirm a replacement.");
            }

            if (!string.Equals(
                    folder.MimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal))
            {
                return InvalidStoredFolder(
                    profile,
                    GoogleDriveRootFolderStatus.WrongType,
                    GoogleDriveRootFolderErrorCodes.WrongType,
                    "The saved Google Drive root identity no longer refers to a folder. Confirm replacement before continuing.");
            }

            if (!string.IsNullOrWhiteSpace(folder.DriveId))
            {
                return InvalidStoredFolder(
                    profile,
                    GoogleDriveRootFolderStatus.UnsupportedLocation,
                    GoogleDriveRootFolderErrorCodes.UnsupportedLocation,
                    "The saved Google Drive root folder is in a shared drive, which is not supported by this version. Confirm a replacement in My Drive.");
            }

            if (string.IsNullOrWhiteSpace(folder.Id))
            {
                return InvalidStoredFolder(
                    profile,
                    GoogleDriveRootFolderStatus.Missing,
                    GoogleDriveRootFolderErrorCodes.Missing,
                    "Google Drive returned incomplete folder metadata. Confirm replacement before continuing.");
            }

            bool isTopLevel;

            try
            {
                isTopLevel = await _folderApi.IsDirectChildOfMyDriveRootAsync(
                    credential,
                    folder.Id,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (GoogleDriveRootFolderApiException ex)
            {
                return await MapApiFailureAsync(profile, ex.Details, cancellationToken);
            }

            bool moved = !isTopLevel;
            string displayName = NormalizeDisplayName(folder.Name);
            GoogleDriveRootFolderResult persistence = PersistFolder(
                profile,
                folder.Id,
                displayName,
                moved ? GoogleDriveRootFolderStatus.Moved : GoogleDriveRootFolderStatus.Ready,
                wasCreated: false,
                wasDiscovered: false,
                wasValidatedById: true,
                wasMoved: moved);

            if (!persistence.Succeeded)
                return persistence;

            return persistence with
            {
                Message = moved
                    ? "The Google Drive backup folder moved within My Drive and remains linked by its authoritative folder ID."
                    : !string.Equals(
                        profile.RemoteRootDisplayName,
                        displayName,
                        StringComparison.Ordinal)
                        ? "The Google Drive backup folder was renamed and remains linked by its authoritative folder ID."
                        : "The Google Drive backup folder is ready."
            };
        }

        private async Task<GoogleDriveRootFolderResult> DiscoverAsync(
            SyncRemoteProfile profile,
            GoogleAuthorizedCredential credential,
            bool createWhenMissing,
            bool repeatSearchBeforeCreate,
            CancellationToken cancellationToken)
        {
            GoogleDriveRootFolderResult? discovered = await TryDiscoverAsync(
                profile,
                credential,
                cancellationToken);

            if (discovered is not null)
                return discovered;

            if (!createWhenMissing)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Unconfigured,
                    profile.Id,
                    DisplayName: profile.RemoteRootDisplayName,
                    Message: "No accessible application backup folder was found. Select Set Up Drive Folder to create it.");
            }

            if (repeatSearchBeforeCreate)
            {
                discovered = await TryDiscoverAsync(
                    profile,
                    credential,
                    cancellationToken);

                if (discovered is not null)
                    return discovered;
            }

            return await CreateAsync(profile, credential, cancellationToken);
        }

        /// <summary>
        /// Returns null only when discovery completed successfully with no
        /// candidates. Every other outcome is explicit.
        /// </summary>
        private async Task<GoogleDriveRootFolderResult?> TryDiscoverAsync(
            SyncRemoteProfile profile,
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<GoogleDriveFolderMetadata> candidates;

            try
            {
                candidates = await _folderApi.FindTopLevelFoldersByNameAsync(
                    credential,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (GoogleDriveRootFolderApiException ex)
            {
                return await MapApiFailureAsync(profile, ex.Details, cancellationToken);
            }

            TouchSuccessfulConnection(profile.Id);

            GoogleDriveFolderMetadata[] usable = candidates
                .Where(folder =>
                    !folder.Trashed &&
                    string.Equals(
                        folder.MimeType,
                        GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(folder.DriveId) &&
                    !string.IsNullOrWhiteSpace(folder.Id))
                .GroupBy(folder => folder.Id!, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();

            if (usable.Length == 0)
                return null;

            if (usable.Length > 1)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Ambiguous,
                    profile.Id,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Ambiguous,
                    Message: "More than one accessible “GameSave Manager Backups” folder exists in My Drive. Resolve the duplicates in Google Drive, then check again.");
            }

            GoogleDriveFolderMetadata folder = usable[0];
            return PersistFolder(
                profile,
                folder.Id!,
                NormalizeDisplayName(folder.Name),
                GoogleDriveRootFolderStatus.Ready,
                wasCreated: false,
                wasDiscovered: true,
                wasValidatedById: false,
                wasMoved: false) with
            {
                Message = "The existing Google Drive backup folder was discovered and linked by its folder ID."
            };
        }

        private async Task<GoogleDriveRootFolderResult> CreateAsync(
            SyncRemoteProfile profile,
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            GoogleDriveFolderMetadata folder;

            try
            {
                folder = await _folderApi.CreateTopLevelFolderAsync(
                    credential,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (GoogleDriveRootFolderApiException ex)
            {
                GoogleDriveRootFolderResult mapped =
                    await MapApiFailureAsync(profile, ex.Details, cancellationToken);
                return mapped with
                {
                    ErrorCode = mapped.Status == GoogleDriveRootFolderStatus.Failed
                        ? GoogleDriveRootFolderErrorCodes.CreationFailed
                        : mapped.ErrorCode,
                    Message = mapped.Status == GoogleDriveRootFolderStatus.Failed
                        ? "The Google Drive backup folder could not be created."
                        : mapped.Message
                };
            }

            TouchSuccessfulConnection(profile.Id);

            if (string.IsNullOrWhiteSpace(folder.Id) ||
                string.IsNullOrWhiteSpace(folder.Name) ||
                folder.Name.Length > 255 ||
                folder.Trashed ||
                !string.Equals(
                    folder.MimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(folder.DriveId))
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profile.Id,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.CreationFailed,
                    Message: "Google Drive returned invalid metadata for the new backup folder.");
            }

            GoogleDriveRootFolderResult result = PersistFolder(
                profile,
                folder.Id,
                NormalizeDisplayName(folder.Name),
                GoogleDriveRootFolderStatus.Ready,
                wasCreated: true,
                wasDiscovered: false,
                wasValidatedById: false,
                wasMoved: false,
                createdRemoteFolder: true);

            return result.Succeeded
                ? result with
                {
                    Message = "The visible Google Drive backup folder was created in My Drive and linked by its folder ID."
                }
                : result;
        }

        private GoogleDriveRootFolderResult PersistFolder(
            SyncRemoteProfile previousProfile,
            string folderId,
            string displayName,
            GoogleDriveRootFolderStatus status,
            bool wasCreated,
            bool wasDiscovered,
            bool wasValidatedById,
            bool wasMoved,
            bool createdRemoteFolder = false)
        {
            try
            {
                SyncRemoteProfile? current = SafeGetProfile(previousProfile.Id);

                if (current is null ||
                    current.ProviderKind != SyncProviderKind.GoogleDrive)
                {
                    return new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Failed,
                        previousProfile.Id,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.PersistenceFailed,
                        Message: createdRemoteFolder
                            ? "A Google Drive folder might have been created, but it could not be linked because the saved profile no longer exists. Retry setup to search before creating another folder."
                            : "The Google Drive folder was found, but the saved profile no longer exists.");
                }

                SyncRemoteProfile updated = _profileRepository.Update(current with
                {
                    RemoteFolderId = folderId,
                    RemoteRootDisplayName = displayName,
                    UpdatedUtc = _clock.UtcNow
                });

                TouchSuccessfulConnection(updated.Id);

                return new GoogleDriveRootFolderResult(
                    status,
                    updated.Id,
                    updated.RemoteFolderId,
                    updated.RemoteRootDisplayName,
                    WasCreated: wasCreated,
                    WasDiscovered: wasDiscovered,
                    WasValidatedById: wasValidatedById,
                    WasMoved: wasMoved);
            }
            catch
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    previousProfile.Id,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.PersistenceFailed,
                    Message: createdRemoteFolder
                        ? "A Google Drive folder might have been created, but its identity could not be linked locally. Retry setup to search before creating another folder."
                        : "The Google Drive folder was found, but its identity could not be saved to the profile.");
            }
        }

        private async Task<GoogleDriveRootFolderResult> MapApiFailureAsync(
            SyncRemoteProfile profile,
            GoogleDriveApiFailureDetails details,
            CancellationToken cancellationToken)
        {
            GoogleDriveRootFolderApiFailure failure = details.Failure;

            if (failure == GoogleDriveRootFolderApiFailure.AuthorizationRevoked)
            {
                bool removed = await TryRemoveRevokedAuthenticationAsync(
                    profile.Id,
                    cancellationToken);
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.ReauthenticationRequired,
                    profile.Id,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.AuthenticationRequired,
                    Message: removed
                        ? "Google Drive authorization is no longer valid. The invalid local authentication was removed; reconnect before checking the folder."
                        : "Google Drive authorization is no longer valid. The invalid local authentication could not be removed; disconnect locally, then reconnect.");
            }

            if (failure == GoogleDriveRootFolderApiFailure.AccessDenied)
            {
                TouchSuccessfulConnection(profile.Id);
                if (!string.IsNullOrWhiteSpace(profile.RemoteFolderId))
                {
                    return InvalidStoredFolder(
                        profile,
                        GoogleDriveRootFolderStatus.RecreationConfirmationRequired,
                        GoogleDriveRootFolderErrorCodes.AccessDenied,
                        "The saved Google Drive root folder is inaccessible. Confirm replacement before creating or selecting another folder.");
                }

                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profile.Id,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.AccessDenied,
                    Message: "Google Drive did not allow access to inspect or set up the application backup folder.");
            }

            if (failure == GoogleDriveRootFolderApiFailure.InsufficientScope)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.ReauthenticationRequired,
                    profile.Id,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.InsufficientScope,
                    Message: "Google Drive did not grant the required drive.file access. Reconnect the account and approve the requested access.");
            }

            if (failure == GoogleDriveRootFolderApiFailure.ApiNotEnabled)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Unavailable,
                    profile.Id,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.ApiNotEnabled,
                    Message: "The Google Drive API is not enabled for the configured OAuth project.");
            }

            if (failure is GoogleDriveRootFolderApiFailure.InvalidRequest or
                GoogleDriveRootFolderApiFailure.InvalidQuery)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profile.Id,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    ErrorCode: failure == GoogleDriveRootFolderApiFailure.InvalidQuery
                        ? GoogleDriveRootFolderErrorCodes.InvalidQuery
                        : GoogleDriveRootFolderErrorCodes.InvalidRequest,
                    Message: $"The {OperationDisplayName(details.Operation)} request was rejected by Google Drive. The saved folder identity was preserved.");
            }

            if (failure is GoogleDriveRootFolderApiFailure.RateLimited or
                GoogleDriveRootFolderApiFailure.QuotaExceeded)
            {
                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Unavailable,
                    profile.Id,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    ErrorCode: failure == GoogleDriveRootFolderApiFailure.RateLimited
                        ? GoogleDriveRootFolderErrorCodes.RateLimited
                        : GoogleDriveRootFolderErrorCodes.QuotaExceeded,
                    Message: failure == GoogleDriveRootFolderApiFailure.RateLimited
                        ? "Google Drive temporarily rate-limited the root-folder request. Try again later; the saved folder identity was preserved."
                        : "Google Drive quota prevented the root-folder request. The saved folder identity was preserved.");
            }

            return new GoogleDriveRootFolderResult(
                failure == GoogleDriveRootFolderApiFailure.Unavailable
                    ? GoogleDriveRootFolderStatus.Unavailable
                    : GoogleDriveRootFolderStatus.Failed,
                profile.Id,
                profile.RemoteFolderId,
                profile.RemoteRootDisplayName,
                ErrorCode: failure == GoogleDriveRootFolderApiFailure.Unavailable
                    ? GoogleDriveRootFolderErrorCodes.Unavailable
                    : GoogleDriveRootFolderErrorCodes.Failed,
                Message: failure == GoogleDriveRootFolderApiFailure.Unavailable
                    ? "Google Drive is temporarily unavailable. The saved folder identity was preserved."
                    : "The Google Drive root folder could not be checked. The saved folder identity was preserved.");
        }

        private static string OperationDisplayName(
            GoogleDriveRootFolderApiOperation operation) =>
            operation switch
            {
                GoogleDriveRootFolderApiOperation.RootFolderInspection =>
                    "root-folder inspection",
                GoogleDriveRootFolderApiOperation.RootFolderDiscovery =>
                    "root-folder discovery",
                GoogleDriveRootFolderApiOperation.RootFolderTopLevelMembership =>
                    "root-folder location check",
                GoogleDriveRootFolderApiOperation.RootFolderCreation =>
                    "root-folder creation",
                _ => "root-folder"
            };

        private static GoogleDriveRootFolderResult InvalidStoredFolder(
            SyncRemoteProfile profile,
            GoogleDriveRootFolderStatus status,
            string errorCode,
            string message) =>
            new(
                status,
                profile.Id,
                profile.RemoteFolderId,
                profile.RemoteRootDisplayName,
                WasValidatedById: true,
                RequiresRecreationConfirmation: true,
                ErrorCode: errorCode,
                Message: message);

        private static GoogleDriveRootFolderResult MapSessionFailure(
            Guid profileId,
            GoogleDriveAuthorizedSessionFailure failure) =>
            failure switch
            {
                GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication =>
                    new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.ReauthenticationRequired,
                        profileId,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.NotConnected,
                        Message: "Connect Google Drive before setting up or checking its backup folder."),
                GoogleDriveAuthorizedSessionFailure.TokenCorrupted or
                GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired or
                GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked or
                GoogleDriveAuthorizedSessionFailure.RevokedTokenCleanupFailed =>
                    new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.ReauthenticationRequired,
                        profileId,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.AuthenticationRequired,
                        Message: "Google Drive authentication is no longer usable. Reconnect the account before checking its backup folder."),
                GoogleDriveAuthorizedSessionFailure.ClientConfigurationMissing or
                GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable or
                GoogleDriveAuthorizedSessionFailure.Unavailable =>
                    new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Unavailable,
                        profileId,
                        ErrorCode: GoogleDriveRootFolderErrorCodes.Unavailable,
                        Message: "Google Drive authentication or secure storage is temporarily unavailable."),
                _ => new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Failed,
                    profileId,
                    ErrorCode: GoogleDriveRootFolderErrorCodes.Failed,
                    Message: "Google Drive authentication could not be validated.")
            };

        private async Task<bool> TryRemoveRevokedAuthenticationAsync(
            Guid profileId,
            CancellationToken cancellationToken)
        {
            try
            {
                SecretOperationResult cleanup = await _secretStore.DeleteAsync(
                    new SecretKey(profileId, SecretNames.OAuthTokenData),
                    cancellationToken);
                return cleanup.Succeeded;
            }
            catch
            {
                return false;
            }
        }

        private void TouchSuccessfulConnection(Guid profileId)
        {
            DateTimeOffset now = _clock.UtcNow;

            try
            {
                _profileRepository.UpdateLastUsed(profileId, now);
            }
            catch
            {
            }

            try
            {
                _profileRepository.UpdateLastSuccessfulConnection(profileId, now);
            }
            catch
            {
            }
        }

        private SyncRemoteProfile? SafeGetProfile(Guid profileId)
        {
            try
            {
                return _profileRepository.GetById(profileId);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeDisplayName(string? displayName)
        {
            string normalized = displayName?.Trim() ?? string.Empty;
            return normalized.Length is > 0 and <= 255
                ? normalized
                : GoogleDriveApplicationRoot.DisplayName;
        }

        private static GoogleDriveRootFolderResult ProfileNotFound(Guid profileId) =>
            new(
                GoogleDriveRootFolderStatus.Failed,
                profileId,
                ErrorCode: GoogleDriveRootFolderErrorCodes.ProfileNotFound,
                Message: "The saved Google Drive profile was not found.");

        private enum RootOperation
        {
            Inspect,
            Ensure,
            Recreate
        }
    }
}
