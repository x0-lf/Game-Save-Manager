using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRemoteValidationService
    {
        Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Validates a saved Google Drive account and its authoritative application
    /// root without opening a browser or mutating Drive. This service is not a
    /// sync provider and deliberately exposes no other remote-file operations.
    /// </summary>
    internal sealed class GoogleDriveRemoteValidationService
        : IGoogleDriveRemoteValidationService
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IGoogleDriveAuthorizedSessionFactory _sessionFactory;
        private readonly IGoogleDriveRootValidationApi _rootValidationApi;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;
        private readonly IUtcClock _clock;

        public GoogleDriveRemoteValidationService(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveAuthorizedSessionFactory sessionFactory,
            IGoogleDriveRootValidationApi rootValidationApi,
            IGoogleDriveObjectIdCache objectIdCache,
            IUtcClock clock)
        {
            _profileRepository = profileRepository;
            _sessionFactory = sessionFactory;
            _rootValidationApi = rootValidationApi;
            _objectIdCache = objectIdCache;
            _clock = clock;
        }

        public async Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.ProfileNotFound);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
                if (profile is null)
                {
                    return GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.ProfileNotFound);
                }

                GoogleDriveRemoteValidationResult? profileFailure =
                    ValidateProfile(profile);
                if (profileFailure is not null)
                    return profileFailure;

                string rootFolderId = profile.RemoteFolderId!;
                GoogleDriveAuthorizedSession session;

                try
                {
                    session = await _sessionFactory.RestoreAsync(
                        profile,
                        cancellationToken);
                }
                catch (GoogleDriveAuthorizedSessionException ex)
                {
                    GoogleDriveRemoteValidationResult result =
                        GoogleDriveRemoteValidationMapper.FromSessionFailure(ex.Failure);
                    return ApplyCacheInvalidation(
                        result,
                        profile.Id,
                        rootFolderId);
                }

                using GoogleAuthorizedCredential credential = session.Credential;

                GoogleDriveRootValidationMetadata metadata;
                try
                {
                    metadata = await _rootValidationApi.GetByIdAsync(
                        credential,
                        rootFolderId,
                        cancellationToken);
                }
                catch (GoogleDriveApiException ex)
                {
                    GoogleDriveRemoteValidationResult result =
                        GoogleDriveRemoteValidationMapper.FromApiFailure(ex.Details);
                    return ApplyCacheInvalidation(
                        result,
                        profile.Id,
                        rootFolderId,
                        credential.WasAuthenticationRefreshed);
                }

                GoogleDriveRemoteValidationStatus metadataStatus =
                    ValidateRootMetadata(metadata);
                GoogleDriveRemoteValidationResult validationResult =
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        metadataStatus,
                        metadata.Name ?? profile.RemoteRootDisplayName,
                        credential.WasAuthenticationRefreshed);

                if (metadataStatus != GoogleDriveRemoteValidationStatus.Valid)
                {
                    return ApplyCacheInvalidation(
                        validationResult,
                        profile.Id,
                        rootFolderId,
                        credential.WasAuthenticationRefreshed);
                }

                UpdateSuccessfulTimestamps(profile.Id);
                return validationResult;
            }
            catch (OperationCanceledException)
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.Cancelled);
            }
            catch
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.Failed);
            }
        }

        private static GoogleDriveRemoteValidationResult? ValidateProfile(
            SyncRemoteProfile profile)
        {
            if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.WrongProviderKind);
            }

            if (profile.SettingsError is not null ||
                profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings ||
                settings.SchemaVersion != GoogleDriveSyncRemoteSettings.CurrentSchemaVersion ||
                !string.Equals(
                    settings.RequestedScope,
                    GoogleDriveAuthorizationScopes.DriveFile,
                    StringComparison.Ordinal))
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.UnsupportedScope);
            }

            if (string.IsNullOrWhiteSpace(profile.RemoteFolderId))
            {
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    GoogleDriveRemoteValidationStatus.RootNotConfigured);
            }

            return null;
        }

        private static GoogleDriveRemoteValidationStatus ValidateRootMetadata(
            GoogleDriveRootValidationMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (metadata.Trashed)
                return GoogleDriveRemoteValidationStatus.RootTrashed;
            if (!metadata.IsFolder)
                return GoogleDriveRemoteValidationStatus.RootWrongType;
            if (metadata.IsInSharedDrive)
                return GoogleDriveRemoteValidationStatus.RootUnsupportedLocation;
            if (string.IsNullOrWhiteSpace(metadata.Name) ||
                metadata.ParentIds.Count == 0 ||
                metadata.ParentIds.Any(string.IsNullOrWhiteSpace))
            {
                return GoogleDriveRemoteValidationStatus.Failed;
            }
            if (!metadata.CanListChildren)
                return GoogleDriveRemoteValidationStatus.RootCannotListChildren;
            if (!metadata.CanAddChildren)
                return GoogleDriveRemoteValidationStatus.RootCannotAddChildren;

            return GoogleDriveRemoteValidationStatus.Valid;
        }

        private GoogleDriveRemoteValidationResult ApplyCacheInvalidation(
            GoogleDriveRemoteValidationResult result,
            Guid profileId,
            string rootFolderId,
            bool wasAuthenticationRefreshed = false)
        {
            bool invalidated = false;

            try
            {
                if (result.Status == GoogleDriveRemoteValidationStatus.AuthorizationRevoked)
                {
                    _objectIdCache.InvalidateProfile(
                        profileId,
                        GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation);
                    invalidated = true;
                }
                else if (ShouldClearRootScope(result.Status))
                {
                    _objectIdCache.ClearScope(
                        new GoogleDriveObjectCacheScope(profileId, rootFolderId));
                    invalidated = true;
                }
            }
            catch
            {
                // Cache cleanup is best effort. It must not hide the authoritative
                // validation outcome or trigger remote reconstruction.
            }

            return GoogleDriveRemoteValidationMapper.FromStatus(
                result.Status,
                result.RootDisplayName,
                wasAuthenticationRefreshed || result.WasAuthenticationRefreshed,
                invalidated);
        }

        private static bool ShouldClearRootScope(
            GoogleDriveRemoteValidationStatus status) =>
            status is GoogleDriveRemoteValidationStatus.RootMissing or
                GoogleDriveRemoteValidationStatus.RootTrashed or
                GoogleDriveRemoteValidationStatus.RootWrongType or
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation or
                GoogleDriveRemoteValidationStatus.RootInaccessible or
                GoogleDriveRemoteValidationStatus.RootCannotListChildren or
                GoogleDriveRemoteValidationStatus.RootCannotAddChildren or
                GoogleDriveRemoteValidationStatus.Failed;

        private void UpdateSuccessfulTimestamps(Guid profileId)
        {
            DateTimeOffset now = _clock.UtcNow;

            try
            {
                _profileRepository.UpdateLastUsed(profileId, now);
            }
            catch
            {
                // Timestamp bookkeeping cannot invalidate successful validation.
            }

            try
            {
                _profileRepository.UpdateLastSuccessfulConnection(profileId, now);
            }
            catch
            {
                // Timestamp bookkeeping cannot invalidate successful validation.
            }
        }
    }
}
