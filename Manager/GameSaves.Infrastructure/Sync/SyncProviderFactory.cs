using GameSaves.Core.Platform;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SyncProviderFactory : ISyncProviderFactory
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;
        private readonly SftpKnownHostsStore _knownHosts;

        public SyncProviderFactory(
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            IAppDatabasePathProvider databasePathProvider)
        {
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;

            string appDataDirectory =
                Path.GetDirectoryName(databasePathProvider.GetDatabasePath())
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            _knownHosts = new SftpKnownHostsStore(
                Path.Combine(appDataDirectory, "sftp-known-hosts.json"));
        }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot)
        {
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

        public void ForgetSftpHostKey(string host, int port)
        {
            _knownHosts.Forget(host, port);
        }
    }
}
