using GameSaves.Core.Platform;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SyncProviderFactory : ISyncProviderFactory
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly IGoogleDriveSyncProviderFactory _googleDriveProviders;
        private readonly SftpKnownHostsStore _knownHosts;

        // Internal because IGoogleDriveSyncProviderFactory is internal: a public
        // constructor taking it is CS0051. Dependency injection resolves this
        // through a registration lambda in the composition root, which keeps the
        // dependency explicit here instead of hiding it behind a service
        // locator. See D-028, and D-026 for why the locator stays rejected.
        internal SyncProviderFactory(
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IAppDatabasePathProvider databasePathProvider,
            IGoogleDriveSyncProviderFactory googleDriveProviders)
        {
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
            _googleDriveProviders = googleDriveProviders;

            string appDataDirectory =
                Path.GetDirectoryName(databasePathProvider.GetDatabasePath())
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            _knownHosts = new SftpKnownHostsStore(
                Path.Combine(appDataDirectory, "sftp-known-hosts.json"));
        }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot)
        {
            // The Google Drive case refuses an empty identifier before doing any
            // work; this case refused nothing, so a blank root produced a
            // provider that failed later with an obscure IO error instead. The
            // only production caller validates the selection first, so this
            // guard changes no reachable behaviour.
            if (string.IsNullOrWhiteSpace(remoteRoot))
            {
                throw new ArgumentException(
                    "A remote folder path is required.",
                    nameof(remoteRoot));
            }

            return new LocalFolderSyncProvider(
                remoteRoot,
                _backupHistoryService,
                _historyRepository);
        }

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings)
        {
            return new SftpSyncProvider(
                settings,
                _knownHosts,
                _backupHistoryService,
                _historyRepository);
        }

        // Pure delegation. Every rejection rule already lives in the internal
        // factory and the shared profile validator, so adding a check here
        // would only let a second taxonomy drift into the seam.
        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) =>
            _googleDriveProviders.Create(remoteProfileId);

        public void ForgetSftpHostKey(string host, int port)
        {
            _knownHosts.Forget(host, port);
        }
    }
}
