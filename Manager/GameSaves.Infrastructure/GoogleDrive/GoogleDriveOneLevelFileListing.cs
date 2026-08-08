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
    /// Repeated authoritative identities and traversal cycles also fail closed.
    /// </summary>
    internal sealed class GoogleDriveOneLevelFileListingService
        : IGoogleDriveOneLevelFileListingService
    {
        private readonly IGoogleDriveFolderChildEnumerationService
            _childEnumerationService;
        private readonly IGoogleDriveObjectIdCache _objectIdCache;

        public GoogleDriveOneLevelFileListingService(
            IGoogleDriveFolderChildEnumerationService childEnumerationService,
            IGoogleDriveObjectIdCache? objectIdCache = null)
        {
            _childEnumerationService = childEnumerationService ??
                throw new ArgumentNullException(nameof(childEnumerationService));
            _objectIdCache = objectIdCache ?? new GoogleDriveObjectIdCache();
        }

        public async Task<GoogleDriveRecursiveFileListingResult> ListAsync(
            GoogleDriveResolvedRunFolder resolvedRunFolder,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolvedRunFolder);

            GoogleDriveObjectCacheScope? cacheScope = null;
            PendingFolder? enumeratingFolder = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveRemoteOperationContext operationContext =
                    resolvedRunFolder.OperationContext;
                cacheScope = new GoogleDriveObjectCacheScope(
                    operationContext.RemoteProfileId,
                    operationContext.RootFolderId);
                GoogleDriveRecursiveRelativePath runRelativeRoot =
                    GoogleDriveRecursiveRelativePath.StartAtRunFolder();
                var pendingFolders = new Queue<PendingFolder>();
                cancellationToken.ThrowIfCancellationRequested();
                pendingFolders.Enqueue(new PendingFolder(
                    resolvedRunFolder.FolderId,
                    runRelativeRoot,
                    depth: 0,
                    parentFolderId: null,
                    exactName: null));
                var visitedObjectIds = new HashSet<string>(
                    StringComparer.Ordinal)
                {
                    resolvedRunFolder.FolderId
                };
                var files = new List<GoogleDriveRecursiveFileEntry>();
                var stagedCacheEntries = new List<GoogleDriveObjectMetadata>();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pendingFolders.TryDequeue(
                            out PendingFolder? pendingFolder))
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    enumeratingFolder = pendingFolder;
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

                        if (!visitedObjectIds.Add(child.ObjectId))
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
                                stagedCacheEntries.Add(ToCacheMetadata(child));
                                cancellationToken.ThrowIfCancellationRequested();
                                pendingFolders.Enqueue(new PendingFolder(
                                    child.ObjectId,
                                    relativePath,
                                    pendingFolder.Depth + 1,
                                    pendingFolder.FolderId,
                                    child.ExactName));
                                break;

                            case GoogleDriveRecursiveObjectKind.BlobFile:
                                cancellationToken.ThrowIfCancellationRequested();
                                stagedCacheEntries.Add(ToCacheMetadata(child));
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

                    enumeratingFolder = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                GoogleDriveRecursiveFileEntry[] orderedFiles =
                    ValidateAndOrderFiles(files, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (GoogleDriveObjectMetadata metadata in stagedCacheEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _objectIdCache.TryStoreUniqueValidated(
                        cacheScope.Value,
                        metadata.ParentIds[0],
                        metadata.Name,
                        metadata.Kind,
                        metadata);
                }

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
            catch (GoogleDriveRecursiveFileListingException exception)
            {
                TryInvalidateCache(exception, cacheScope, enumeratingFolder);
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.Failed);
            }
            finally
            {
                resolvedRunFolder.Dispose();
            }
        }

        private static GoogleDriveObjectMetadata ToCacheMetadata(
            GoogleDriveFolderChildEntry child) =>
            new(
                child.ObjectId,
                child.ExactName,
                child.MimeType,
                child.Trashed,
                child.ParentIds,
                child.DriveId);

        private void TryInvalidateCache(
            GoogleDriveRecursiveFileListingException exception,
            GoogleDriveObjectCacheScope? cacheScope,
            PendingFolder? enumeratingFolder)
        {
            try
            {
                if (cacheScope is null)
                    return;

                if (exception.Result.Status ==
                    GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)
                {
                    _objectIdCache.InvalidateProfile(
                        cacheScope.Value.RemoteProfileId,
                        GoogleDriveObjectCacheInvalidationReason
                            .AuthorizationRevocation);
                }
                else if (
                    exception.Result.Status ==
                        GoogleDriveRecursiveFileListingStatus.FolderNotFound &&
                    enumeratingFolder?.ParentFolderId is not null &&
                    enumeratingFolder.ExactName is not null)
                {
                    _objectIdCache.Remove(
                        cacheScope.Value,
                        enumeratingFolder.ParentFolderId,
                        enumeratingFolder.ExactName,
                        GoogleDriveObjectKind.Folder);
                }
            }
            catch
            {
                // Cache maintenance must not replace the sanitized listing failure.
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
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(status);

        private sealed class PendingFolder
        {
            public PendingFolder(
                string folderId,
                GoogleDriveRecursiveRelativePath relativePathPrefix,
                int depth,
                string? parentFolderId,
                string? exactName)
            {
                if (string.IsNullOrWhiteSpace(folderId))
                    throw new ArgumentException(nameof(folderId));
                ArgumentNullException.ThrowIfNull(relativePathPrefix);
                if (depth < 0 || depth != relativePathPrefix.Depth)
                    throw new ArgumentOutOfRangeException(nameof(depth));
                FolderId = folderId;
                RelativePathPrefix = relativePathPrefix;
                Depth = depth;
                ParentFolderId = parentFolderId;
                ExactName = exactName;
            }

            public string FolderId { get; }
            public GoogleDriveRecursiveRelativePath RelativePathPrefix { get; }
            public int Depth { get; }
            public string? ParentFolderId { get; }
            public string? ExactName { get; }
        }

    }
}
