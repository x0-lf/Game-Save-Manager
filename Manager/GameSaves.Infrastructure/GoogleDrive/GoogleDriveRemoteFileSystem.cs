using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal interface IGoogleDriveRemoteFileSystemFactory
    {
        IRemoteFileSystem Create(Guid remoteProfileId);
    }

    /// <summary>
    /// Creates profile-scoped Google Drive remote boundaries. Factory
    /// construction and Create perform no authentication or Drive request.
    /// </summary>
    internal sealed class GoogleDriveRemoteFileSystemFactory
        : IGoogleDriveRemoteFileSystemFactory
    {
        private const string DefaultDisplayRoot = "Google Drive";

        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IGoogleDriveRemoteValidationService _validationService;
        private readonly IGoogleDriveRootExistenceService _rootExistenceService;
        private readonly IGoogleDriveFolderExistenceService _folderExistenceService;
        private readonly IGoogleDriveRunFolderNameService _runFolderNameService;
        private readonly IGoogleDriveTextFileReadService _textFileReadService;
        private readonly IGoogleDriveProviderMetadataReadService
            _providerMetadataReadService;
        private readonly IGoogleDriveProviderMetadataReplacementService
            _providerMetadataReplacementService;
        private readonly IGoogleDriveCreateOnlyTextFileService
            _createOnlyTextFileService;
        private readonly IGoogleDriveRecursiveFileListingService
            _recursiveFileListingService;
        private readonly IGoogleDriveBinaryUploadService _binaryUploadService;
        private readonly IGoogleDriveBinaryDownloadService _binaryDownloadService;

        public GoogleDriveRemoteFileSystemFactory(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveRemoteValidationService validationService,
            IGoogleDriveRootExistenceService rootExistenceService,
            IGoogleDriveFolderExistenceService folderExistenceService,
            IGoogleDriveRunFolderNameService runFolderNameService,
            IGoogleDriveTextFileReadService textFileReadService,
            IGoogleDriveProviderMetadataReadService providerMetadataReadService,
            IGoogleDriveProviderMetadataReplacementService
                providerMetadataReplacementService,
            IGoogleDriveCreateOnlyTextFileService createOnlyTextFileService,
            IGoogleDriveRecursiveFileListingService recursiveFileListingService,
            IGoogleDriveBinaryUploadService binaryUploadService,
            IGoogleDriveBinaryDownloadService binaryDownloadService)
        {
            _profileRepository = profileRepository ??
                throw new ArgumentNullException(nameof(profileRepository));
            _validationService = validationService ??
                throw new ArgumentNullException(nameof(validationService));
            _rootExistenceService = rootExistenceService ??
                throw new ArgumentNullException(nameof(rootExistenceService));
            _folderExistenceService = folderExistenceService ??
                throw new ArgumentNullException(nameof(folderExistenceService));
            _runFolderNameService = runFolderNameService ??
                throw new ArgumentNullException(nameof(runFolderNameService));
            _textFileReadService = textFileReadService ??
                throw new ArgumentNullException(nameof(textFileReadService));
            _providerMetadataReadService = providerMetadataReadService ??
                throw new ArgumentNullException(nameof(providerMetadataReadService));
            _providerMetadataReplacementService =
                providerMetadataReplacementService ??
                throw new ArgumentNullException(
                    nameof(providerMetadataReplacementService));
            _createOnlyTextFileService = createOnlyTextFileService ??
                throw new ArgumentNullException(nameof(createOnlyTextFileService));
            _recursiveFileListingService = recursiveFileListingService ??
                throw new ArgumentNullException(nameof(recursiveFileListingService));
            _binaryUploadService = binaryUploadService ??
                throw new ArgumentNullException(nameof(binaryUploadService));
            _binaryDownloadService = binaryDownloadService ??
                throw new ArgumentNullException(nameof(binaryDownloadService));
        }

        public IRemoteFileSystem Create(Guid remoteProfileId)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
            return new GoogleDriveRemoteFileSystem(
                remoteProfileId,
                GetSafeDisplayRoot(profile),
                _validationService,
                _rootExistenceService,
                _folderExistenceService,
                _runFolderNameService,
                _textFileReadService,
                _providerMetadataReadService,
                _providerMetadataReplacementService,
                _createOnlyTextFileService,
                _recursiveFileListingService,
                _binaryUploadService,
                _binaryDownloadService);
        }

        private static string GetSafeDisplayRoot(SyncRemoteProfile? profile)
        {
            if (profile?.ProviderKind != SyncProviderKind.GoogleDrive)
                return DefaultDisplayRoot;

            string? candidate = profile.RemoteRootDisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return DefaultDisplayRoot;

            if (Contains(candidate, profile.RemoteFolderId))
                return DefaultDisplayRoot;

            string? accountEmail =
                (profile.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail;
            if (Contains(candidate, accountEmail, StringComparison.OrdinalIgnoreCase))
                return DefaultDisplayRoot;

            return candidate;
        }

        private static bool Contains(
            string value,
            string? sensitiveValue,
            StringComparison comparison = StringComparison.Ordinal) =>
            !string.IsNullOrWhiteSpace(sensitiveValue) &&
            value.Contains(sensitiveValue, comparison);
    }

    /// <summary>
    /// Google Drive implementation boundary for the currently completed
    /// remote primitives. Google Drive stays inactive in the provider catalog
    /// and factory, so SyncEngine still cannot treat it as a working provider.
    /// </summary>
    internal sealed class GoogleDriveRemoteFileSystem : IRemoteFileSystem
    {
        private readonly Guid _remoteProfileId;
        private readonly IGoogleDriveRemoteValidationService _validationService;
        private readonly IGoogleDriveRootExistenceService _rootExistenceService;
        private readonly IGoogleDriveFolderExistenceService _folderExistenceService;
        private readonly IGoogleDriveRunFolderNameService _runFolderNameService;
        private readonly IGoogleDriveTextFileReadService _textFileReadService;
        private readonly IGoogleDriveProviderMetadataReadService
            _providerMetadataReadService;
        private readonly IGoogleDriveProviderMetadataReplacementService
            _providerMetadataReplacementService;
        private readonly IGoogleDriveCreateOnlyTextFileService
            _createOnlyTextFileService;
        private readonly IGoogleDriveRecursiveFileListingService
            _recursiveFileListingService;
        private readonly IGoogleDriveBinaryUploadService _binaryUploadService;
        private readonly IGoogleDriveBinaryDownloadService _binaryDownloadService;

        internal GoogleDriveRemoteFileSystem(
            Guid remoteProfileId,
            string displayRoot,
            IGoogleDriveRemoteValidationService validationService,
            IGoogleDriveRootExistenceService rootExistenceService,
            IGoogleDriveFolderExistenceService folderExistenceService,
            IGoogleDriveRunFolderNameService runFolderNameService,
            IGoogleDriveTextFileReadService textFileReadService,
            IGoogleDriveProviderMetadataReadService providerMetadataReadService,
            IGoogleDriveProviderMetadataReplacementService
                providerMetadataReplacementService,
            IGoogleDriveCreateOnlyTextFileService createOnlyTextFileService,
            IGoogleDriveRecursiveFileListingService recursiveFileListingService,
            IGoogleDriveBinaryUploadService binaryUploadService,
            IGoogleDriveBinaryDownloadService binaryDownloadService)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }
            if (string.IsNullOrWhiteSpace(displayRoot))
            {
                throw new ArgumentException(
                    "A safe Google Drive display root is required.",
                    nameof(displayRoot));
            }

            _remoteProfileId = remoteProfileId;
            DisplayRoot = displayRoot.Trim();
            _validationService = validationService ??
                throw new ArgumentNullException(nameof(validationService));
            _rootExistenceService = rootExistenceService ??
                throw new ArgumentNullException(nameof(rootExistenceService));
            _folderExistenceService = folderExistenceService ??
                throw new ArgumentNullException(nameof(folderExistenceService));
            _runFolderNameService = runFolderNameService ??
                throw new ArgumentNullException(nameof(runFolderNameService));
            _textFileReadService = textFileReadService ??
                throw new ArgumentNullException(nameof(textFileReadService));
            _providerMetadataReadService = providerMetadataReadService ??
                throw new ArgumentNullException(nameof(providerMetadataReadService));
            _providerMetadataReplacementService =
                providerMetadataReplacementService ??
                throw new ArgumentNullException(
                    nameof(providerMetadataReplacementService));
            _createOnlyTextFileService = createOnlyTextFileService ??
                throw new ArgumentNullException(nameof(createOnlyTextFileService));
            _recursiveFileListingService = recursiveFileListingService ??
                throw new ArgumentNullException(nameof(recursiveFileListingService));
            _binaryUploadService = binaryUploadService ??
                throw new ArgumentNullException(nameof(binaryUploadService));
            _binaryDownloadService = binaryDownloadService ??
                throw new ArgumentNullException(nameof(binaryDownloadService));
        }

        public string DisplayRoot { get; }

        public string GetDisplayPath(string relativePath)
        {
            GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse(relativePath);
            return path.IsRoot
                ? DisplayRoot
                : $"{DisplayRoot.TrimEnd('/')}/{path.Canonical}";
        }

        public async Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            GoogleDriveRemoteValidationResult result =
                await _validationService.ValidateAsync(
                    _remoteProfileId,
                    cancellationToken).ConfigureAwait(false);

            return GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(result);
        }

        public Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default) =>
            _rootExistenceService.ExistsAsync(
                _remoteProfileId,
                cancellationToken);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            _runFolderNameService.ListAsync(
                _remoteProfileId,
                cancellationToken);

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            _folderExistenceService.ExistsAsync(
                _remoteProfileId,
                relativeFolder,
                cancellationToken);

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            _textFileReadService.ReadAsync(
                _remoteProfileId,
                relativePath,
                cancellationToken);

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            _createOnlyTextFileService.CreateAsync(
                _remoteProfileId,
                relativePath,
                content,
                cancellationToken);

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            _providerMetadataReadService.ReadAsync(
                _remoteProfileId,
                relativePath,
                cancellationToken);

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            _providerMetadataReplacementService.ReplaceAsync(
                _remoteProfileId,
                relativePath,
                content,
                cancellationToken);

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            _recursiveFileListingService.ListAsync(
                _remoteProfileId,
                relativeFolder,
                cancellationToken);

        public async Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            if (!GoogleDriveRelativePath.TryParse(
                    relativeRemotePath,
                    out GoogleDriveRelativePath? remotePath) ||
                remotePath is null ||
                remotePath.IsRoot)
            {
                throw GoogleDriveUploadFailureMapper.InvalidTargetPath();
            }

            GoogleDriveBinaryUploadResult result =
                await _binaryUploadService.UploadAsync(
                    localFilePath,
                    new GoogleDriveBinaryUploadRequest(
                        _remoteProfileId,
                        remotePath,
                        PlannedLength(localFilePath)),
                    cancellationToken).ConfigureAwait(false);

            if (result.Status != GoogleDriveBinaryUploadStatus.Completed)
                throw GoogleDriveUploadFailureMapper.FromIncompleteResult(result);

            return result.CompletedBytes;
        }

        /// <summary>
        /// A diagnostic-only planned size. The upload service validates and
        /// opens the source itself, and the validated opened length is the
        /// only value that decides success or completed bytes.
        /// </summary>
        private static long PlannedLength(string localFilePath)
        {
            if (string.IsNullOrWhiteSpace(localFilePath))
                return 0;

            try
            {
                var file = new FileInfo(localFilePath);
                return file.Exists && file.Length > 0 ? file.Length : 0;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException or
                    NotSupportedException)
            {
                return 0;
            }
        }

        public async Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            if (!GoogleDriveRelativePath.TryParse(
                    relativeRemotePath,
                    out GoogleDriveRelativePath? remotePath) ||
                remotePath is null ||
                remotePath.IsRoot)
            {
                throw GoogleDriveDownloadFailureMapper.InvalidSourcePath();
            }

            GoogleDriveBinaryDownloadResult result =
                await _binaryDownloadService.DownloadAsync(
                    new GoogleDriveBinaryDownloadRequest(
                        _remoteProfileId,
                        remotePath),
                    localFilePath,
                    cancellationToken).ConfigureAwait(false);

            if (result.Status != GoogleDriveBinaryDownloadStatus.Completed)
                throw GoogleDriveDownloadFailureMapper.FromIncompleteResult(result);

            return result.CompletedBytes;
        }
    }
}
