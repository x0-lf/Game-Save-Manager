using System;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
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
        private readonly IOneDriveApiClient _apiClient;
        private readonly OneDriveOAuthService _oauthService;
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly IDelayProvider _delay;
        private readonly IRetryBackoffNotifier? _backoffNotifier;

        public OneDriveSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository,
            IOneDriveApiClient apiClient,
            OneDriveOAuthService oauthService,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IDelayProvider delay,
            IRetryBackoffNotifier? backoffNotifier = null)
        {
            _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
            _backupHistoryService = backupHistoryService ?? throw new ArgumentNullException(nameof(backupHistoryService));
            _historyRepository = historyRepository ?? throw new ArgumentNullException(nameof(historyRepository));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            _backoffNotifier = backoffNotifier;
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

            // The root is the fixed app-folder label, so the account email
            // never reaches sync plans or persisted transfer history.
            return new EngineSyncProvider(
                "OneDrive",
                OneDriveRemoteFileSystem.DisplayRootName,
                WithRetries(
                    new OneDriveRemoteFileSystem(remoteProfileId, _apiClient, _oauthService),
                    _delay,
                    _backoffNotifier),
                _backupHistoryService,
                _historyRepository);
        }

        /// <summary>
        /// Retries throttling (429), server errors (5xx), and requests that got no
        /// response (network error or timeout), honouring Retry-After. Retrying a
        /// create-only upload is safe: with conflictBehavior=fail a retry after a
        /// lost success is refused with 409, never turned into an overwrite.
        /// </summary>
        internal static IRemoteFileSystem WithRetries(
            IRemoteFileSystem fileSystem,
            IDelayProvider delay,
            IRetryBackoffNotifier? backoffNotifier) =>
            new RetryingRemoteFileSystem(
                fileSystem,
                delay,
                exception => exception is OneDriveApiException { StatusCode: null or 429 or >= 500 },
                backoffNotifier: backoffNotifier,
                isRateLimited: exception => exception is OneDriveApiException { StatusCode: 429 });
    }
}
