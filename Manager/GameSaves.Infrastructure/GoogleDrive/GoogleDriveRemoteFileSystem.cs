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

        public GoogleDriveRemoteFileSystemFactory(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveRemoteValidationService validationService,
            IGoogleDriveRootExistenceService rootExistenceService,
            IGoogleDriveFolderExistenceService folderExistenceService,
            IGoogleDriveRunFolderNameService runFolderNameService)
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
                _runFolderNameService);
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
    /// remote primitives. Text, recursive listing, and transfer operations
    /// remain unavailable so SyncEngine cannot treat Google Drive as a
    /// working provider.
    /// </summary>
    internal sealed class GoogleDriveRemoteFileSystem : IRemoteFileSystem
    {
        internal const string OperationsUnavailableMessage =
            "Google Drive remote listing and transfer operations are implemented in later milestones.";

        private readonly Guid _remoteProfileId;
        private readonly IGoogleDriveRemoteValidationService _validationService;
        private readonly IGoogleDriveRootExistenceService _rootExistenceService;
        private readonly IGoogleDriveFolderExistenceService _folderExistenceService;
        private readonly IGoogleDriveRunFolderNameService _runFolderNameService;

        internal GoogleDriveRemoteFileSystem(
            Guid remoteProfileId,
            string displayRoot,
            IGoogleDriveRemoteValidationService validationService,
            IGoogleDriveRootExistenceService rootExistenceService,
            IGoogleDriveFolderExistenceService folderExistenceService,
            IGoogleDriveRunFolderNameService runFolderNameService)
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
            Unsupported<string?>();

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            Unsupported();

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Unsupported<string?>();

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            Unsupported();

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Unsupported<IReadOnlyList<string>>();

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default) =>
            Unsupported<long>();

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default) =>
            Unsupported<long>();

        private static Task Unsupported() =>
            Task.FromException(CreateUnavailableException());

        private static Task<T> Unsupported<T>() =>
            Task.FromException<T>(CreateUnavailableException());

        private static NotSupportedException CreateUnavailableException() =>
            new(OperationsUnavailableMessage);
    }
}
