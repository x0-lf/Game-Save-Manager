namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveBinaryUploadService
    {
        Task<GoogleDriveBinaryUploadResult> UploadAsync(
            string localFilePath,
            GoogleDriveBinaryUploadRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Composes the existing guarded primitives for one binary file only.
    /// Run enumeration and ordering remain owned by SyncEngine.
    /// </summary>
    internal sealed class GoogleDriveBinaryUploadService
        : IGoogleDriveBinaryUploadService
    {
        private readonly Func<string, CancellationToken,
            Task<GoogleDriveLocalUploadSource>> _openSourceAsync;
        private readonly IGoogleDriveRemoteOperationContextFactory _contextFactory;
        private readonly GoogleDriveUploadParentPreparationService
            _parentPreparationService;
        private readonly GoogleDriveCreateOnlyUploadTargetGuard _targetGuard;
        private readonly IGoogleDriveMediaUploadClientFactory _mediaClientFactory;

        public GoogleDriveBinaryUploadService(
            Func<string, CancellationToken,
                Task<GoogleDriveLocalUploadSource>> openSourceAsync,
            IGoogleDriveRemoteOperationContextFactory contextFactory,
            GoogleDriveUploadParentPreparationService parentPreparationService,
            GoogleDriveCreateOnlyUploadTargetGuard targetGuard,
            IGoogleDriveMediaUploadClientFactory mediaClientFactory)
        {
            _openSourceAsync = openSourceAsync ??
                throw new ArgumentNullException(nameof(openSourceAsync));
            _contextFactory = contextFactory ??
                throw new ArgumentNullException(nameof(contextFactory));
            _parentPreparationService = parentPreparationService ??
                throw new ArgumentNullException(nameof(parentPreparationService));
            _targetGuard = targetGuard ??
                throw new ArgumentNullException(nameof(targetGuard));
            _mediaClientFactory = mediaClientFactory ??
                throw new ArgumentNullException(nameof(mediaClientFactory));
        }

        public async Task<GoogleDriveBinaryUploadResult> UploadAsync(
            string localFilePath,
            GoogleDriveBinaryUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            using GoogleDriveLocalUploadSource source =
                await _openSourceAsync(
                    localFilePath,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using GoogleDriveRemoteOperationContext context =
                await _contextFactory.CreateAsync(
                    request.RemoteProfileId,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string parentId = await _parentPreparationService.PrepareAsync(
                context,
                ParentPath(request.RemotePath),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string exactName = request.RemotePath.Segments[^1];
            using IDisposable lease = await _targetGuard.AcquireAsync(
                context,
                parentId,
                exactName,
                GoogleDriveObjectKind.File,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using IGoogleDriveMediaUploadClient mediaClient =
                _mediaClientFactory.Create(context.Credential);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveMediaUploadMetadata response =
                await mediaClient.UploadAsync(
                    parentId,
                    exactName,
                    source.Stream,
                    source.Length,
                    GoogleDriveMediaUploadClient.OpaqueMediaType,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            GoogleDriveUploadResponseValidator.Validate(
                response,
                parentId,
                exactName,
                source.Length);
            cancellationToken.ThrowIfCancellationRequested();

            return new GoogleDriveBinaryUploadResult(
                GoogleDriveBinaryUploadStatus.Completed,
                source.Length);
        }

        private static GoogleDriveRelativePath ParentPath(
            GoogleDriveRelativePath path) =>
            path.Segments.Count == 1
                ? GoogleDriveRelativePath.Root
                : GoogleDriveRelativePath.Parse(
                    string.Join('/', path.Segments.Take(path.Segments.Count - 1)));
    }
}
