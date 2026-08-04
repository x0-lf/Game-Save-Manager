using System.Collections.ObjectModel;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveFolderChildEnumerationService
    {
        Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// One validated direct child of an authoritative Google Drive folder.
    /// Identifiers and names remain available only to trusted Infrastructure
    /// callers and are omitted from all diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveFolderChildEntry
    {
        public GoogleDriveFolderChildEntry(
            string objectId,
            string exactName,
            string mimeType,
            GoogleDriveRecursiveObjectKind kind,
            IEnumerable<string> parentIds,
            bool trashed,
            string? driveId)
        {
            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("An object ID is required.", nameof(objectId));
            if (!IsValidPathSegment(exactName))
            {
                throw new ArgumentException(
                    "A valid exact Drive name is required.",
                    nameof(exactName));
            }
            if (GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType) != kind ||
                kind == GoogleDriveRecursiveObjectKind.Unsupported)
            {
                throw new ArgumentException(
                    "A valid MIME type and matching recursive object kind are required.",
                    nameof(mimeType));
            }
            ArgumentNullException.ThrowIfNull(parentIds);

            string[] parentSnapshot = parentIds.ToArray();
            if (parentSnapshot.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    "Parent IDs cannot contain empty values.",
                    nameof(parentIds));
            }

            ObjectId = objectId;
            ExactName = exactName;
            MimeType = mimeType;
            Kind = kind;
            ParentIds = new ReadOnlyCollection<string>(parentSnapshot);
            Trashed = trashed;
            DriveId = string.IsNullOrWhiteSpace(driveId) ? null : driveId;
        }

        public string ObjectId { get; }

        public string ExactName { get; }

        public string MimeType { get; }

        public GoogleDriveRecursiveObjectKind Kind { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public bool Trashed { get; }

        public string? DriveId { get; }

        public override string ToString() =>
            $"Google Drive folder child (kind={Kind}; parents={ParentIds.Count})";

        internal static bool IsValidPathSegment(string? name) =>
            GoogleDriveRelativePath.TryParse(name, out GoogleDriveRelativePath? path) &&
            path is not null &&
            !path.IsRoot &&
            path.Segments.Count == 1 &&
            string.Equals(path.Canonical, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enumerates and validates one complete direct-child set. Pagination,
    /// My Drive request flags, cancellation forwarding, and short-lived Drive
    /// client disposal remain centralized in IGoogleDriveObjectListingApi.
    /// </summary>
    internal sealed class GoogleDriveFolderChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        private const string ParentMismatchErrorCode =
            "GoogleDriveObjectParentMismatch";

        private readonly IGoogleDriveObjectListingApi _listingApi;

        public GoogleDriveFolderChildEnumerationService(
            IGoogleDriveObjectListingApi listingApi) =>
            _listingApi = listingApi ?? throw new ArgumentNullException(nameof(listingApi));

        public async Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "An authoritative parent-folder ID is required.",
                    nameof(parentFolderId));
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                IReadOnlyList<GoogleDriveObjectMetadata> metadata =
                    await _listingApi.ListChildrenAsync(
                        context.Credential,
                        parentFolderId,
                        expectedKind: null,
                        cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (metadata is null)
                    throw Failure(GoogleDriveRecursiveFileListingStatus.InvalidMetadata);

                var children = new List<GoogleDriveFolderChildEntry>(metadata.Count);
                foreach (GoogleDriveObjectMetadata? child in metadata)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    children.Add(ValidateChild(child, parentFolderId));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new ReadOnlyCollection<GoogleDriveFolderChildEntry>(
                    children.ToArray());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRecursiveFileListingException)
            {
                throw;
            }
            catch (GoogleDriveApiException exception)
            {
                throw Failure(exception);
            }
            catch
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.Failed);
            }
        }

        private static GoogleDriveFolderChildEntry ValidateChild(
            GoogleDriveObjectMetadata? metadata,
            string expectedParentId)
        {
            if (metadata is null ||
                string.IsNullOrWhiteSpace(metadata.Id) ||
                !GoogleDriveFolderChildEntry.IsValidPathSegment(metadata.Name) ||
                !metadata.ParentIds.Contains(expectedParentId, StringComparer.Ordinal))
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
            }

            if (metadata.Trashed)
                throw Failure(GoogleDriveRecursiveFileListingStatus.TrashedObject);

            if (!string.IsNullOrWhiteSpace(metadata.DriveId))
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.UnsupportedLocation);
            }

            GoogleDriveRecursiveObjectKind kind =
                GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                    metadata.MimeType);
            if (kind == GoogleDriveRecursiveObjectKind.Unsupported)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
            }

            return new GoogleDriveFolderChildEntry(
                metadata.Id,
                metadata.Name,
                metadata.MimeType,
                kind,
                metadata.ParentIds,
                metadata.Trashed,
                metadata.DriveId);
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveApiException exception)
        {
            GoogleDriveRecursiveFileListingStatus status =
                exception.Details.SafeErrorCode switch
                {
                    GoogleDriveObjectResolutionErrorCodes.UnsupportedLocation =>
                        GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
                    GoogleDriveObjectResolutionErrorCodes.Trashed =>
                        GoogleDriveRecursiveFileListingStatus.TrashedObject,
                    ParentMismatchErrorCode =>
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
                    GoogleDriveObjectResolutionErrorCodes.TypeMismatch =>
                        GoogleDriveRecursiveFileListingStatus.TypeCollision,
                    _ => exception.Failure switch
                    {
                        GoogleDriveApiFailure.AuthorizationRevoked or
                            GoogleDriveApiFailure.InsufficientScope =>
                            GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired,
                        GoogleDriveApiFailure.AccessDenied =>
                            GoogleDriveRecursiveFileListingStatus.AccessDenied,
                        GoogleDriveApiFailure.NotFound =>
                            GoogleDriveRecursiveFileListingStatus.FolderNotFound,
                        GoogleDriveApiFailure.RateLimited =>
                            GoogleDriveRecursiveFileListingStatus.RateLimited,
                        GoogleDriveApiFailure.QuotaExceeded =>
                            GoogleDriveRecursiveFileListingStatus.QuotaExceeded,
                        GoogleDriveApiFailure.Unavailable or
                            GoogleDriveApiFailure.ApiNotEnabled =>
                            GoogleDriveRecursiveFileListingStatus.Unavailable,
                        _ => GoogleDriveRecursiveFileListingStatus.Failed
                    }
                };

            return Failure(status, exception.Details.Retryable);
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveRecursiveFileListingStatus status,
            bool retryable = false) =>
            new(new GoogleDriveRecursiveFileListingResult(
                status,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                retryable,
                GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
                SafeUserMessage(status)));

        private static string SafeUserMessage(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.FolderNotFound =>
                    "The Google Drive backup folder could not be found.",
                GoogleDriveRecursiveFileListingStatus.TrashedObject =>
                    "A trashed Google Drive object blocked file listing.",
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation =>
                    "This Google Drive location is not supported.",
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    "Google Drive returned invalid file metadata.",
                GoogleDriveRecursiveFileListingStatus.TypeCollision =>
                    "A Google Drive object has an unexpected type.",
                GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired =>
                    "Google Drive must be connected again.",
                GoogleDriveRecursiveFileListingStatus.AccessDenied =>
                    "Google Drive did not allow access to the backup folder.",
                GoogleDriveRecursiveFileListingStatus.RateLimited =>
                    "Google Drive is receiving too many requests. Try again later.",
                GoogleDriveRecursiveFileListingStatus.QuotaExceeded =>
                    "Google Drive quota prevents listing the backup folder.",
                GoogleDriveRecursiveFileListingStatus.Unavailable =>
                    "Google Drive is temporarily unavailable.",
                _ => "The Google Drive backup folder could not be listed."
            };
    }
}
