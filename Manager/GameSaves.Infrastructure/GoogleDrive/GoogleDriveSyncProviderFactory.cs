using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Creates a sync provider for one saved Google Drive profile. The Core
    /// <see cref="ISyncProviderFactory"/> deliberately learns nothing about
    /// Google Drive during Milestone T; see decision D-026.
    /// </summary>
    internal interface IGoogleDriveSyncProviderFactory
    {
        ISyncProvider Create(Guid remoteProfileId);
    }

    /// <summary>
    /// Profile-scoped construction boundary for the Google Drive sync provider.
    /// It refuses an unusable profile before any provider exists, and performs
    /// no authentication, Drive request, or provider activation.
    /// </summary>
    internal sealed class GoogleDriveSyncProviderFactory
        : IGoogleDriveSyncProviderFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IGoogleDriveRemoteFileSystemFactory _fileSystemFactory;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public GoogleDriveSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            IGoogleDriveRemoteFileSystemFactory fileSystemFactory,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            _profileRepository = profileRepository ??
                throw new ArgumentNullException(nameof(profileRepository));
            _fileSystemFactory = fileSystemFactory ??
                throw new ArgumentNullException(nameof(fileSystemFactory));
            _backupHistoryService = backupHistoryService ??
                throw new ArgumentNullException(nameof(backupHistoryService));
            _historyRepository = historyRepository ??
                throw new ArgumentNullException(nameof(historyRepository));
        }

        public ISyncProvider Create(Guid remoteProfileId)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            SyncRemoteProfile? profile = _profileRepository.GetById(remoteProfileId);
            if (profile is null)
            {
                throw new GoogleDriveRemoteOperationException(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.ProfileNotFound));
            }

            GoogleDriveRemoteValidationResult? rejection =
                GoogleDriveRemoteProfileValidator.Validate(profile);
            if (rejection is not null)
                throw new GoogleDriveRemoteOperationException(rejection);

            return new GoogleDriveSyncProvider(
                _fileSystemFactory.Create(remoteProfileId),
                _backupHistoryService,
                _historyRepository);
        }
    }
}
