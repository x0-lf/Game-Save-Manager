using GameSaves.Core.Sync;
using System;
using System.Linq;

namespace GameSaves.App.Services
{
    public interface ISyncRemoteProfileMigrationService
    {
        SyncUiSettings LoadAndMigrate();
    }

    /// <summary>
    /// Converts meaningful pre-profile UI settings into one named SQLite
    /// profile. The JSON marker prevents a deleted migrated profile from being
    /// recreated on later starts.
    /// </summary>
    public sealed class SyncRemoteProfileMigrationService : ISyncRemoteProfileMigrationService
    {
        private readonly ISyncSettingsStore _settingsStore;
        private readonly ISyncRemoteProfileRepository _profileRepository;
        private readonly IUtcClock _clock;

        public SyncRemoteProfileMigrationService(
            ISyncSettingsStore settingsStore,
            ISyncRemoteProfileRepository profileRepository,
            IUtcClock clock)
        {
            _settingsStore = settingsStore;
            _profileRepository = profileRepository;
            _clock = clock;
        }

        public SyncUiSettings LoadAndMigrate()
        {
            SyncUiSettings settings = _settingsStore.Load();

            if (settings.LegacyProfileMigrationCompleted)
                return settings;

            SyncRemoteProfile? migrated = BuildMigratedProfile(settings);

            if (migrated is not null)
            {
                SyncRemoteProfile? existing = _profileRepository.GetAll()
                    .FirstOrDefault(profile =>
                        profile.ProviderKind == migrated.ProviderKind &&
                        profile.DisplayName.Equals(
                            migrated.DisplayName,
                            StringComparison.OrdinalIgnoreCase));

                migrated = existing ?? _profileRepository.Create(migrated);
            }

            SyncUiSettings upgraded = settings with
            {
                SchemaVersion = SyncUiSettings.CurrentSchemaVersion,
                SelectedRemoteProfileId = migrated?.Id,
                LegacyProfileMigrationCompleted = true
            };
            _settingsStore.Save(upgraded);
            return upgraded;
        }

        private SyncRemoteProfile? BuildMigratedProfile(SyncUiSettings settings)
        {
            DateTimeOffset now = _clock.UtcNow;

            if (settings.SelectedProviderKind == SyncProviderKind.LocalFolder &&
                !string.IsNullOrWhiteSpace(settings.LocalFolderPath))
            {
                string path = settings.LocalFolderPath.Trim();
                return new SyncRemoteProfile(
                    Guid.NewGuid(),
                    "Migrated Local Folder",
                    SyncProviderKind.LocalFolder,
                    AccountDisplayName: null,
                    RemoteRootDisplayName: path,
                    ProviderSettings: new LocalFolderSyncRemoteSettings(path),
                    CreatedUtc: now,
                    UpdatedUtc: now,
                    LastUsedUtc: null,
                    LastSuccessfulConnectionUtc: null,
                    RemoteFolderId: null);
            }

            bool meaningfulSftp = settings.SelectedProviderKind == SyncProviderKind.Sftp &&
                (!string.IsNullOrWhiteSpace(settings.SftpHost) ||
                 !string.IsNullOrWhiteSpace(settings.SftpUsername) ||
                 !string.IsNullOrWhiteSpace(settings.SftpKeyFilePath) ||
                 !settings.SftpRemotePath.Equals("/gamesave-sync", StringComparison.Ordinal));

            if (!meaningfulSftp)
                return null;

            int port = int.TryParse(settings.SftpPort, out int parsedPort) &&
                       parsedPort is >= 1 and <= 65535
                ? parsedPort
                : 22;
            string host = settings.SftpHost.Trim();
            string username = settings.SftpUsername.Trim();
            string remotePath = string.IsNullOrWhiteSpace(settings.SftpRemotePath)
                ? "/gamesave-sync"
                : settings.SftpRemotePath.Trim();
            string displayRoot = BuildSftpDisplayRoot(username, host, port, remotePath);

            return new SyncRemoteProfile(
                Guid.NewGuid(),
                "Migrated SFTP Server",
                SyncProviderKind.Sftp,
                AccountDisplayName: string.IsNullOrEmpty(username) && string.IsNullOrEmpty(host)
                    ? null
                    : $"{username}@{host}",
                RemoteRootDisplayName: displayRoot,
                ProviderSettings: new SftpSyncRemoteSettings(
                    host,
                    port,
                    username,
                    settings.SftpUsePrivateKey
                        ? SftpAuthMethod.PrivateKey
                        : SftpAuthMethod.Password,
                    string.IsNullOrWhiteSpace(settings.SftpKeyFilePath)
                        ? null
                        : settings.SftpKeyFilePath.Trim(),
                    remotePath),
                CreatedUtc: now,
                UpdatedUtc: now,
                LastUsedUtc: null,
                LastSuccessfulConnectionUtc: null,
                RemoteFolderId: null);
        }

        private static string BuildSftpDisplayRoot(
            string username,
            string host,
            int port,
            string remotePath) =>
            $"sftp://{username}@{host}:{port}" +
            (remotePath.StartsWith('/') ? remotePath : "/" + remotePath);
    }
}
