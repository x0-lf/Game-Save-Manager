namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveBinaryDownloadService
    {
        Task<GoogleDriveBinaryDownloadResult> DownloadAsync(
            GoogleDriveBinaryDownloadRequest request,
            string localFilePath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Composes the guarded download primitives for one file only. Run
    /// enumeration, manifest rewriting, restore verification, and progress
    /// remain owned by SyncEngine.
    /// </summary>
    internal sealed class GoogleDriveBinaryDownloadService
        : IGoogleDriveBinaryDownloadService
    {
        private readonly Func<string, CancellationToken,
            Task<GoogleDriveLocalDownloadDestination>> _openDestinationAsync;
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly GoogleDriveDownloadSourceResolver _sourceResolver;
        private readonly IGoogleDriveMediaDownloadClientFactory _mediaClientFactory;
        private readonly GoogleDriveDownloadContentStreamer _streamer;

        public GoogleDriveBinaryDownloadService(
            Func<string, CancellationToken,
                Task<GoogleDriveLocalDownloadDestination>> openDestinationAsync,
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            GoogleDriveDownloadSourceResolver sourceResolver,
            IGoogleDriveMediaDownloadClientFactory mediaClientFactory,
            GoogleDriveDownloadContentStreamer streamer)
        {
            _openDestinationAsync = openDestinationAsync ??
                throw new ArgumentNullException(nameof(openDestinationAsync));
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _sourceResolver = sourceResolver ??
                throw new ArgumentNullException(nameof(sourceResolver));
            _mediaClientFactory = mediaClientFactory ??
                throw new ArgumentNullException(nameof(mediaClientFactory));
            _streamer = streamer ??
                throw new ArgumentNullException(nameof(streamer));
        }

        public async Task<GoogleDriveBinaryDownloadResult> DownloadAsync(
            GoogleDriveBinaryDownloadRequest request,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Started);

            GoogleDriveLocalDownloadDestination destination;
            try
            {
                destination = await _openDestinationAsync(
                    localFilePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw Report(exception, GoogleDriveDownloadStage.Failed);
            }

            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.DestinationPrepared);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await DownloadCoreAsync(
                    request,
                    destination,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Cancellation and every failure leave no partial local file.
                GoogleDriveDownloadTemporaryFileCleanup.Remove(destination);
                GoogleDriveDownloadFailureMapper.Log(
                    GoogleDriveDownloadStage.CleanedUp);
                throw Report(
                    exception,
                    exception is OperationCanceledException
                        ? GoogleDriveDownloadStage.Cancelled
                        : GoogleDriveDownloadStage.Failed);
            }
        }

        private static Exception Report(
            Exception exception,
            GoogleDriveDownloadStage stage)
        {
            GoogleDriveDownloadFailureDetails details =
                GoogleDriveDownloadFailureMapper.Classify(exception);
            GoogleDriveDownloadFailureMapper.Log(stage, bytes: 0, details);
            return GoogleDriveDownloadFailureMapper.ToSafeException(
                exception,
                details);
        }

        private async Task<GoogleDriveBinaryDownloadResult> DownloadCoreAsync(
            GoogleDriveBinaryDownloadRequest request,
            GoogleDriveLocalDownloadDestination destination,
            CancellationToken cancellationToken)
        {
            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    request.RemoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveDownloadSource source = await _sourceResolver.ResolveAsync(
                context,
                request.RemotePath,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.SourceResolved);

            using IGoogleDriveMediaDownloadClient mediaClient =
                _mediaClientFactory.Create(context.Credential);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveMediaDownloadMetadata metadata =
                await mediaClient.GetMetadataAsync(
                    source.FileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            long writtenBytes = await _streamer.StreamAsync(
                mediaClient,
                source.FileId,
                destination,
                progress: null,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Transferred,
                writtenBytes);

            GoogleDriveDownloadCompletionValidator.Validate(
                metadata,
                source,
                writtenBytes);
            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Validated,
                writtenBytes);

            // Placement is the last cancellable point. Once the validated file
            // carries its final name the download is complete, and cancelling
            // afterwards must not delete a finished local file.
            GoogleDriveDownloadPlacement.Place(destination, cancellationToken);
            GoogleDriveDownloadFailureMapper.Log(
                GoogleDriveDownloadStage.Placed,
                writtenBytes);

            return new GoogleDriveBinaryDownloadResult(
                GoogleDriveBinaryDownloadStatus.Completed,
                writtenBytes);
        }
    }
}
