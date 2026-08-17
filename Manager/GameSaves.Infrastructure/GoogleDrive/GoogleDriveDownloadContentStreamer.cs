namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Streams one Drive file's content straight into the prepared temporary
    /// file. The destination stream is handed to the media client unchanged,
    /// so no copy of the payload is ever materialized in this process, and the
    /// number of bytes actually written to disk is what the caller receives.
    /// </summary>
    internal sealed class GoogleDriveDownloadContentStreamer
    {
        public async Task<long> StreamAsync(
            IGoogleDriveMediaDownloadClient client,
            string fileId,
            GoogleDriveLocalDownloadDestination destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(destination);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                throw new ArgumentException(
                    "An authoritative file ID is required.",
                    nameof(fileId));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await client.DownloadAsync(
                fileId,
                destination.Stream,
                progress,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await destination.Stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            destination.Stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();

            return destination.Stream.Length;
        }
    }
}
