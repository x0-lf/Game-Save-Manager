namespace GameSaves.Infrastructure.GoogleDrive
{
    internal static class GoogleDriveCreateOnlyUploadTargetErrorCodes
    {
        public const string AlreadyExists =
            "GoogleDriveUploadTargetAlreadyExists";
        public const string CaseCollision =
            "GoogleDriveUploadTargetCaseCollision";
        public const string TypeCollision =
            "GoogleDriveUploadTargetTypeCollision";
    }

    /// <summary>
    /// Refuses creation when the complete authoritative child set contains a
    /// Windows-equivalent target name. It does not select or mutate an object.
    /// </summary>
    internal sealed class GoogleDriveCreateOnlyUploadTargetGuard
    {
        private readonly IGoogleDriveFolderChildEnumerationService
            _childEnumerationService;
        private readonly GoogleDriveObjectCreationCoordinator
            _creationCoordinator;

        public GoogleDriveCreateOnlyUploadTargetGuard(
            IGoogleDriveFolderChildEnumerationService childEnumerationService,
            GoogleDriveObjectCreationCoordinator creationCoordinator)
        {
            _childEnumerationService = childEnumerationService ??
                throw new ArgumentNullException(nameof(childEnumerationService));
            _creationCoordinator = creationCoordinator ??
                throw new ArgumentNullException(nameof(creationCoordinator));
        }

        public async ValueTask<IDisposable> AcquireAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            string exactName,
            GoogleDriveObjectKind targetKind,
            CancellationToken cancellationToken = default)
        {
            await EnsureAvailableAsync(
                context,
                parentFolderId,
                exactName,
                targetKind,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IDisposable? lease = null;
            try
            {
                lease = await _creationCoordinator.AcquireAsync(
                    parentFolderId,
                    exactName,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureAvailableAsync(
                    context,
                    parentFolderId,
                    exactName,
                    targetKind,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return lease;
            }
            catch
            {
                lease?.Dispose();
                throw;
            }
        }

        private async Task EnsureAvailableAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            string exactName,
            GoogleDriveObjectKind targetKind,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                throw new ArgumentException(
                    "An authoritative parent-folder ID is required.",
                    nameof(parentFolderId));
            }
            if (!GoogleDriveFolderChildEntry.IsValidPathSegment(exactName))
            {
                throw new ArgumentException(
                    "A valid exact Drive name is required.",
                    nameof(exactName));
            }
            if (!Enum.IsDefined(targetKind))
                throw new ArgumentOutOfRangeException(nameof(targetKind));

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GoogleDriveFolderChildEntry> children =
                await _childEnumerationService.EnumerateAsync(
                    context,
                    parentFolderId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            bool exactCollision = false;
            bool caseCollision = false;
            bool typeCollision = false;
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

                GoogleDriveObjectKind childKind =
                    child.Kind == GoogleDriveRecursiveObjectKind.Folder
                        ? GoogleDriveObjectKind.Folder
                        : GoogleDriveObjectKind.File;
                typeCollision |= childKind != targetKind;
                bool exactMatch = string.Equals(
                    child.ExactName,
                    exactName,
                    StringComparison.Ordinal);
                exactCollision |= exactMatch;
                caseCollision |= !exactMatch;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (typeCollision)
                throw Failure(GoogleDriveCreateOnlyUploadTargetErrorCodes.TypeCollision);
            if (exactCollision)
                throw Failure(GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists);
            if (caseCollision)
                throw Failure(GoogleDriveCreateOnlyUploadTargetErrorCodes.CaseCollision);
        }

        private static GoogleDriveRemoteOperationException Failure(
            string errorCode) =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                errorCode,
                "The Google Drive create-only target is not available.",
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));
    }
}
