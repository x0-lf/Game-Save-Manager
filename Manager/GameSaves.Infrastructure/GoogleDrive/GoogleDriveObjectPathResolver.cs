using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveObjectPathResolver
    {
        Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Resolves one authenticated Google Drive session. The caller owns the
    /// credential lifetime. Cached IDs remain scoped to one saved profile and
    /// configured application root and are revalidated before cross-call use.
    /// </summary>
    internal sealed class GoogleDriveObjectPathResolver
        : IGoogleDriveObjectPathResolver
    {
        private static readonly GoogleDriveObjectCreationCoordinator
            SharedCreationCoordinator = new();

        private readonly IGoogleDriveObjectApi _objectApi;
        private readonly GoogleAuthorizedCredential _credential;
        private readonly GoogleDriveObjectCreationCoordinator _creationCoordinator;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;
        private readonly Guid _remoteProfileId;

        public GoogleDriveObjectPathResolver(
            IGoogleDriveObjectApi objectApi,
            GoogleAuthorizedCredential credential)
            : this(
                objectApi,
                credential,
                SharedCreationCoordinator,
                new GoogleDriveObjectIdCache(),
                Guid.NewGuid())
        {
        }

        internal GoogleDriveObjectPathResolver(
            IGoogleDriveObjectApi objectApi,
            GoogleAuthorizedCredential credential,
            GoogleDriveObjectCreationCoordinator creationCoordinator)
            : this(
                objectApi,
                credential,
                creationCoordinator,
                new GoogleDriveObjectIdCache(),
                Guid.NewGuid())
        {
        }

        internal GoogleDriveObjectPathResolver(
            IGoogleDriveObjectApi objectApi,
            GoogleAuthorizedCredential credential,
            GoogleDriveObjectCreationCoordinator creationCoordinator,
            IGoogleDriveObjectIdCache objectIdCache,
            Guid remoteProfileId)
        {
            _objectApi = objectApi ?? throw new ArgumentNullException(nameof(objectApi));
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _creationCoordinator = creationCoordinator ??
                throw new ArgumentNullException(nameof(creationCoordinator));
            _objectIdCache = objectIdCache ??
                throw new ArgumentNullException(nameof(objectIdCache));
            if (remoteProfileId == Guid.Empty)
                throw new ArgumentException("A remote profile ID is required.", nameof(remoteProfileId));
            _remoteProfileId = remoteProfileId;
        }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(expectedKind))
                return Task.FromResult(InvalidPath());

            return FindChildCoreAsync(
                parentId,
                exactName,
                expectedKind,
                cacheScope: null,
                cancellationToken);
        }

        public async Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rootFolderId) ||
                relativePath is null ||
                (expectedFinalKind is not null && !Enum.IsDefined(expectedFinalKind.Value)))
            {
                return InvalidPath(relativePath);
            }

            if (relativePath.IsRoot)
            {
                return expectedFinalKind == GoogleDriveObjectKind.File
                    ? TypeMismatch(relativePath, GoogleDriveObjectKind.Folder, rootFolderId)
                    : Found(
                        relativePath,
                        GoogleDriveObjectKind.Folder,
                        rootFolderId,
                        metadata: null);
            }

            string parentId = rootFolderId;
            var cacheScope = new GoogleDriveObjectCacheScope(
                _remoteProfileId,
                rootFolderId);

            for (int index = 0; index < relativePath.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isFinal = index == relativePath.Segments.Count - 1;
                GoogleDriveObjectKind? expectedKind = isFinal
                    ? expectedFinalKind
                    : GoogleDriveObjectKind.Folder;

                GoogleDriveObjectResolutionResult segmentResult =
                    await FindChildCoreAsync(
                        parentId,
                        relativePath.Segments[index],
                        expectedKind,
                        cacheScope,
                        cancellationToken);

                if (segmentResult.Status != GoogleDriveObjectResolutionStatus.Found)
                    return WithPath(segmentResult, relativePath);

                if (string.IsNullOrWhiteSpace(segmentResult.ObjectId))
                    return Failed(relativePath, expectedKind);

                parentId = segmentResult.ObjectId;

                if (isFinal)
                    return WithPath(segmentResult, relativePath);
            }

            return Failed(relativePath);
        }

        public async Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rootFolderId) ||
                relativeFolderPath is null)
            {
                return InvalidPath(relativeFolderPath);
            }

            if (relativeFolderPath.IsRoot)
            {
                return Found(
                    relativeFolderPath,
                    GoogleDriveObjectKind.Folder,
                    rootFolderId,
                    metadata: null);
            }

            string parentId = rootFolderId;
            var cacheScope = new GoogleDriveObjectCacheScope(
                _remoteProfileId,
                rootFolderId);
            GoogleDriveObjectMetadata? finalMetadata = null;
            bool createdAnyFolder = false;

            foreach (string segment in relativeFolderPath.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                GoogleDriveObjectResolutionResult segmentResult =
                    await FindChildCoreAsync(
                        parentId,
                        segment,
                        GoogleDriveObjectKind.Folder,
                        cacheScope,
                        cancellationToken);

                if (segmentResult.Status == GoogleDriveObjectResolutionStatus.NotFound)
                {
                    segmentResult = await EnsureChildFolderAsync(
                        parentId,
                        segment,
                        cacheScope,
                        cancellationToken);
                }

                if (segmentResult.Status is not (
                    GoogleDriveObjectResolutionStatus.Found or
                    GoogleDriveObjectResolutionStatus.Created))
                {
                    return WithPath(segmentResult, relativeFolderPath);
                }

                if (string.IsNullOrWhiteSpace(segmentResult.ObjectId))
                    return Failed(relativeFolderPath, GoogleDriveObjectKind.Folder);

                createdAnyFolder |=
                    segmentResult.Status == GoogleDriveObjectResolutionStatus.Created;
                parentId = segmentResult.ObjectId;
                finalMetadata = segmentResult.Metadata;
            }

            return createdAnyFolder
                ? Created(relativeFolderPath, parentId, finalMetadata)
                : Found(
                    relativeFolderPath,
                    GoogleDriveObjectKind.Folder,
                    parentId,
                    finalMetadata);
        }

        private async Task<GoogleDriveObjectResolutionResult>
            EnsureChildFolderAsync(
                string parentId,
                string exactName,
                GoogleDriveObjectCacheScope cacheScope,
                CancellationToken cancellationToken)
        {
            using IDisposable lease = await _creationCoordinator.AcquireAsync(
                parentId,
                exactName,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveObjectResolutionResult secondSearch =
                await FindChildCoreAsync(
                    parentId,
                    exactName,
                    GoogleDriveObjectKind.Folder,
                    cacheScope,
                    cancellationToken);

            if (secondSearch.Status != GoogleDriveObjectResolutionStatus.NotFound)
                return secondSearch;

            GoogleDriveRelativePath segmentPath = GoogleDriveRelativePath.Parse(exactName);

            try
            {
                GoogleDriveObjectMetadata created =
                    await _objectApi.CreateFolderAsync(
                        _credential,
                        parentId,
                        exactName,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveObjectResolutionResult result = ValidateCreatedFolder(
                    segmentPath,
                    parentId,
                    exactName,
                    created);

                if (result.Status == GoogleDriveObjectResolutionStatus.Created &&
                    result.Metadata is not null)
                {
                    _objectIdCache.TryStoreUniqueValidated(
                        cacheScope,
                        parentId,
                        exactName,
                        GoogleDriveObjectKind.Folder,
                        result.Metadata);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                return MapApiFailure(
                    ex,
                    segmentPath,
                    GoogleDriveObjectKind.Folder,
                    isCreation: true);
            }
            catch
            {
                return Failed(segmentPath, GoogleDriveObjectKind.Folder);
            }
        }

        private static GoogleDriveObjectResolutionResult ValidateCreatedFolder(
            GoogleDriveRelativePath segmentPath,
            string expectedParentId,
            string expectedName,
            GoogleDriveObjectMetadata? metadata)
        {
            if (metadata is null ||
                string.IsNullOrWhiteSpace(metadata.Id) ||
                !string.Equals(metadata.Name, expectedName, StringComparison.Ordinal) ||
                !metadata.ParentIds.Contains(expectedParentId, StringComparer.Ordinal))
            {
                return InvalidCreateResponse(segmentPath);
            }

            GoogleDriveObjectKind actualKind = metadata.Kind;

            if (!string.IsNullOrWhiteSpace(metadata.DriveId))
            {
                return new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.UnsupportedLocation,
                    segmentPath,
                    actualKind,
                    metadata,
                    metadata.Id,
                    GoogleDriveObjectResolutionErrorCodes.UnsupportedLocation,
                    "The created Google Drive folder is in an unsupported location.");
            }

            if (metadata.Trashed)
            {
                return new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.Trashed,
                    segmentPath,
                    actualKind,
                    metadata,
                    metadata.Id,
                    GoogleDriveObjectResolutionErrorCodes.Trashed,
                    "The created Google Drive folder is trashed.");
            }

            if (actualKind != GoogleDriveObjectKind.Folder)
                return TypeMismatch(segmentPath, actualKind, metadata.Id, metadata);

            return Created(segmentPath, metadata.Id, metadata);
        }

        private async Task<GoogleDriveObjectResolutionResult> FindChildCoreAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind? expectedKind,
            GoogleDriveObjectCacheScope? cacheScope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(parentId) ||
                !GoogleDriveRelativePath.TryParse(
                    exactName,
                    out GoogleDriveRelativePath? childPath) ||
                childPath is null ||
                childPath.IsRoot ||
                childPath.Segments.Count != 1)
            {
                return InvalidPath();
            }

            if (cacheScope is not null && expectedKind is not null &&
                _objectIdCache.TryGet(
                    cacheScope.Value,
                    parentId,
                    exactName,
                    expectedKind.Value,
                    out GoogleDriveObjectIdCacheEntry? cachedEntry) &&
                cachedEntry is not null)
            {
                GoogleDriveObjectResolutionResult? cachedResult =
                    await ValidateCachedEntryAsync(
                        cacheScope.Value,
                        parentId,
                        exactName,
                        expectedKind.Value,
                        childPath,
                        cachedEntry,
                        cancellationToken);

                if (cachedResult is not null)
                    return cachedResult;
            }

            try
            {
                IReadOnlyList<GoogleDriveObjectMetadata> matches =
                    await _objectApi.ListChildrenByExactNameAsync(
                        _credential,
                        parentId,
                        exactName,
                        cancellationToken);

                if (matches.Count == 0)
                {
                    return new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.NotFound,
                        childPath,
                        expectedKind,
                        errorCode: GoogleDriveObjectResolutionErrorCodes.NotFound,
                        message: "The requested Google Drive object was not found.");
                }

                if (matches.Count > 1)
                {
                    return new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.Ambiguous,
                        childPath,
                        expectedKind,
                        errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
                        message: "More than one Google Drive object has the requested name.");
                }

                GoogleDriveObjectMetadata metadata = matches[0];
                GoogleDriveObjectKind actualKind = metadata.Kind;

                if (!string.Equals(metadata.Name, exactName, StringComparison.Ordinal) ||
                    !metadata.ParentIds.Contains(parentId, StringComparer.Ordinal))
                {
                    return InvalidMetadata(childPath, expectedKind);
                }

                if (!string.IsNullOrWhiteSpace(metadata.DriveId))
                {
                    return new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.UnsupportedLocation,
                        childPath,
                        actualKind,
                        metadata,
                        metadata.Id,
                        GoogleDriveObjectResolutionErrorCodes.UnsupportedLocation,
                        "The Google Drive object is in an unsupported location.");
                }

                if (metadata.Trashed)
                {
                    return new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.Trashed,
                        childPath,
                        actualKind,
                        metadata,
                        metadata.Id,
                        GoogleDriveObjectResolutionErrorCodes.Trashed,
                        "The Google Drive object is trashed.");
                }

                if (expectedKind is not null && actualKind != expectedKind)
                    return TypeMismatch(childPath, actualKind, metadata.Id, metadata);

                if (cacheScope is not null && expectedKind is not null)
                {
                    _objectIdCache.TryStoreUniqueValidated(
                        cacheScope.Value,
                        parentId,
                        exactName,
                        expectedKind.Value,
                        metadata);
                }

                return Found(childPath, actualKind, metadata.Id, metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                return MapApiFailure(ex, childPath, expectedKind);
            }
            catch
            {
                return Failed(childPath, expectedKind);
            }
        }

        private async Task<GoogleDriveObjectResolutionResult?> ValidateCachedEntryAsync(
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveRelativePath childPath,
            GoogleDriveObjectIdCacheEntry cachedEntry,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveObjectMetadata current = await _objectApi.GetByIdAsync(
                    _credential,
                    cachedEntry.ObjectId,
                    cancellationToken);

                if (IsCurrentCacheMatch(
                    cachedEntry.ObjectId,
                    parentId,
                    exactName,
                    expectedKind,
                    current))
                {
                    _objectIdCache.TryStoreUniqueValidated(
                        cacheScope,
                        parentId,
                        exactName,
                        expectedKind,
                        current);
                    return Found(childPath, expectedKind, current.Id, current);
                }

                InvalidateStaleEntry(
                    cacheScope,
                    parentId,
                    exactName,
                    expectedKind);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                if (ex.Failure == GoogleDriveApiFailure.NotFound)
                {
                    InvalidateStaleEntry(
                        cacheScope,
                        parentId,
                        exactName,
                        expectedKind);
                    return null;
                }

                return MapApiFailure(ex, childPath, expectedKind);
            }
            catch
            {
                return Failed(childPath, expectedKind);
            }
        }

        private void InvalidateStaleEntry(
            GoogleDriveObjectCacheScope cacheScope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind)
        {
            if (expectedKind == GoogleDriveObjectKind.Folder)
            {
                _objectIdCache.ClearScope(cacheScope);
                return;
            }

            _objectIdCache.Remove(
                cacheScope,
                parentId,
                exactName,
                expectedKind);
        }

        private static bool IsCurrentCacheMatch(
            string cachedObjectId,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata) =>
            string.Equals(metadata.Id, cachedObjectId, StringComparison.Ordinal) &&
            string.Equals(metadata.Name, exactName, StringComparison.Ordinal) &&
            metadata.ParentIds.Contains(parentId, StringComparer.Ordinal) &&
            !metadata.Trashed &&
            string.IsNullOrWhiteSpace(metadata.DriveId) &&
            metadata.Kind == expectedKind;

        private static GoogleDriveObjectResolutionResult MapApiFailure(
            GoogleDriveApiException exception,
            GoogleDriveRelativePath path,
            GoogleDriveObjectKind? expectedKind,
            bool isCreation = false) =>
            exception.Failure switch
            {
                GoogleDriveApiFailure.NotFound => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.NotFound,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.NotFound,
                    message: "The requested Google Drive object was not found."),
                GoogleDriveApiFailure.AuthorizationRevoked =>
                    new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.ReauthenticationRequired,
                        path,
                        expectedKind,
                        errorCode: GoogleDriveObjectResolutionErrorCodes.AuthenticationRequired,
                        message: "Google Drive authentication is no longer valid."),
                GoogleDriveApiFailure.InsufficientScope or
                    GoogleDriveApiFailure.AccessDenied or
                    GoogleDriveApiFailure.ApiNotEnabled =>
                    new GoogleDriveObjectResolutionResult(
                        GoogleDriveObjectResolutionStatus.AccessDenied,
                        path,
                        expectedKind,
                        errorCode: GoogleDriveObjectResolutionErrorCodes.AccessDenied,
                        message: isCreation
                            ? "Google Drive did not allow folder creation."
                            : "Google Drive did not allow the object lookup."),
                GoogleDriveApiFailure.RateLimited => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.RateLimited,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.RateLimited,
                    message: isCreation
                        ? "Google Drive temporarily rate-limited folder creation."
                        : "Google Drive temporarily rate-limited the object lookup."),
                GoogleDriveApiFailure.QuotaExceeded => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.QuotaExceeded,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.QuotaExceeded,
                    message: isCreation
                        ? "Google Drive quota prevented folder creation."
                        : "Google Drive quota prevented the object lookup."),
                GoogleDriveApiFailure.Unavailable => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.Unavailable,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.Unavailable,
                    message: isCreation
                        ? "Google Drive folder creation is temporarily unavailable."
                        : "Google Drive is temporarily unavailable."),
                _ => Failed(path, expectedKind)
            };

        private static GoogleDriveObjectResolutionResult Found(
            GoogleDriveRelativePath path,
            GoogleDriveObjectKind kind,
            string objectId,
            GoogleDriveObjectMetadata? metadata) =>
            new(
                GoogleDriveObjectResolutionStatus.Found,
                path,
                kind,
                metadata,
                objectId);

        private static GoogleDriveObjectResolutionResult Created(
            GoogleDriveRelativePath path,
            string objectId,
            GoogleDriveObjectMetadata? metadata) =>
            new(
                GoogleDriveObjectResolutionStatus.Created,
                path,
                GoogleDriveObjectKind.Folder,
                metadata,
                objectId);

        private static GoogleDriveObjectResolutionResult TypeMismatch(
            GoogleDriveRelativePath path,
            GoogleDriveObjectKind actualKind,
            string objectId,
            GoogleDriveObjectMetadata? metadata = null) =>
            new(
                GoogleDriveObjectResolutionStatus.TypeMismatch,
                path,
                actualKind,
                metadata,
                objectId,
                GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
                "The Google Drive object has a different type than required.");

        private static GoogleDriveObjectResolutionResult InvalidPath(
            GoogleDriveRelativePath? path = null) =>
            new(
                GoogleDriveObjectResolutionStatus.InvalidPath,
                path,
                errorCode: GoogleDriveObjectResolutionErrorCodes.InvalidPath,
                message: "The Google Drive path or parent identity is invalid.");

        private static GoogleDriveObjectResolutionResult InvalidCreateResponse(
            GoogleDriveRelativePath path) =>
            new(
                GoogleDriveObjectResolutionStatus.Failed,
                path,
                GoogleDriveObjectKind.Folder,
                errorCode: GoogleDriveObjectResolutionErrorCodes.InvalidCreateResponse,
                message: "Google Drive returned invalid metadata for the created folder.");

        private static GoogleDriveObjectResolutionResult InvalidMetadata(
            GoogleDriveRelativePath path,
            GoogleDriveObjectKind? expectedKind) =>
            new(
                GoogleDriveObjectResolutionStatus.Failed,
                path,
                expectedKind,
                errorCode: GoogleDriveObjectResolutionErrorCodes.InvalidMetadata,
                message: "Google Drive returned inconsistent object metadata.");

        private static GoogleDriveObjectResolutionResult Failed(
            GoogleDriveRelativePath? path,
            GoogleDriveObjectKind? expectedKind = null) =>
            new(
                GoogleDriveObjectResolutionStatus.Failed,
                path,
                expectedKind,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Failed,
                message: "The Google Drive object could not be resolved.");

        private static GoogleDriveObjectResolutionResult WithPath(
            GoogleDriveObjectResolutionResult result,
            GoogleDriveRelativePath path) =>
            new(
                result.Status,
                path,
                result.ObjectKind,
                result.Metadata,
                result.ObjectId,
                result.ErrorCode,
                result.Message);
    }
}
