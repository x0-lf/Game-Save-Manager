using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Builds a provider-neutral runtime view from saved non-secret profile
    /// metadata and exact OAuth-token presence. It performs no OAuth or Drive
    /// API work and never reads protected token bytes.
    /// </summary>
    public sealed class GoogleDriveConnectionSettingsService
        : IGoogleDriveConnectionSettingsService
    {
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly ISecretStore _secretStore;
        private readonly ISyncProviderCatalog _providerCatalog;

        public GoogleDriveConnectionSettingsService(
            ISyncRemoteProfileRepository profileRepository,
            ISecretStore secretStore,
            ISyncProviderCatalog providerCatalog)
        {
            _profileRepository = profileRepository;
            _secretStore = secretStore;
            _providerCatalog = providerCatalog;
        }

        public async Task<GoogleDriveConnectionSettingsResult> GetAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            if (remoteProfileId == Guid.Empty)
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.Failed,
                    GoogleDriveConnectionErrorCodes.InvalidProfileId,
                    "A valid Google Drive remote profile is required.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                SyncProviderDescriptor descriptor =
                    _providerCatalog.GetDescriptor(SyncProviderKind.GoogleDrive);

                if (descriptor.Kind != SyncProviderKind.GoogleDrive)
                {
                    return Failure(
                        GoogleDriveConnectionSettingsResultStatus.Failed,
                        GoogleDriveConnectionErrorCodes.ProviderCatalogUnavailable,
                        "Google Drive connection settings are unavailable.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.Failed,
                    GoogleDriveConnectionErrorCodes.ProviderCatalogUnavailable,
                    "Google Drive connection settings are unavailable.");
            }

            SyncRemoteProfile? profile;

            try
            {
                profile = _profileRepository.GetById(remoteProfileId);
            }
            catch
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.Failed,
                    GoogleDriveConnectionErrorCodes.Failed,
                    "The Google Drive profile could not be read.");
            }

            if (profile is null)
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.ProfileNotFound,
                    GoogleDriveConnectionErrorCodes.ProfileNotFound,
                    "The Google Drive profile was not found.");
            }

            if (profile.ProviderKind != SyncProviderKind.GoogleDrive)
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.WrongProviderKind,
                    GoogleDriveConnectionErrorCodes.WrongProviderKind,
                    "The selected remote profile is not a Google Drive profile.");
            }

            if (profile.ProviderSettings is null)
            {
                if (profile.SettingsError?.Contains(
                        "scope",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Failure(
                        GoogleDriveConnectionSettingsResultStatus.UnsupportedScope,
                        GoogleDriveConnectionErrorCodes.UnsupportedScope,
                        "The saved Google Drive authorization scope is not supported.");
                }

                return profile.SettingsError is null
                    ? Failure(
                        GoogleDriveConnectionSettingsResultStatus.SettingsMissing,
                        GoogleDriveConnectionErrorCodes.SettingsMissing,
                        "Google Drive profile settings are missing.")
                    : Failure(
                        GoogleDriveConnectionSettingsResultStatus.SettingsCorrupted,
                        GoogleDriveConnectionErrorCodes.SettingsCorrupted,
                        "Google Drive profile settings are unreadable or unsupported.");
            }

            if (profile.ProviderSettings is not GoogleDriveSyncRemoteSettings settings ||
                settings.SchemaVersion != GoogleDriveSyncRemoteSettings.CurrentSchemaVersion)
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.SettingsCorrupted,
                    GoogleDriveConnectionErrorCodes.SettingsCorrupted,
                    "Google Drive profile settings are unreadable or unsupported.");
            }

            if (!string.Equals(
                    settings.RequestedScope,
                    GoogleDriveAuthorizationScopes.DriveFile,
                    StringComparison.Ordinal))
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.UnsupportedScope,
                    GoogleDriveConnectionErrorCodes.UnsupportedScope,
                    "The saved Google Drive authorization scope is not supported.");
            }

            bool hasStoredToken;

            try
            {
                hasStoredToken = await _secretStore.ExistsAsync(
                    new SecretKey(profile.Id, SecretNames.OAuthTokenData),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.SecretStoreUnavailable,
                    GoogleDriveConnectionErrorCodes.SecretStoreUnavailable,
                    "Protected Google Drive authentication cannot be checked.");
            }

            try
            {
                var runtimeSettings = new GoogleDriveConnectionSettings(
                    profile.Id,
                    profile.AccountDisplayName,
                    settings.AccountEmail,
                    profile.RemoteFolderId,
                    profile.RemoteRootDisplayName,
                    settings.RequestedScope,
                    hasStoredToken
                        ? GoogleDriveConnectionStatus.StoredAuthenticationAvailable
                        : GoogleDriveConnectionStatus.Disconnected,
                    hasStoredToken);

                return GoogleDriveConnectionSettingsResult.Success(runtimeSettings);
            }
            catch (ArgumentException)
            {
                return Failure(
                    GoogleDriveConnectionSettingsResultStatus.SettingsCorrupted,
                    GoogleDriveConnectionErrorCodes.SettingsCorrupted,
                    "Google Drive profile settings are unreadable or unsupported.");
            }
        }

        private static GoogleDriveConnectionSettingsResult Failure(
            GoogleDriveConnectionSettingsResultStatus status,
            string errorCode,
            string errorMessage) =>
            GoogleDriveConnectionSettingsResult.Failure(
                status,
                errorCode,
                errorMessage);
    }
}
