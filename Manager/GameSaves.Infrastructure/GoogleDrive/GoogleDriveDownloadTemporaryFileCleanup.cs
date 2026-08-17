namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Removes exactly one temporary download file: the one this operation
    /// created, identified by its full path and its temporary suffix. It never
    /// touches the final destination, a directory, or any other file, and a
    /// failed removal never replaces the failure that caused it.
    /// </summary>
    internal static class GoogleDriveDownloadTemporaryFileCleanup
    {
        public static bool Remove(GoogleDriveLocalDownloadDestination destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            destination.Stream.Dispose();
            return Remove(destination.TemporaryPath, destination.FinalPath);
        }

        internal static bool Remove(string temporaryPath, string finalPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath) ||
                !temporaryPath.EndsWith(
                    GoogleDriveLocalDownloadDestination.TemporarySuffix,
                    StringComparison.Ordinal) ||
                string.Equals(temporaryPath, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (!File.Exists(temporaryPath))
                    return false;

                File.Delete(temporaryPath);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    NotSupportedException)
            {
                // A temporary file that cannot be removed is left in place; the
                // original failure or cancellation stays authoritative.
                return false;
            }
        }
    }
}
