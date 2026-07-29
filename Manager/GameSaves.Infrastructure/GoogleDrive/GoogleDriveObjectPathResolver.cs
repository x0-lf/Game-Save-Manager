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
    }

    /// <summary>
    /// Resolves one authenticated Google Drive session. The caller owns the
    /// credential lifetime; the resolver does not cache IDs across calls.
    /// </summary>
    internal sealed class GoogleDriveObjectPathResolver
        : IGoogleDriveObjectPathResolver
    {
        private readonly IGoogleDriveObjectApi _objectApi;
        private readonly GoogleAuthorizedCredential _credential;

        public GoogleDriveObjectPathResolver(
            IGoogleDriveObjectApi objectApi,
            GoogleAuthorizedCredential credential)
        {
            _objectApi = objectApi ?? throw new ArgumentNullException(nameof(objectApi));
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
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

        private async Task<GoogleDriveObjectResolutionResult> FindChildCoreAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind? expectedKind,
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
                GoogleDriveObjectKind actualKind = KindOf(metadata);

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

        private static GoogleDriveObjectKind KindOf(GoogleDriveObjectMetadata metadata) =>
            string.Equals(
                metadata.MimeType,
                GoogleDriveApplicationRoot.FolderMimeType,
                StringComparison.Ordinal)
                ? GoogleDriveObjectKind.Folder
                : GoogleDriveObjectKind.File;

        private static GoogleDriveObjectResolutionResult MapApiFailure(
            GoogleDriveApiException exception,
            GoogleDriveRelativePath path,
            GoogleDriveObjectKind? expectedKind) =>
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
                        message: "Google Drive did not allow the object lookup."),
                GoogleDriveApiFailure.RateLimited => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.RateLimited,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.RateLimited,
                    message: "Google Drive temporarily rate-limited the object lookup."),
                GoogleDriveApiFailure.QuotaExceeded => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.QuotaExceeded,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.QuotaExceeded,
                    message: "Google Drive quota prevented the object lookup."),
                GoogleDriveApiFailure.Unavailable => new GoogleDriveObjectResolutionResult(
                    GoogleDriveObjectResolutionStatus.Unavailable,
                    path,
                    expectedKind,
                    errorCode: GoogleDriveObjectResolutionErrorCodes.Unavailable,
                    message: "Google Drive is temporarily unavailable."),
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
