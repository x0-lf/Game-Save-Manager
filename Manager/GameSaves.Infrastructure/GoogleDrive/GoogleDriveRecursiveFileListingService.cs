namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRecursiveFileListingService
    {
        Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Resolves one requested backup-run folder and returns its validated,
    /// deterministic file paths relative to that folder.
    /// </summary>
    internal sealed class GoogleDriveRecursiveFileListingService
        : IGoogleDriveRecursiveFileListingService
    {
        private readonly IGoogleDriveRunFolderResolver _runFolderResolver;
        private readonly IGoogleDriveOneLevelFileListingService _listingService;

        public GoogleDriveRecursiveFileListingService(
            IGoogleDriveRunFolderResolver runFolderResolver,
            IGoogleDriveOneLevelFileListingService listingService)
        {
            _runFolderResolver = runFolderResolver ??
                throw new ArgumentNullException(nameof(runFolderResolver));
            _listingService = listingService ??
                throw new ArgumentNullException(nameof(listingService));
        }

        public async Task<IReadOnlyList<string>> ListAsync(
            Guid remoteProfileId,
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRecursiveFileListingRequest request;
            try
            {
                request = GoogleDriveRecursiveFileListingRequest.Parse(
                    remoteProfileId,
                    relativeFolder);
            }
            catch (ArgumentException)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.InvalidPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveResolvedRunFolder resolvedRunFolder;
            try
            {
                resolvedRunFolder = await _runFolderResolver.ResolveAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleDriveRecursiveFileListingException exception)
                when (exception.Result.Status ==
                      GoogleDriveRecursiveFileListingStatus.FolderNotFound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Array.Empty<string>();
            }
            catch (GoogleDriveRecursiveFileListingException)
            {
                throw;
            }
            catch
            {
                throw Failure(GoogleDriveRecursiveFileListingStatus.Failed);
            }

            if (resolvedRunFolder is null)
            {
                throw Failure(
                    GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
            }

            using (resolvedRunFolder)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GoogleDriveRecursiveFileListingResult result =
                        await _listingService.ListAsync(
                            resolvedRunFolder,
                            cancellationToken).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    if (result is null)
                    {
                        throw Failure(
                            GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
                    }
                    if (result.Status !=
                        GoogleDriveRecursiveFileListingStatus.Completed)
                    {
                        throw new GoogleDriveRecursiveFileListingException(result);
                    }

                    GoogleDriveRecursiveFileEntry[] entries =
                        GoogleDriveOneLevelFileListingService.ValidateAndOrderFiles(
                            result.Entries,
                            cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return entries
                        .Select(entry => entry.CanonicalRelativePath)
                        .ToArray();
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
                    throw Failure(
                        GoogleDriveRecursiveFileListingStatus.Failed);
                }
            }
        }

        private static GoogleDriveRecursiveFileListingException Failure(
            GoogleDriveRecursiveFileListingStatus status) =>
            GoogleDriveRecursiveFileListingFailureMapper.FromStatus(status);
    }
}
