namespace GameSaves.Infrastructure.GoogleDrive
{
    internal static class GoogleDriveDownloadSourceErrorCodes
    {
        public const string NotFound = "GoogleDriveDownloadSourceNotFound";
        public const string Ambiguous = "GoogleDriveDownloadSourceAmbiguous";
        public const string CaseCollision =
            "GoogleDriveDownloadSourceCaseCollision";
        public const string TypeCollision =
            "GoogleDriveDownloadSourceTypeCollision";
        public const string UnsupportedObject =
            "GoogleDriveDownloadSourceUnsupportedObject";
    }

    /// <summary>
    /// One validated ordinary blob file to download. Identifiers and names
    /// stay available to trusted Infrastructure callers only and never appear
    /// in diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveDownloadSource
    {
        public GoogleDriveDownloadSource(
            string fileId,
            string parentFolderId,
            string exactName,
            string mimeType)
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("A file ID is required.", nameof(fileId));
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "A parent-folder ID is required.",
                    nameof(parentFolderId));
            }
            if (string.IsNullOrEmpty(exactName))
            {
                throw new ArgumentException(
                    "An exact file name is required.",
                    nameof(exactName));
            }
            if (GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType) !=
                GoogleDriveRecursiveObjectKind.BlobFile)
            {
                throw new ArgumentException(
                    "A download source must be an ordinary blob file.",
                    nameof(mimeType));
            }

            FileId = fileId;
            ParentFolderId = parentFolderId;
            ExactName = exactName;
            MimeType = mimeType;
        }

        public string FileId { get; }

        public string ParentFolderId { get; }

        public string ExactName { get; }

        public string MimeType { get; }

        public override string ToString() => "Google Drive download source";
    }

    /// <summary>
    /// Resolves a canonical remote path to one authoritative blob file under
    /// the configured root. Every segment is matched against a complete,
    /// validated child set, and anything ambiguous, case-colliding, wrongly
    /// typed, or unsupported fails closed. This resolver reads only.
    /// </summary>
    internal sealed class GoogleDriveDownloadSourceResolver
    {
        private readonly IGoogleDriveFolderChildEnumerationService
            _childEnumerationService;

        public GoogleDriveDownloadSourceResolver(
            IGoogleDriveFolderChildEnumerationService childEnumerationService) =>
            _childEnumerationService = childEnumerationService ??
                throw new ArgumentNullException(nameof(childEnumerationService));

        public async Task<GoogleDriveDownloadSource> ResolveAsync(
            GoogleDriveRemoteOperationContext context,
            GoogleDriveRelativePath remotePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(remotePath);
            if (remotePath.IsRoot)
            {
                throw new ArgumentException(
                    "A download source path is required.",
                    nameof(remotePath));
            }

            cancellationToken.ThrowIfCancellationRequested();

            string parentId = context.RootFolderId;
            for (int index = 0; index < remotePath.Segments.Count - 1; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveFolderChildEntry folder = await FindAsync(
                    context,
                    parentId,
                    remotePath.Segments[index],
                    GoogleDriveRecursiveObjectKind.Folder,
                    cancellationToken).ConfigureAwait(false);
                parentId = folder.ObjectId;
            }

            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveFolderChildEntry file = await FindAsync(
                context,
                parentId,
                remotePath.Segments[^1],
                GoogleDriveRecursiveObjectKind.BlobFile,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return new GoogleDriveDownloadSource(
                file.ObjectId,
                parentId,
                file.ExactName,
                file.MimeType);
        }

        private async Task<GoogleDriveFolderChildEntry> FindAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            string exactName,
            GoogleDriveRecursiveObjectKind expectedKind,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<GoogleDriveFolderChildEntry> children =
                await _childEnumerationService.EnumerateAsync(
                    context,
                    parentFolderId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveFolderChildEntry? exact = null;
            bool caseCollision = false;
            foreach (GoogleDriveFolderChildEntry? child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is null)
                {
                    throw GoogleDriveRecursiveFileListingFailureMapper.FromStatus(
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                }
                if (!string.Equals(
                        child.ExactName,
                        exactName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(child.ExactName, exactName, StringComparison.Ordinal))
                {
                    caseCollision = true;
                    continue;
                }

                if (exact is not null)
                    throw Failure(GoogleDriveDownloadSourceErrorCodes.Ambiguous);

                exact = child;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (caseCollision)
                throw Failure(GoogleDriveDownloadSourceErrorCodes.CaseCollision);
            if (exact is null)
                throw Failure(GoogleDriveDownloadSourceErrorCodes.NotFound);

            if (exact.Kind is GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument
                or GoogleDriveRecursiveObjectKind.Shortcut
                or GoogleDriveRecursiveObjectKind.Unsupported)
            {
                throw Failure(GoogleDriveDownloadSourceErrorCodes.UnsupportedObject);
            }
            if (exact.Kind != expectedKind)
                throw Failure(GoogleDriveDownloadSourceErrorCodes.TypeCollision);

            return exact;
        }

        private static GoogleDriveRemoteOperationException Failure(
            string errorCode) =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                errorCode,
                "The Google Drive download source could not be resolved safely.",
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));
    }
}
