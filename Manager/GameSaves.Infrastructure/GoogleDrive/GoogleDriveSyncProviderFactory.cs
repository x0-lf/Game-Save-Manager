using GameSaves.Core.Sync;

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
        private const string ProviderPendingMessage =
            "The Google Drive sync provider is not available yet.";

        private readonly ISyncRemoteProfileRepository _profileRepository;

        public GoogleDriveSyncProviderFactory(
            ISyncRemoteProfileRepository profileRepository)
        {
            _profileRepository = profileRepository ??
                throw new ArgumentNullException(nameof(profileRepository));
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

            // The provider wrapper itself lands in the next Milestone T task.
            // Until then a usable profile still stops here rather than
            // returning a partially wired provider.
            throw new NotSupportedException(ProviderPendingMessage);
        }
    }
}
