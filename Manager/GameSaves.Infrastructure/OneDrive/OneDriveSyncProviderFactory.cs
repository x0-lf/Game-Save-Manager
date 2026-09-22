using System;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    internal interface IOneDriveRemoteFileSystemFactory
    {
        IRemoteFileSystem Create(Guid remoteProfileId);
    }

    internal sealed class OneDriveRemoteFileSystemFactory : IOneDriveRemoteFileSystemFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IOneDriveApiClient _apiClient;
        private readonly OneDriveOAuthService _oauthService;

        public OneDriveRemoteFileSystemFactory(
            ISyncRemoteProfileRepository profileRepository,
            IOneDriveApiClient apiClient,
            OneDriveOAuthService oauthService)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
        }

        public IRemoteFileSystem Create(Guid remoteProfileId)
        {
            var profile = _profileRepository.GetById(remoteProfileId);
            string? accountEmail = null;
            if (profile?.ProviderSettings is OneDriveSyncRemoteSettings onedriveSettings)
            {
                accountEmail = onedriveSettings.AccountEmail;
            }
            return new OneDriveRemoteFileSystem(remoteProfileId, _apiClient, _oauthService, accountEmail);
        }
    }

    /// <summary>
    /// Creates a sync provider for one saved Microsoft OneDrive profile.
    /// </summary>
    internal interface IOneDriveSyncProviderFactory
    {
        ISyncProvider Create(Guid remoteProfileId);
    }

    /// <summary>
    /// Profile-scoped construction boundary for the Microsoft OneDrive sync provider.
    /// Refuses an unusable profile before any provider exists, and performs
    /// no network requests or provider activation.
    /// </summary>
    internal sealed class OneDriveSyncProviderFactory : IOneDriveSyncProviderFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IOneDriveRemoteFileSystemFactory _fileSystemFactory;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public OneDriveSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            IOneDriveRemoteFileSystemFactory fileSystemFactory,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
            _backupHistoryService = backupHistoryService ?? throw new ArgumentNullException(nameof(backupHistoryService));
            _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
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
                throw new InvalidOperationException(
                    $"Microsoft OneDrive remote profile '{remoteProfileId}' was not found.");
            }

            if (profile.ProviderKind != SyncProviderKind.OneDrive)
            {
                throw new InvalidOperationException(
                    $"Remote profile '{remoteProfileId}' is not a Microsoft OneDrive profile.");
            }

            return new OneDriveSyncProvider(
                _fileSystemFactory.Create(remoteProfileId),
                _backupHistoryService,
                _historyRepository);
        }
    }
}
