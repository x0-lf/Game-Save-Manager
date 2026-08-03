namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveFolderExistenceService
    {
        Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Resolves one Drive-relative folder path beneath the authoritative
    /// configured application root. Resolution is read-only: ambiguous or
    /// inaccessible state fails closed and folder creation is never invoked.
    /// </summary>
    internal sealed class GoogleDriveFolderExistenceService
        : IGoogleDriveFolderExistenceService
    {
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;

        public GoogleDriveFolderExistenceService(
            IGoogleDriveRemoteOperationContextFactory contextFactory)
        {
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRelativePath path =
                GoogleDriveRelativePath.Parse(relativeFolder);

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    remoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveObjectResolutionResult resolution;
            try
            {
                resolution = await context.Resolver.ResolveAsync(
                    context.RootFolderId,
                    path,
                    GoogleDriveObjectKind.Folder,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRemoteOperationException)
            {
                throw;
            }
            catch
            {
                throw Failure();
            }

            if (resolution is null)
                throw Failure();

            if (resolution.Status == GoogleDriveObjectResolutionStatus.NotFound)
                return false;

            if (resolution.Status == GoogleDriveObjectResolutionStatus.Found)
            {
                if (resolution.ObjectKind == GoogleDriveObjectKind.Folder &&
                    !string.IsNullOrWhiteSpace(resolution.ObjectId))
                {
                    return true;
                }

                throw Failure();
            }

            throw new GoogleDriveRemoteOperationException(
                GoogleDriveRemoteValidationMapper.FromObjectResolution(resolution));
        }

        private static GoogleDriveRemoteOperationException Failure()
        {
            var resolution = new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Failed,
                GoogleDriveRelativePath.Root,
                GoogleDriveObjectKind.Folder,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Failed,
                message: "The Google Drive folder could not be resolved safely.");

            return new GoogleDriveRemoteOperationException(
                GoogleDriveRemoteValidationMapper.FromObjectResolution(resolution));
        }
    }
}
