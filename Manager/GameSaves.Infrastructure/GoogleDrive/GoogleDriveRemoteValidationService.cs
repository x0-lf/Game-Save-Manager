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
        private readonly IGoogleDriveRootMembershipApi _rootMembershipApi;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;
        private readonly IUtcClock _clock;
        private readonly IGoogleDriveValidationCoordinator _validationCoordinator;
        private readonly Dictionary<Guid, string> _observedRootIds = new();
        private readonly object _rootObservationGate = new();

        public GoogleDriveRemoteValidationService(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveAuthorizedSessionFactory sessionFactory,
            IGoogleDriveRootValidationApi rootValidationApi,
            IGoogleDriveRootMembershipApi rootMembershipApi,
            IGoogleDriveObjectIdCache objectIdCache,
            IUtcClock clock,
            IGoogleDriveValidationCoordinator? validationCoordinator = null)
        {
            _profileRepository = profileRepository;
            _sessionFactory = sessionFactory;
            _rootValidationApi = rootValidationApi;
            _rootMembershipApi = rootMembershipApi;
            _objectIdCache = objectIdCache;
            _clock = clock;
            _validationCoordinator = validationCoordinator ??
                new GoogleDriveValidationCoordinator();
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

            using GoogleDriveValidationOperation operation =
                _validationCoordinator.Begin(remoteProfileId, cancellationToken);
            CancellationToken validationToken = operation.CancellationToken;

            try
            {
                validationToken.ThrowIfCancellationRequested();

                SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
                if (profile is null)
                {
                    ForgetObservedRoot(remoteProfileId);
                    bool invalidated = TryInvalidateProfile(
                        remoteProfileId,
                        GoogleDriveObjectCacheInvalidationReason.ProfileDeletion);
                    return GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.ProfileNotFound,
                        cacheInvalidated: invalidated);
                }

                GoogleDriveRemoteValidationResult? profileFailure =
                    GoogleDriveRemoteProfileValidator.Validate(profile);
                if (profileFailure is not null)
                {
                    if (profileFailure.Status ==
                        GoogleDriveRemoteValidationStatus.RootNotConfigured)
                    {
                        ForgetObservedRoot(profile.Id);
                        bool invalidated = TryInvalidateProfile(
                            profile.Id,
                            GoogleDriveObjectCacheInvalidationReason
                                .ApplicationRootReplacement);
                        return GoogleDriveRemoteValidationMapper.FromStatus(
                            profileFailure.Status,
                            cacheInvalidated: invalidated);
                    }

                    return profileFailure;
                }

                string rootFolderId = profile.RemoteFolderId!;
                bool rootChanged = ObserveRoot(profile.Id, rootFolderId);
                bool cacheAlreadyInvalidated = rootChanged && TryInvalidateProfile(
                    profile.Id,
                    GoogleDriveObjectCacheInvalidationReason
                        .ApplicationRootReplacement);
                GoogleDriveAuthorizedSession session;

                try
                {
                    session = await _sessionFactory.RestoreAsync(
                        profile,
                        validationToken);
                }
                catch (GoogleDriveAuthorizedSessionException ex)
                {
                    if (!operation.IsCurrent)
                        return Superseded();

                    GoogleDriveRemoteValidationResult result =
                        GoogleDriveRemoteValidationMapper.FromSessionFailure(ex.Failure);
                    return ApplyCacheInvalidation(
                        result,
                        profile.Id,
                        rootFolderId,
                        cacheAlreadyInvalidated: cacheAlreadyInvalidated);
                }

                using GoogleAuthorizedCredential credential = session.Credential;

                if (!operation.IsCurrent)
                    return Superseded();

                GoogleDriveRootValidationMetadata metadata;
                try
                {
                    metadata = await _rootValidationApi.GetByIdAsync(
                        credential,
                        rootFolderId,
                        validationToken);
                }
                catch (GoogleDriveApiException ex)
                {
                    if (!operation.IsCurrent)
                        return Superseded();

                    GoogleDriveRemoteValidationResult result =
                        GoogleDriveRemoteValidationMapper.FromApiFailure(ex.Details);
                    return ApplyCacheInvalidation(
                        result,
                        profile.Id,
                        rootFolderId,
                        credential.WasAuthenticationRefreshed,
                        cacheAlreadyInvalidated);
                }

                if (!operation.IsCurrent)
                    return Superseded();

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
                        credential.WasAuthenticationRefreshed,
                        cacheAlreadyInvalidated);
                }

                bool isDirectChildOfMyDriveRoot;
                try
                {
                    isDirectChildOfMyDriveRoot =
                        await _rootMembershipApi.IsDirectChildOfMyDriveRootAsync(
                            credential,
                            rootFolderId,
                            validationToken);
                }
                catch (GoogleDriveApiException ex)
                {
                    if (!operation.IsCurrent)
                        return Superseded();

                    GoogleDriveRemoteValidationResult result =
                        GoogleDriveRemoteValidationMapper.FromApiFailure(ex.Details);
                    return ApplyCacheInvalidation(
                        result,
                        profile.Id,
                        rootFolderId,
                        credential.WasAuthenticationRefreshed,
                        cacheAlreadyInvalidated);
                }

                if (!operation.IsCurrent)
                    return Superseded();

                if (!isDirectChildOfMyDriveRoot)
                {
                    GoogleDriveRemoteValidationResult moved =
                        GoogleDriveRemoteValidationMapper.FromStatus(
                            GoogleDriveRemoteValidationStatus.RootMoved,
                            metadata.Name,
                            credential.WasAuthenticationRefreshed);
                    GoogleDriveRemoteValidationResult result = ApplyCacheInvalidation(
                        moved,
                        profile.Id,
                        rootFolderId,
                        credential.WasAuthenticationRefreshed,
                        cacheAlreadyInvalidated);
                    UpdateSuccessfulTimestamps(profile.Id);
                    return result;
                }

                UpdateSuccessfulTimestamps(profile.Id);
                return GoogleDriveRemoteValidationMapper.FromStatus(
                    validationResult.Status,
                    validationResult.RootDisplayName,
                    credential.WasAuthenticationRefreshed,
                    cacheAlreadyInvalidated);
            }
            catch (OperationCanceledException)
            {
                return !operation.IsCurrent
                    ? Superseded()
                    : GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.Cancelled);
            }
            catch
            {
                return !operation.IsCurrent
                    ? Superseded()
                    : GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.Failed);
            }
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
            bool wasAuthenticationRefreshed = false,
            bool cacheAlreadyInvalidated = false)
        {
            bool invalidated = cacheAlreadyInvalidated;

            try
            {
                if (result.Status == GoogleDriveRemoteValidationStatus.AuthorizationRevoked)
                {
                    _objectIdCache.InvalidateProfile(
                        profileId,
                        GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation);
                    invalidated = true;
                }
                else if (InvalidationReason(result.Status) is { } reason)
                {
                    _objectIdCache.InvalidateScope(
                        new GoogleDriveObjectCacheScope(profileId, rootFolderId),
                        reason);
                    invalidated = true;
                }
            }
            catch
            {
                // Cache cleanup is best effort. It must not hide the authoritative
                // validation outcome or trigger remote reconstruction.
            }

            return result.WithRuntimeState(
                wasAuthenticationRefreshed || result.WasAuthenticationRefreshed,
                invalidated);
        }

        private static GoogleDriveObjectCacheInvalidationReason? InvalidationReason(
            GoogleDriveRemoteValidationStatus status) =>
            status switch
            {
                GoogleDriveRemoteValidationStatus.RootMissing =>
                    GoogleDriveObjectCacheInvalidationReason.RootMissing,
                GoogleDriveRemoteValidationStatus.RootTrashed =>
                    GoogleDriveObjectCacheInvalidationReason.RootTrashed,
                GoogleDriveRemoteValidationStatus.RootMoved =>
                    GoogleDriveObjectCacheInvalidationReason.RootMoved,
                GoogleDriveRemoteValidationStatus.RootWrongType =>
                    GoogleDriveObjectCacheInvalidationReason.RootTypeChanged,
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation =>
                    GoogleDriveObjectCacheInvalidationReason.RootUnsupportedLocation,
                GoogleDriveRemoteValidationStatus.RootInaccessible or
                    GoogleDriveRemoteValidationStatus.RootCannotListChildren or
                    GoogleDriveRemoteValidationStatus.RootCannotAddChildren =>
                    GoogleDriveObjectCacheInvalidationReason.RootInaccessible,
                _ => null
            };

        private bool TryInvalidateProfile(
            Guid profileId,
            GoogleDriveObjectCacheInvalidationReason reason)
        {
            try
            {
                _objectIdCache.InvalidateProfile(profileId, reason);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ObserveRoot(Guid profileId, string rootFolderId)
        {
            lock (_rootObservationGate)
            {
                if (!_observedRootIds.TryGetValue(profileId, out string? previous))
                {
                    _observedRootIds[profileId] = rootFolderId;
                    return false;
                }

                if (string.Equals(previous, rootFolderId, StringComparison.Ordinal))
                    return false;

                _observedRootIds[profileId] = rootFolderId;
                return true;
            }
        }

        private void ForgetObservedRoot(Guid profileId)
        {
            lock (_rootObservationGate)
                _observedRootIds.Remove(profileId);
        }

        private static GoogleDriveRemoteValidationResult Superseded() =>
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Superseded);

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
