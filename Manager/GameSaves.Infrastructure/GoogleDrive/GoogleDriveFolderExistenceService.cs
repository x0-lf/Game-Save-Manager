namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveFolderExistenceService
    {
        Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Same read-only resolution for a file path, which the engine needs
        /// for the create-only check before an archive container upload.
        /// </summary>
        Task<bool> FileExistsAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Resolves one Drive-relative path beneath the authoritative configured
    /// application root. Resolution is read-only: ambiguous or inaccessible
    /// state fails closed and folder creation is never invoked.
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

        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            ExistsAsync(
                remoteProfileId,
                relativeFolder,
                GoogleDriveObjectKind.Folder,
                cancellationToken);

        public Task<bool> FileExistsAsync(
            Guid remoteProfileId,
            string relativePath,
            CancellationToken cancellationToken = default) =>
            ExistsAsync(
                remoteProfileId,
                relativePath,
                GoogleDriveObjectKind.File,
                cancellationToken);

        private async Task<bool> ExistsAsync(
            Guid remoteProfileId,
            string relativePath,
            GoogleDriveObjectKind kind,
            CancellationToken cancellationToken)
        {
            GoogleDriveRelativePath path =
                GoogleDriveRelativePath.Parse(relativePath);

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
                    kind,
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
                throw Failure(kind);
            }

            if (resolution is null)
                throw Failure(kind);

            if (resolution.Status == GoogleDriveObjectResolutionStatus.NotFound)
                return false;

            if (resolution.Status == GoogleDriveObjectResolutionStatus.Found)
            {
                if (resolution.ObjectKind == kind &&
                    !string.IsNullOrWhiteSpace(resolution.ObjectId))
                {
                    return true;
                }

                throw Failure(kind);
            }

            throw new GoogleDriveRemoteOperationException(
                GoogleDriveRemoteValidationMapper.FromObjectResolution(resolution));
        }

        private static GoogleDriveRemoteOperationException Failure(
            GoogleDriveObjectKind kind)
        {
            var resolution = new GoogleDriveObjectResolutionResult(
                GoogleDriveObjectResolutionStatus.Failed,
                GoogleDriveRelativePath.Root,
                kind,
                errorCode: GoogleDriveObjectResolutionErrorCodes.Failed,
                message: kind == GoogleDriveObjectKind.Folder
                    ? "The Google Drive folder could not be resolved safely."
                    : "The Google Drive file could not be resolved safely.");

            return new GoogleDriveRemoteOperationException(
                GoogleDriveRemoteValidationMapper.FromObjectResolution(resolution));
        }
    }
}
