using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal static class GoogleDriveUploadParentPreparationErrorCodes
    {
        public const string Ambiguous = "GoogleDriveUploadParentAmbiguous";
        public const string CaseCollision =
            "GoogleDriveUploadParentCaseCollision";
        public const string TypeCollision =
            "GoogleDriveUploadParentTypeCollision";
        public const string UnsupportedObject =
            "GoogleDriveUploadParentUnsupportedObject";
        public const string UnsupportedLocation =
            "GoogleDriveUploadParentUnsupportedLocation";
        public const string InvalidMetadata =
            "GoogleDriveUploadParentInvalidMetadata";
        public const string CreateFailed =
            "GoogleDriveUploadParentCreateFailed";
        public const string InvalidCreateResponse =
            "GoogleDriveUploadParentInvalidCreateResponse";
        public const string CacheRejected =
            "GoogleDriveUploadParentCacheRejected";
    }

    /// <summary>
    /// Resolves canonical upload-parent segments from the configured root by
    /// complete authoritative child sets and creates only missing folders
    /// while holding the existing checked creation lease.
    /// </summary>
    internal sealed class GoogleDriveUploadParentPreparationService
    {
        private readonly IGoogleDriveFolderChildEnumerationService
            _childEnumerationService;
        private readonly GoogleDriveCreateOnlyUploadTargetGuard _targetGuard;
        private readonly IGoogleDriveObjectApi _objectApi;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveUploadParentPreparationService(
            IGoogleDriveFolderChildEnumerationService childEnumerationService,
            GoogleDriveCreateOnlyUploadTargetGuard targetGuard,
            IGoogleDriveObjectApi objectApi,
            IGoogleDriveObjectIdCache objectIdCache)
        {
            _childEnumerationService = childEnumerationService ??
                throw new ArgumentNullException(nameof(childEnumerationService));
            _targetGuard = targetGuard ??
                throw new ArgumentNullException(nameof(targetGuard));
            _objectApi = objectApi ?? throw new ArgumentNullException(nameof(objectApi));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
        }

        public async Task<string> PrepareAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveRelativePath parentPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(parentPath);
            cancellationToken.ThrowIfCancellationRequested();

            string parentId = context.RootFolderId;
            var cacheScope = new GoogleDriveObjectCacheScope(
                context.RemoteProfileId,
                context.RootFolderId);
            foreach (string segment in parentPath.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<GoogleDriveFolderChildEntry> children =
                    await _childEnumerationService.EnumerateAsync(
                        context,
                        parentId,
                        cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                GoogleDriveFolderChildEntry? folder = FindExactFolder(
                    children,
                    parentId,
                    segment,
                    cancellationToken);
                if (folder is null)
                {
                    using IDisposable lease = await _targetGuard.AcquireAsync(
                        context,
                        parentId,
                        segment,
                        GoogleDriveObjectKind.Folder,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        GoogleDriveObjectMetadata created =
                            await _objectApi.CreateFolderAsync(
                                context.Credential,
                                parentId,
                                segment,
                                cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        ValidateCreatedFolder(created, parentId, segment);
                        cancellationToken.ThrowIfCancellationRequested();
                        CacheFolder(
                            cacheScope,
                            parentId,
                            segment,
                            created);
                        cancellationToken.ThrowIfCancellationRequested();
                        parentId = created.Id;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (GoogleDriveRemoteOperationException)
                    {
                        throw;
                    }
                    catch (GoogleDriveApiException exception)
                    {
                        throw ApiFailure(exception);
                    }
                    catch
                    {
                        throw Failure(
                            GoogleDriveUploadParentPreparationErrorCodes.CreateFailed);
                    }
                }
                else
                {
                    CacheFolder(
                        cacheScope,
                        parentId,
                        segment,
                        ToMetadata(folder));
                    cancellationToken.ThrowIfCancellationRequested();
                    parentId = folder.ObjectId;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return parentId;
        }

        private static void ValidateCreatedFolder(
            GoogleDriveObjectMetadata? created,
            string expectedParentId,
            string expectedName)
        {
            if (created is null ||
                string.IsNullOrWhiteSpace(created.Id) ||
                !string.Equals(
                    created.Name,
                    expectedName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    created.MimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal) ||
                created.Kind != GoogleDriveObjectKind.Folder ||
                created.Trashed ||
                !string.IsNullOrWhiteSpace(created.DriveId) ||
                created.ParentIds.Count != 1 ||
                !string.Equals(
                    created.ParentIds[0],
                    expectedParentId,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    GoogleDriveUploadParentPreparationErrorCodes
                        .InvalidCreateResponse);
            }
        }

        private void CacheFolder(
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string exactName,
            GoogleDriveObjectMetadata metadata)
        {
            bool cached;
            try
            {
                cached = _objectIdCache.TryStoreUniqueValidated(
                    cacheScope,
                    parentId,
                    exactName,
                    GoogleDriveObjectKind.Folder,
                    metadata);
            }
            catch
            {
                throw Failure(
                    GoogleDriveUploadParentPreparationErrorCodes.CacheRejected);
            }

            if (!cached)
            {
                throw Failure(
                    GoogleDriveUploadParentPreparationErrorCodes.CacheRejected);
            }
        }

        private static GoogleDriveObjectMetadata ToMetadata(
            GoogleDriveFolderChildEntry folder) =>
            new(
                folder.ObjectId,
                folder.ExactName,
                folder.MimeType,
                folder.Trashed,
                folder.ParentIds,
                folder.DriveId);

        private static GoogleDriveFolderChildEntry? FindExactFolder(
            IReadOnlyList<GoogleDriveFolderChildEntry> children,
            string expectedParentId,
            string exactName,
            CancellationToken cancellationToken)
        {
            GoogleDriveFolderChildEntry? exactFolder = null;
            bool caseCollision = false;

            foreach (GoogleDriveFolderChildEntry? child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is null ||
                    child.ParentIds.Count != 1 ||
                    !string.Equals(
                        child.ParentIds[0],
                        expectedParentId,
                        StringComparison.Ordinal))
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.InvalidMetadata);
                }
                if (child.Trashed)
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.InvalidMetadata);
                }
                if (!string.IsNullOrWhiteSpace(child.DriveId))
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.UnsupportedLocation);
                }
                if (child.Kind is
                    GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument or
                    GoogleDriveRecursiveObjectKind.Shortcut or
                    GoogleDriveRecursiveObjectKind.Unsupported)
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.UnsupportedObject);
                }
                if (!string.Equals(
                        child.ExactName,
                        exactName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (child.Kind != GoogleDriveRecursiveObjectKind.Folder)
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.TypeCollision);
                }
                if (!string.Equals(
                        child.ExactName,
                        exactName,
                        StringComparison.Ordinal))
                {
                    caseCollision = true;
                    continue;
                }
                if (exactFolder is not null)
                {
                    throw Failure(
                        GoogleDriveUploadParentPreparationErrorCodes.Ambiguous);
                }

                exactFolder = child;
            }

            if (caseCollision)
            {
                throw Failure(
                    GoogleDriveUploadParentPreparationErrorCodes.CaseCollision);
            }

            return exactFolder;
        }

        private static GoogleDriveRemoteOperationException Failure(
            string errorCode) =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                errorCode,
                "The Google Drive upload parent could not be prepared safely.",
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));

        private static GoogleDriveRemoteOperationException ApiFailure(
            GoogleDriveApiException exception)
        {
            GoogleDriveRemoteValidationResult mapped =
                GoogleDriveRemoteValidationMapper.FromApiFailure(
                    exception.Details);
            return new GoogleDriveRemoteOperationException(
                new GoogleDriveRemoteValidationResult(
                    mapped.Status,
                    exception.Details.SafeErrorCode,
                    mapped.UserMessage,
                    mapped.Retryable,
                    rootDisplayName: null,
                    wasAuthenticationRefreshed: false,
                    cacheInvalidated: false));
        }
    }
}
