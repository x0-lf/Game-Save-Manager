using System.Collections.ObjectModel;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRunFolderDiscoveryService
    {
        Task<GoogleDriveRunFolderDiscoveryResult> DiscoverAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// One authoritative top-level folder candidate. IDs and exact names are
    /// available only to trusted Infrastructure callers and are omitted from
    /// diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveRunFolderCandidate
    {
        internal GoogleDriveRunFolderCandidate(
            string folderId,
            string exactName,
            string mimeType,
            IEnumerable<string> parentIds,
            bool hasExactNameCollision,
            bool hasCaseInsensitiveNameCollision)
        {
            if (string.IsNullOrWhiteSpace(folderId))
                throw new ArgumentException("A folder ID is required.", nameof(folderId));
            if (string.IsNullOrEmpty(exactName))
                throw new ArgumentException("An exact folder name is required.", nameof(exactName));
            if (string.IsNullOrWhiteSpace(mimeType))
                throw new ArgumentException("A folder MIME type is required.", nameof(mimeType));
            ArgumentNullException.ThrowIfNull(parentIds);

            FolderId = folderId;
            ExactName = exactName;
            MimeType = mimeType;
            ParentIds = new ReadOnlyCollection<string>(parentIds.ToArray());
            HasExactNameCollision = hasExactNameCollision;
            HasCaseInsensitiveNameCollision = hasCaseInsensitiveNameCollision;
        }

        public string FolderId { get; }

        public string ExactName { get; }

        public string MimeType { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public bool HasExactNameCollision { get; }

        /// <summary>
        /// True when another candidate differs only by ordinal casing. Exact
        /// duplicates are reported separately by HasExactNameCollision.
        /// </summary>
        public bool HasCaseInsensitiveNameCollision { get; }

        public override string ToString() =>
            "Google Drive run-folder candidate " +
            $"(parents={ParentIds.Count}, exactCollision={HasExactNameCollision}, " +
            $"caseCollision={HasCaseInsensitiveNameCollision})";
    }

    internal sealed class GoogleDriveRunFolderDiscoveryResult
    {
        public GoogleDriveRunFolderDiscoveryResult(
            IEnumerable<GoogleDriveRunFolderCandidate> candidates)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            Candidates = new ReadOnlyCollection<GoogleDriveRunFolderCandidate>(
                candidates.ToArray());
        }

        public IReadOnlyList<GoogleDriveRunFolderCandidate> Candidates { get; }

        public bool HasExactNameCollisions =>
            Candidates.Any(candidate => candidate.HasExactNameCollision);

        public bool HasCaseInsensitiveNameCollisions =>
            Candidates.Any(candidate => candidate.HasCaseInsensitiveNameCollision);

        public string ToSafeDiagnosticString() =>
            $"Google Drive run-folder discovery: candidates={Candidates.Count}; " +
            $"exactCollisions={HasExactNameCollisions}; " +
            $"caseCollisions={HasCaseInsensitiveNameCollisions}";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// Discovers direct folder candidates beneath the authoritative saved
    /// application root. It performs one read-only paginated child listing,
    /// preserves duplicate candidates, and never reads folder contents.
    /// </summary>
    internal sealed class GoogleDriveRunFolderDiscoveryService
        : IGoogleDriveRunFolderDiscoveryService
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveObjectListingApi _listingApi;

        public GoogleDriveRunFolderDiscoveryService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveObjectListingApi listingApi)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _listingApi = listingApi ??
                throw new ArgumentNullException(nameof(listingApi));
        }

        public async Task<GoogleDriveRunFolderDiscoveryResult> DiscoverAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);

            IReadOnlyList<GoogleDriveObjectMetadata> folders;
            try
            {
                folders = await _listingApi.ListChildrenAsync(
                    context.Credential,
                    context.RootFolderId,
                    GoogleDriveObjectKind.Folder,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveApiException ex)
            {
                throw Failure(ex);
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }

            if (folders is null)
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);

            try
            {
                foreach (GoogleDriveObjectMetadata folder in folders)
                    ValidateFolder(folder, context.RootFolderId);

                HashSet<string> exactCollisions = CollisionNames(
                    folders,
                    StringComparer.Ordinal,
                    requireDifferentExactNames: false);
                HashSet<string> caseCollisions = CollisionNames(
                    folders,
                    StringComparer.OrdinalIgnoreCase,
                    requireDifferentExactNames: true);

                var candidates = folders.Select(folder =>
                    new GoogleDriveRunFolderCandidate(
                        folder.Id,
                        folder.Name,
                        folder.MimeType,
                        folder.ParentIds,
                        exactCollisions.Contains(folder.Name),
                        caseCollisions.Contains(folder.Name)));

                return new GoogleDriveRunFolderDiscoveryResult(candidates);
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }
        }

        private static HashSet<string> CollisionNames(
            IReadOnlyList<GoogleDriveObjectMetadata> folders,
            StringComparer comparer,
            bool requireDifferentExactNames)
        {
            var collisions = new HashSet<string>(comparer);

            foreach (IGrouping<string, GoogleDriveObjectMetadata> group in
                folders.GroupBy(folder => folder.Name, comparer))
            {
                bool isCollision = requireDifferentExactNames
                    ? group.Select(folder => folder.Name)
                        .Distinct(StringComparer.Ordinal)
                        .Skip(1)
                        .Any()
                    : group.Skip(1).Any();

                if (isCollision)
                    collisions.Add(group.Key);
            }

            return collisions;
        }

        private static void ValidateFolder(
            GoogleDriveObjectMetadata folder,
            string rootFolderId)
        {
            if (!string.IsNullOrWhiteSpace(folder.DriveId))
            {
                throw Failure(
                    GoogleDriveRemoteValidationStatus.RootUnsupportedLocation);
            }

            if (folder.Trashed)
                throw Failure(GoogleDriveRemoteValidationStatus.RootTrashed);

            if (folder.Kind != GoogleDriveObjectKind.Folder)
                throw Failure(GoogleDriveRemoteValidationStatus.RootWrongType);

            if (string.IsNullOrWhiteSpace(folder.Id) ||
                string.IsNullOrEmpty(folder.Name) ||
                !string.Equals(
                    folder.MimeType,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    StringComparison.Ordinal) ||
                !folder.ParentIds.Contains(rootFolderId, StringComparer.Ordinal))
            {
                throw Failure(GoogleDriveRemoteValidationStatus.Failed);
            }
        }

        private static GoogleDriveRemoteOperationException Failure(
            GoogleDriveApiException exception)
        {
            GoogleDriveRemoteValidationResult result =
                exception.Details.SafeErrorCode switch
                {
                    GoogleDriveObjectResolutionErrorCodes.UnsupportedLocation =>
                        GoogleDriveRemoteValidationMapper.FromStatus(
                            GoogleDriveRemoteValidationStatus.RootUnsupportedLocation),
                    GoogleDriveObjectResolutionErrorCodes.Trashed =>
                        GoogleDriveRemoteValidationMapper.FromStatus(
                            GoogleDriveRemoteValidationStatus.RootTrashed),
                    GoogleDriveObjectResolutionErrorCodes.TypeMismatch =>
                        GoogleDriveRemoteValidationMapper.FromStatus(
                            GoogleDriveRemoteValidationStatus.RootWrongType),
                    _ => GoogleDriveRemoteValidationMapper.FromApiFailure(
                        exception.Details)
                };

            return new GoogleDriveRemoteOperationException(result);
        }

        private static GoogleDriveRemoteOperationException Failure(
            GoogleDriveRemoteValidationStatus status) =>
            new(GoogleDriveRemoteValidationMapper.FromStatus(status));
    }
}
