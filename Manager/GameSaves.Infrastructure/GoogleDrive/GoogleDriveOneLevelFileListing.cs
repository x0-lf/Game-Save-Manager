namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveOneLevelFileListingService
    {
        Task<GoogleDriveRecursiveFileListingResult> ListAsync(
            GoogleDriveResolvedRunFolder resolvedRunFolder,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Iteratively lists ordinary blob files beneath one authoritative
    /// backup-run folder. Exact and case-only sibling-name collisions fail
    /// closed before any member of that sibling set is traversed or recorded.
    /// Repeated identities remain available for later validation.
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
                GoogleDriveRecursiveRelativePath runRelativeRoot =
                    GoogleDriveRecursiveRelativePath.StartAtRunFolder();
                var pendingFolders = new Queue<PendingFolder>();
                pendingFolders.Enqueue(new PendingFolder(
                    resolvedRunFolder.FolderId,
                    runRelativeRoot,
                    depth: 0));
                var files = new List<GoogleDriveRecursiveFileEntry>();

                while (pendingFolders.TryDequeue(out PendingFolder? pendingFolder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<GoogleDriveFolderChildEntry> children =
                        await _childEnumerationService.EnumerateAsync(
                            operationContext,
                            pendingFolder.FolderId,
                            cancellationToken).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    if (children is null)
                    {
                        throw Failure(
                            GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                    }

                    ValidateSiblingNames(
                        children,
                        StringComparer.Ordinal,
                        GoogleDriveRecursiveFileListingStatus.Ambiguous,
                        cancellationToken);
                    ValidateSiblingNames(
                        children,
                        StringComparer.OrdinalIgnoreCase,
                        GoogleDriveRecursiveFileListingStatus.CaseCollision,
                        cancellationToken);

                    foreach (GoogleDriveFolderChildEntry? child in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (child is null)
                        {
                            throw Failure(
                                GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                        }

                        GoogleDriveRecursiveRelativePath relativePath =
                            pendingFolder.RelativePathPrefix.AppendChild(
                                child.ExactName);

                        switch (child.Kind)
                        {
                            case GoogleDriveRecursiveObjectKind.Folder:
                                cancellationToken.ThrowIfCancellationRequested();
                                pendingFolders.Enqueue(new PendingFolder(
                                    child.ObjectId,
                                    relativePath,
                                    pendingFolder.Depth + 1));
                                break;

                            case GoogleDriveRecursiveObjectKind.BlobFile:
                                cancellationToken.ThrowIfCancellationRequested();
                                files.Add(new GoogleDriveRecursiveFileEntry(
                                    child.ObjectId,
                                    pendingFolder.FolderId,
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
                }

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveRecursiveFileEntry[] orderedFiles =
                    ValidateAndOrderFiles(files, cancellationToken);

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

        internal static GoogleDriveRecursiveFileEntry[] ValidateAndOrderFiles(
            IReadOnlyList<GoogleDriveRecursiveFileEntry> files,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(files);

            var exactPaths = new HashSet<string>(StringComparer.Ordinal);
            var caseInsensitivePaths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GoogleDriveRecursiveFileEntry? file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file is null ||
                    !GoogleDriveRelativePath.TryParse(
                        file.CanonicalRelativePath,
                        out GoogleDriveRelativePath? path) ||
                    path is null ||
                    path.IsRoot ||
                    !string.Equals(
                        path.Canonical,
                        file.CanonicalRelativePath,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        path.Segments[^1],
                        file.ExactFileName,
                        StringComparison.Ordinal) ||
                    GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                        file.MimeType) != GoogleDriveRecursiveObjectKind.BlobFile)
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                }

                if (!exactPaths.Add(file.CanonicalRelativePath))
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.Ambiguous);
                }

                if (!caseInsensitivePaths.Add(file.CanonicalRelativePath))
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.CaseCollision);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveRecursiveFileEntry[] orderedFiles = files
                .OrderBy(
                    file => file.CanonicalRelativePath,
                    StringComparer.Ordinal)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return orderedFiles;
        }

        private static void ValidateSiblingNames(
            IReadOnlyList<GoogleDriveFolderChildEntry> children,
            StringComparer comparer,
            GoogleDriveRecursiveFileListingStatus collisionStatus,
            CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(comparer);
            foreach (GoogleDriveFolderChildEntry? child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is null)
                {
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                }

                if (!names.Add(child.ExactName))
                    throw Failure(collisionStatus);
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
                GoogleDriveRecursiveFileListingStatus.Ambiguous =>
                    "The Google Drive backup folder contains ambiguous duplicate names.",
                GoogleDriveRecursiveFileListingStatus.CaseCollision =>
                    "The Google Drive backup folder contains names that differ only by case.",
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject =>
                    "The Google Drive backup folder contains an unsupported object.",
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    "Google Drive returned invalid file metadata.",
                _ => "The Google Drive backup folder could not be listed."
            };

        private sealed class PendingFolder
        {
            public PendingFolder(
                string folderId,
                GoogleDriveRecursiveRelativePath relativePathPrefix,
                int depth)
            {
                if (string.IsNullOrWhiteSpace(folderId))
                    throw new ArgumentException(nameof(folderId));
                ArgumentNullException.ThrowIfNull(relativePathPrefix);
                if (depth < 0 || depth != relativePathPrefix.Depth)
                    throw new ArgumentOutOfRangeException(nameof(depth));
                FolderId = folderId;
                RelativePathPrefix = relativePathPrefix;
                Depth = depth;
            }

            public string FolderId { get; }
            public GoogleDriveRecursiveRelativePath RelativePathPrefix { get; }
            public int Depth { get; }
        }
    }
}
