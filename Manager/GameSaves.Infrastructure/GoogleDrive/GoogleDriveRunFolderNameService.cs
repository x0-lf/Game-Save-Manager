using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRunFolderNameService
    {
        Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveRunFolderListingErrorCodes
    {
        public const string AmbiguousRunName =
            "GoogleDriveRunFolderNameAmbiguous";
        public const string InvalidManifestMetadata =
            "GoogleDriveRunFolderManifestMetadataInvalid";
    }

    /// <summary>
    /// Lists representable backup-run folder names without reading manifest
    /// content. Exact manifest matches are existence evidence only: object IDs
    /// are never selected, returned, or persisted by this service.
    /// </summary>
    internal sealed class GoogleDriveRunFolderNameService
        : IGoogleDriveRunFolderNameService
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveRunFolderDiscoveryService _discoveryService;
        private readonly IGoogleDriveObjectApi _objectApi;

        public GoogleDriveRunFolderNameService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveRunFolderDiscoveryService discoveryService,
            IGoogleDriveObjectApi objectApi)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _discoveryService = discoveryService ??
                throw new ArgumentNullException(nameof(discoveryService));
            _objectApi = objectApi ?? throw new ArgumentNullException(nameof(objectApi));
        }

        public async Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);

            GoogleDriveRunFolderDiscoveryResult discovery =
                await _discoveryService.DiscoverAsync(
                    context,
                    cancellationToken).ConfigureAwait(false);

            var includedCandidates = new List<GoogleDriveRunFolderCandidate>();
            foreach (GoogleDriveRunFolderCandidate candidate in discovery.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<GoogleDriveObjectMetadata> matches;
                try
                {
                    matches = await _objectApi.ListChildrenByExactNameAsync(
                        context.Credential,
                        candidate.FolderId,
                        TransferBackupLocations.ManifestFileName,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (GoogleDriveApiException ex)
                {
                    throw new GoogleDriveRemoteOperationException(
                        GoogleDriveRemoteValidationMapper.FromApiFailure(ex.Details));
                }
                catch (GoogleDriveRemoteOperationException)
                {
                    throw;
                }
                catch
                {
                    throw Failure(
                        GoogleDriveRunFolderListingErrorCodes.InvalidManifestMetadata,
                        "Google Drive backup-run manifest metadata could not be checked safely.");
                }

                if (matches is null)
                {
                    throw Failure(
                        GoogleDriveRunFolderListingErrorCodes.InvalidManifestMetadata,
                        "Google Drive backup-run manifest metadata could not be checked safely.");
                }

                bool hasManifestFile = false;
                foreach (GoogleDriveObjectMetadata match in matches)
                {
                    ValidateManifestMetadata(match, candidate.FolderId);
                    hasManifestFile |= match.Kind == GoogleDriveObjectKind.File;
                }

                if (hasManifestFile)
                    includedCandidates.Add(candidate);
            }

            if (includedCandidates.Any(candidate => candidate.HasExactNameCollision) ||
                includedCandidates
                    .GroupBy(
                        candidate => candidate.ExactName,
                        StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Skip(1).Any()))
            {
                throw Failure(
                    GoogleDriveRunFolderListingErrorCodes.AmbiguousRunName,
                    "Google Drive contains backup-run folder names that cannot be represented safely.");
            }

            return includedCandidates
                .Select(candidate => candidate.ExactName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        private static void ValidateManifestMetadata(
            GoogleDriveObjectMetadata metadata,
            string expectedParentId)
        {
            if (!string.IsNullOrWhiteSpace(metadata.DriveId))
            {
                throw new GoogleDriveRemoteOperationException(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.RootUnsupportedLocation));
            }

            if (metadata.Trashed ||
                !string.Equals(
                    metadata.Name,
                    TransferBackupLocations.ManifestFileName,
                    StringComparison.Ordinal) ||
                !metadata.ParentIds.Contains(
                    expectedParentId,
                    StringComparer.Ordinal))
            {
                throw Failure(
                    GoogleDriveRunFolderListingErrorCodes.InvalidManifestMetadata,
                    "Google Drive backup-run manifest metadata could not be checked safely.");
            }
        }

        private static GoogleDriveRemoteOperationException Failure(
            string errorCode,
            string message) =>
            new(new GoogleDriveRemoteValidationResult(
                GoogleDriveRemoteValidationStatus.Failed,
                errorCode,
                message,
                retryable: false,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false),
                message);
    }
}
