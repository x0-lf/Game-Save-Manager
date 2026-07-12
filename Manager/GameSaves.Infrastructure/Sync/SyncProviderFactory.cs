using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SyncProviderFactory : ISyncProviderFactory
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public SyncProviderFactory(
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
        }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot)
        {
            return new LocalFolderSyncProvider(
                remoteRoot,
                _backupHistoryService,
                _historyRepository);
        }
    }
}
