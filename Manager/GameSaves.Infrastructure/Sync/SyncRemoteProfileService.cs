using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SyncRemoteProfileService : ISyncRemoteProfileService
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly IGoogleDriveValidationCoordinator? _validationCoordinator;

        public SyncRemoteProfileService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore)
            : this(profileRepository, secretStore, validationCoordinator: null)
        {
        }

        internal SyncRemoteProfileService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            IGoogleDriveValidationCoordinator? validationCoordinator)
        {
            _profileRepository = profileRepository;
            _secretStore = secretStore;
            _validationCoordinator = validationCoordinator;
        }

        public async Task<SyncRemoteProfileDeleteResult> DeleteAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            _validationCoordinator?.Cancel(profileId);

            if (_profileRepository.GetById(profileId) is null)
            {
                return new SyncRemoteProfileDeleteResult(
                    ProfileDeleted: false,
                    SecretsDeleted: false,
                    "The remote profile no longer exists.");
            }

            SecretOperationResult secretCleanup =
                await _secretStore.DeleteAllForOwnerAsync(profileId, cancellationToken);

            if (!secretCleanup.Succeeded)
            {
                return new SyncRemoteProfileDeleteResult(
                    ProfileDeleted: false,
                    SecretsDeleted: false,
                    CleanupMessage("Stored authentication could not be removed", secretCleanup));
            }

            try
            {
                _profileRepository.Delete(profileId);
                return new SyncRemoteProfileDeleteResult(
                    ProfileDeleted: true,
                    SecretsDeleted: true);
            }
            catch
            {
                return new SyncRemoteProfileDeleteResult(
                    ProfileDeleted: false,
                    SecretsDeleted: true,
                    "Stored authentication was removed, but the profile configuration could not be deleted.");
            }
        }

        public async Task<SyncRemoteProfileAuthenticationResult> DisconnectAuthenticationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            _validationCoordinator?.Cancel(profileId);

            if (_profileRepository.GetById(profileId) is null)
            {
                return new SyncRemoteProfileAuthenticationResult(
                    SecretsDeleted: false,
                    ProfilePreserved: false,
                    "The remote profile no longer exists.");
            }

            SecretOperationResult cleanup =
                await _secretStore.DeleteAllForOwnerAsync(profileId, cancellationToken);

            return cleanup.Succeeded
                ? new SyncRemoteProfileAuthenticationResult(
                    SecretsDeleted: true,
                    ProfilePreserved: true)
                : new SyncRemoteProfileAuthenticationResult(
                    SecretsDeleted: false,
                    ProfilePreserved: true,
                    CleanupMessage("Stored authentication could not be removed", cleanup));
        }

        public async Task<bool> HasStoredAuthenticationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            foreach (string name in SecretNames.StoredAuthenticationNames)
            {
                if (await _secretStore.ExistsAsync(
                        new SecretKey(profileId, name),
                        cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CleanupMessage(
            string message,
            SecretOperationResult result) =>
            result.ErrorCode is null
                ? $"{message}."
                : $"{message} ({result.ErrorCode}).";
    }
}
