namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRootExistenceService
    {
        Task<bool> ExistsAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Performs one authenticated, metadata-only lookup of the authoritative
    /// saved application-root ID. It never searches by name, lists children,
    /// creates objects, or updates the saved profile.
    /// </summary>
    internal sealed class GoogleDriveRootExistenceService
        : IGoogleDriveRootExistenceService
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveObjectApi _objectApi;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveRootExistenceService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveObjectApi objectApi,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _objectApi = objectApi ??
                throw new ArgumentNullException(nameof(objectApi));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
        }

        public async Task<bool> ExistsAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRemoteOperationContext context;
            try
            {
                context = await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleDriveRemoteOperationContextException ex)
            {
                if (ex.Result.Status !=
                    GoogleDriveRemoteValidationStatus.AuthorizationRevoked)
                {
                    throw;
                }

                bool invalidated = TryInvalidateProfile(
                    remoteProfileId,
                    GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation);
                throw new GoogleDriveRemoteOperationException(
                    ex.Result.WithRuntimeState(
                        ex.Result.WasAuthenticationRefreshed,
                        ex.Result.CacheInvalidated || invalidated));
            }

            using (context)
            {
                GoogleDriveObjectMetadata metadata;
                try
                {
                    metadata = await _objectApi.GetByIdAsync(
                        context.Credential,
                        context.RootFolderId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (GoogleDriveApiException ex)
                {
                    GoogleDriveRemoteValidationResult failure =
                        GoogleDriveRemoteValidationMapper.FromApiFailure(ex.Details);

                    if (failure.Status == GoogleDriveRemoteValidationStatus.RootMissing)
                    {
                        TryInvalidateScope(
                            context,
                            GoogleDriveObjectCacheInvalidationReason.RootMissing);
                        return false;
                    }

                    throw FailureWithInvalidation(context, failure);
                }
                catch
                {
                    throw new GoogleDriveRemoteOperationException(
                        GoogleDriveRemoteValidationMapper.FromStatus(
                            GoogleDriveRemoteValidationStatus.Failed));
                }

                GoogleDriveRemoteValidationStatus status = metadata switch
                {
                    _ when !string.Equals(
                        metadata.Id,
                        context.RootFolderId,
                        StringComparison.Ordinal) =>
                        GoogleDriveRemoteValidationStatus.Failed,
                    { Trashed: true } =>
                        GoogleDriveRemoteValidationStatus.RootTrashed,
                    { Kind: not GoogleDriveObjectKind.Folder } =>
                        GoogleDriveRemoteValidationStatus.RootWrongType,
                    { DriveId: not null } =>
                        GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
                    _ => GoogleDriveRemoteValidationStatus.Valid
                };

                if (status == GoogleDriveRemoteValidationStatus.Valid)
                    return true;

                throw FailureWithInvalidation(
                    context,
                    GoogleDriveRemoteValidationMapper.FromStatus(status));
            }
        }

        private GoogleDriveRemoteOperationException FailureWithInvalidation(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveRemoteValidationResult failure)
        {
            bool invalidated = failure.Status switch
            {
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked =>
                    TryInvalidateProfile(
                        context.RemoteProfileId,
                        GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation),
                GoogleDriveRemoteValidationStatus.RootTrashed =>
                    TryInvalidateScope(
                        context,
                        GoogleDriveObjectCacheInvalidationReason.RootTrashed),
                GoogleDriveRemoteValidationStatus.RootWrongType =>
                    TryInvalidateScope(
                        context,
                        GoogleDriveObjectCacheInvalidationReason.RootTypeChanged),
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation =>
                    TryInvalidateScope(
                        context,
                        GoogleDriveObjectCacheInvalidationReason.RootUnsupportedLocation),
                GoogleDriveRemoteValidationStatus.RootInaccessible =>
                    TryInvalidateScope(
                        context,
                        GoogleDriveObjectCacheInvalidationReason.RootInaccessible),
                _ => false
            };

            return new GoogleDriveRemoteOperationException(
                failure.WithRuntimeState(
                    failure.WasAuthenticationRefreshed,
                    failure.CacheInvalidated || invalidated));
        }

        private bool TryInvalidateScope(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveObjectCacheInvalidationReason reason)
        {
            try
            {
                _objectIdCache.InvalidateScope(
                    new GoogleDriveObjectCacheScope(
                        context.RemoteProfileId,
                        context.RootFolderId),
                    reason);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryInvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason)
        {
            try
            {
                _objectIdCache.InvalidateProfile(remoteProfileId, reason);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
