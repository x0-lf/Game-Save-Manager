namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveBinaryUploadService
    {
        Task<GoogleDriveBinaryUploadResult> UploadAsync(
            string localFilePath,
            GoogleDriveBinaryUploadRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Composes the existing guarded primitives for one binary file only.
    /// Run enumeration and ordering remain owned by SyncEngine.
    /// </summary>
    internal sealed class GoogleDriveBinaryUploadService
        : IGoogleDriveBinaryUploadService
    {
        private readonly Func<string, CancellationToken,
            Task<GoogleDriveLocalUploadSource>> _openSourceAsync;
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly GoogleDriveUploadParentPreparationService
            _parentPreparationService;
        private readonly GoogleDriveCreateOnlyUploadTargetGuard _targetGuard;
        private readonly IGoogleDriveMediaUploadClientFactory _mediaClientFactory;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveBinaryUploadService(
            Func<string, CancellationToken,
                Task<GoogleDriveLocalUploadSource>> openSourceAsync,
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            GoogleDriveUploadParentPreparationService parentPreparationService,
            GoogleDriveCreateOnlyUploadTargetGuard targetGuard,
            IGoogleDriveMediaUploadClientFactory mediaClientFactory,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _openSourceAsync = openSourceAsync ??
                throw new ArgumentNullException(nameof(openSourceAsync));
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _parentPreparationService = parentPreparationService ??
                throw new ArgumentNullException(nameof(parentPreparationService));
            _targetGuard = targetGuard ??
                throw new ArgumentNullException(nameof(targetGuard));
            _mediaClientFactory = mediaClientFactory ??
                throw new ArgumentNullException(nameof(mediaClientFactory));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
        }

        public async Task<GoogleDriveBinaryUploadResult> UploadAsync(
            string localFilePath,
            GoogleDriveBinaryUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await UploadCoreAsync(
                    localFilePath,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                GoogleDriveUploadFailureDetails details =
                    GoogleDriveUploadFailureMapper.Classify(exception);
                TryInvalidateProfile(
                    request.RemoteProfileId,
                    RequiresReauthentication(exception, details));
                Exception safeFailure =
                    GoogleDriveUploadFailureMapper.ToSafeException(
                        exception,
                        details);
                if (ReferenceEquals(safeFailure, exception))
                    throw;

                throw safeFailure;
            }
        }

        private static bool RequiresReauthentication(
            Exception exception,
            GoogleDriveUploadFailureDetails details) =>
            exception switch
            {
                GoogleDriveRemoteOperationException remote =>
                    remote.Result.Status is
                        GoogleDriveRemoteValidationStatus.AuthorizationRevoked or
                        GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveRecursiveFileListingException listing =>
                    listing.Result.Status ==
                        GoogleDriveRecursiveFileListingStatus
                            .ReauthenticationRequired,
                _ => details.Category ==
                    GoogleDriveUploadFailureCategory.ReauthenticationRequired
            };

        private async Task<GoogleDriveBinaryUploadResult> UploadCoreAsync(
            string localFilePath,
            GoogleDriveBinaryUploadRequest request,
            CancellationToken cancellationToken)
        {
            using GoogleDriveLocalUploadSource source =
                await _openSourceAsync(
                    localFilePath,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    request.RemoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var cacheScope = new GoogleDriveObjectCacheScope(
                context.RemoteProfileId,
                context.RootFolderId);

            string parentId = await _parentPreparationService.PrepareAsync(
                context,
                ParentPath(request.RemotePath),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string exactName = request.RemotePath.Segments[^1];
            using IDisposable lease = await _targetGuard.AcquireAsync(
                context,
                parentId,
                exactName,
                GoogleDriveObjectKind.File,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RemoveConfirmedStaleTarget(cacheScope, parentId, exactName);
            cancellationToken.ThrowIfCancellationRequested();

            using IGoogleDriveMediaUploadClient mediaClient =
                _mediaClientFactory.Create(context.Credential);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveMediaUploadMetadata response;
            try
            {
                response = await mediaClient.UploadAsync(
                        parentId,
                        exactName,
                        source.Stream,
                        source.Length,
                        GoogleDriveMediaUploadClient.OpaqueMediaType,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleDriveUploadCompletionIndeterminateException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveBinaryUploadResult indeterminateResult = new(
                    GoogleDriveBinaryUploadStatus.Indeterminate,
                    completedBytes: 0,
                    GoogleDriveBinaryUploadErrorCodes
                        .CompletionIndeterminate);
                cancellationToken.ThrowIfCancellationRequested();
                return indeterminateResult;
            }
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveUploadResponseValidator.Validate(
                response,
                parentId,
                exactName,
                source.Length);
            cancellationToken.ThrowIfCancellationRequested();
            CacheCompletedUpload(
                cacheScope,
                parentId,
                exactName,
                response,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveBinaryUploadResult result = new(
                GoogleDriveBinaryUploadStatus.Completed,
                source.Length);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private void RemoveConfirmedStaleTarget(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName)
        {
            try
            {
                if (_objectIdCache.TryGet(
                        scope,
                        parentId,
                        exactName,
                        GoogleDriveObjectKind.File,
                        out GoogleDriveObjectIdCacheEntry? entry) &&
                    entry is not null)
                {
                    _objectIdCache.Remove(
                        scope,
                        parentId,
                        exactName,
                        GoogleDriveObjectKind.File);
                }
            }
            catch
            {
                throw CacheFailure();
            }
        }

        private void TryInvalidateProfile(
            Guid remoteProfileId,
            bool reauthenticationRequired)
        {
            if (!reauthenticationRequired)
                return;

            try
            {
                _objectIdCache.InvalidateProfile(
                    remoteProfileId,
                    GoogleDriveObjectCacheInvalidationReason
                        .AuthorizationRevocation);
            }
            catch
            {
                // The classified authentication failure remains authoritative.
            }
        }

        private void CacheCompletedUpload(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveMediaUploadMetadata response,
            CancellationToken cancellationToken)
        {
            var metadata = new GoogleDriveObjectMetadata(
                response.Id!,
                response.Name!,
                response.MimeType!,
                response.Trashed!.Value,
                response.ParentIds!,
                response.DriveId);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_objectIdCache.TryStoreUniqueValidated(
                        scope,
                        parentId,
                        exactName,
                        GoogleDriveObjectKind.File,
                        metadata))
                {
                    throw CacheFailure();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                TryRemoveTarget(scope, parentId, exactName);
                throw;
            }
            catch
            {
                TryRemoveTarget(scope, parentId, exactName);
                throw CacheFailure();
            }
        }

        private void TryRemoveTarget(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName)
        {
            try
            {
                _objectIdCache.Remove(
                    scope,
                    parentId,
                    exactName,
                    GoogleDriveObjectKind.File);
            }
            catch
            {
                // Cache cleanup must not replace cancellation or the fixed
                // cache-rejection failure.
            }
        }

        private static GoogleDriveRemoteOperationException CacheFailure() =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                GoogleDriveBinaryUploadErrorCodes.CacheRejected,
                "The completed Google Drive upload could not be recorded safely.",
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));

        private static GoogleDriveRelativePath ParentPath(
            GoogleDriveRelativePath path) =>
            path.Segments.Count == 1
                ? GoogleDriveRelativePath.Root
                : GoogleDriveRelativePath.Parse(
                    string.Join('/', path.Segments.Take(path.Segments.Count - 1)));
    }
}
