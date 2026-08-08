namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveOneLevelFileListingService
    {
        Task<GoogleDriveRecursiveFileListingResult> ListAsync(
            GoogleDriveResolvedRunFolder resolvedRunFolder,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Lists ordinary blob files immediately beneath one authoritative
    /// backup-run folder. Child folders are left for the later traversal
    /// coordinator, and duplicate names are preserved for the later
    /// collision-validation policy rather than selected or deduplicated here.
    /// </summary>
    internal sealed class GoogleDriveOneLevelFileListingService
        : IGoogleDriveOneLevelFileListingService
    {
        private readonly IGoogleDriveFolderChildEnumerationService
            _childEnumerationService;

        public GoogleDriveOneLevelFileListingService(
            IGoogleDriveFolderChildEnumerationService childEnumerationService) =>
            _childEnumerationService = childEnumerationService ??
                throw new ArgumentNullException(nameof(childEnumerationService));

        public async Task<GoogleDriveRecursiveFileListingResult> ListAsync(
            GoogleDriveResolvedRunFolder resolvedRunFolder,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolvedRunFolder);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                GoogleDriveRemoteOperationContext operationContext =
                    resolvedRunFolder.OperationContext;
                IReadOnlyList<GoogleDriveFolderChildEntry> children =
                    await _childEnumerationService.EnumerateAsync(
                        operationContext,
                        resolvedRunFolder.FolderId,
                        cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (children is null)
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                }

                GoogleDriveRecursiveRelativePath runRelativeRoot =
                    GoogleDriveRecursiveRelativePath.StartAtRunFolder();
                var files = new List<GoogleDriveRecursiveFileEntry>(children.Count);

                foreach (GoogleDriveFolderChildEntry? child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child is null)
                    {
                        throw Failure(
                            GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                    }

                    switch (child.Kind)
                    {
                        case GoogleDriveRecursiveObjectKind.Folder:
                            continue;

                        case GoogleDriveRecursiveObjectKind.BlobFile:
                            GoogleDriveRecursiveRelativePath relativePath =
                                runRelativeRoot.AppendChild(child.ExactName);
                            cancellationToken.ThrowIfCancellationRequested();
                            files.Add(new GoogleDriveRecursiveFileEntry(
                                child.ObjectId,
                                resolvedRunFolder.FolderId,
                                child.ExactName,
                                relativePath.Canonical,
                                child.MimeType));
                            break;

                        case GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument:
                        case GoogleDriveRecursiveObjectKind.Shortcut:
                        case GoogleDriveRecursiveObjectKind.Unsupported:
                            throw Failure(
                                GoogleDriveRecursiveFileListingStatus.UnsupportedObject);

                        default:
                            throw Failure(
                                GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveRecursiveFileEntry[] orderedFiles = files
                    .OrderBy(
                        entry => entry.CanonicalRelativePath,
                        StringComparer.Ordinal)
                    .ToArray();
                cancellationToken.ThrowIfCancellationRequested();

                return new GoogleDriveRecursiveFileListingResult(
                    GoogleDriveRecursiveFileListingStatus.Completed,
                    orderedFiles,
                    retryable: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRecursiveFileListingException)
            {
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.Failed);
            }
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveRecursiveFileListingStatus status) =>
            new(new GoogleDriveRecursiveFileListingResult(
                status,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                retryable: false,
                GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
                SafeUserMessage(status)));

        private static string SafeUserMessage(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject =>
                    "The Google Drive backup folder contains an unsupported object.",
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    "Google Drive returned invalid file metadata.",
                _ => "The Google Drive backup folder could not be listed."
            };
    }
}
