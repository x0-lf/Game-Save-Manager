using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRunArchiveNameService
    {
        Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveRunArchiveListingErrorCodes
    {
        public const string AmbiguousRunName =
            "GoogleDriveRunArchiveNameAmbiguous";
        public const string InvalidMetadata =
            "GoogleDriveRunArchiveMetadataInvalid";
    }

    /// <summary>
    /// Lists the archive container files (.7z, .zip) directly beneath the
    /// application root, the counterpart of the run-folder listing for runs
    /// synced as one compressed file. One paginated child listing, no content
    /// read; sidecar manifests and any other file are not containers and are
    /// left out. Object IDs are never returned or persisted.
    /// </summary>
    internal sealed class GoogleDriveRunArchiveNameService
        : IGoogleDriveRunArchiveNameService
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly IGoogleDriveObjectListingApi _listingApi;

        public GoogleDriveRunArchiveNameService(
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            IGoogleDriveObjectListingApi listingApi)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _listingApi = listingApi ??
                throw new ArgumentNullException(nameof(listingApi));
        }

        public async Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<GoogleDriveObjectMetadata> files;
            try
            {
                files = await _listingApi.ListChildrenAsync(
                    context.Credential,
                    context.RootFolderId,
                    GoogleDriveObjectKind.File,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
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
                    GoogleDriveRunArchiveListingErrorCodes.InvalidMetadata,
                    "Google Drive archive container metadata could not be checked safely.");
            }

            if (files is null)
            {
                throw Failure(
                    GoogleDriveRunArchiveListingErrorCodes.InvalidMetadata,
                    "Google Drive archive container metadata could not be checked safely.");
            }

            var names = new List<string>();
            foreach (GoogleDriveObjectMetadata file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file is null || !IsArchiveName(file.Name))
                    continue;

                ValidateArchive(file, context.RootFolderId);
                names.Add(file.Name);
            }

            // Two containers whose names differ only by case would land on
            // the same local run folder, so they cannot be represented safely.
            if (names
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Skip(1).Any()))
            {
                throw Failure(
                    GoogleDriveRunArchiveListingErrorCodes.AmbiguousRunName,
                    "Google Drive contains archive container names that cannot be represented safely.");
            }

            return names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsArchiveName(string? name) =>
            name is not null &&
            (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        // A Drive-native object (a Docs file named like an archive, a
        // shortcut) has no bytes to download, so it fails closed here rather
        // than being offered as a run that can never be restored.
        private static void ValidateArchive(
            GoogleDriveObjectMetadata metadata,
            string rootFolderId)
        {
            if (!string.IsNullOrWhiteSpace(metadata.DriveId))
            {
                throw new GoogleDriveRemoteOperationException(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.RootUnsupportedLocation));
            }

            if (metadata.Trashed ||
                metadata.Kind != GoogleDriveObjectKind.File ||
                string.IsNullOrWhiteSpace(metadata.Id) ||
                !GoogleDriveFolderChildEntry.IsValidPathSegment(metadata.Name) ||
                !metadata.ParentIds.Contains(rootFolderId, StringComparer.Ordinal) ||
                GoogleDriveRecursiveObjectClassificationPolicy.Classify(metadata.MimeType) !=
                    GoogleDriveRecursiveObjectKind.BlobFile)
            {
                throw Failure(
                    GoogleDriveRunArchiveListingErrorCodes.InvalidMetadata,
                    "Google Drive archive container metadata could not be checked safely.");
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
