using System;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.Mega
{
    internal interface IMegaRemoteFileSystemFactory
    {
        IRemoteFileSystem Create(Guid remoteProfileId);
    }

    internal sealed class MegaRemoteFileSystemFactory : IMegaRemoteFileSystemFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IMegaApiClient _apiClient;
        private readonly IMegaSessionService _sessionService;

        public MegaRemoteFileSystemFactory(
            ISyncRemoteProfileRepository profileRepository,
            IMegaApiClient apiClient,
            IMegaSessionService sessionService)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        }

        public IRemoteFileSystem Create(Guid remoteProfileId)
        {
            var profile = _profileRepository.GetById(remoteProfileId);
            string? accountEmail = null;
            string? rootFolderName = null;
            if (profile?.ProviderSettings is MegaSyncRemoteSettings megaSettings)
            {
                accountEmail = megaSettings.UserEmail;
                rootFolderName = megaSettings.RootFolderName;
            }
            return new MegaRemoteFileSystem(remoteProfileId, _apiClient, _sessionService, accountEmail, rootFolderName);
        }
    }

    /// <summary>
    /// Creates a sync provider for one saved MEGA cloud profile.
    /// </summary>
    internal interface IMegaSyncProviderFactory
    {
        ISyncProvider Create(Guid remoteProfileId);
    }

    /// <summary>
    /// Profile-scoped construction boundary for the MEGA sync provider.
    /// Refuses an unusable profile before any provider exists, and performs
    /// no network requests or provider activation.
    /// </summary>
    internal sealed class MegaSyncProviderFactory : IMegaSyncProviderFactory
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IMegaRemoteFileSystemFactory _fileSystemFactory;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public MegaSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            IMegaRemoteFileSystemFactory fileSystemFactory,
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
                    $"MEGA remote profile '{remoteProfileId}' was not found.");
            }

            if (profile.ProviderKind != SyncProviderKind.Mega)
            {
                throw new InvalidOperationException(
                    $"Remote profile '{remoteProfileId}' is not a MEGA profile.");
            }

            return new MegaSyncProvider(
                _fileSystemFactory.Create(remoteProfileId),
                _backupHistoryService,
                _historyRepository);
        }
    }
}
