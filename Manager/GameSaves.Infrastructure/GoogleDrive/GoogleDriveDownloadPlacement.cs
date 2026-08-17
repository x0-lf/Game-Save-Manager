namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Moves one validated temporary file to its final name. Placement never
    /// overwrites, never replaces, and never deletes an existing final file:
    /// a destination that appeared during the transfer keeps its content and
    /// the download fails closed.
    /// </summary>
    internal static class GoogleDriveDownloadPlacement
    {
        public static void Place(
            GoogleDriveLocalDownloadDestination destination,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            cancellationToken.ThrowIfCancellationRequested();

            // The stream must be closed before the file can be moved, and the
            // caller keeps ownership of disposal for every other path.
            destination.Stream.Dispose();
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(destination.FinalPath) ||
                    Directory.Exists(destination.FinalPath))
                {
                    throw new GoogleDriveLocalDownloadDestinationException(
                        GoogleDriveLocalDownloadDestinationFailure.AlreadyExists);
                }

                File.Move(
                    destination.TemporaryPath,
                    destination.FinalPath,
                    overwrite: false);
            }
            catch (GoogleDriveLocalDownloadDestinationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (File.Exists(destination.FinalPath))
            {
                throw new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.AlreadyExists);
            }
            catch (UnauthorizedAccessException)
            {
                throw new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.Unwritable);
            }
            catch (DirectoryNotFoundException)
            {
                throw new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.DirectoryUnavailable);
            }
            catch (IOException)
            {
                throw new GoogleDriveLocalDownloadDestinationException(
                    GoogleDriveLocalDownloadDestinationFailure.Unwritable);
            }
        }
    }
}
