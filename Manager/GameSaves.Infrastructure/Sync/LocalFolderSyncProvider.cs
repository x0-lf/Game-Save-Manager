using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Syncs backup runs with another local or mounted folder (NAS share, USB
    /// drive, cloud-synced folder). All sync logic lives in the shared
    /// SyncEngine; this provider only supplies the local-folder file system.
    /// Future providers (WebDAV, SFTP, ...) follow the same shape: implement
    /// IRemoteFileSystem and hand it to the engine.
    /// </summary>
    public sealed class LocalFolderSyncProvider : ISyncProvider
    {
        private readonly SyncEngine _engine;

        public LocalFolderSyncProvider(
            string remoteRoot,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            RemoteRoot = remoteRoot;

            _engine = new SyncEngine(
                new LocalFolderRemoteFileSystem(
                    remoteRoot,
                    backupHistoryService.GetBackupBasePath()),
                ProviderName,
                remoteRoot,
                backupHistoryService,
                historyRepository);
        }

        public string ProviderName => "Local folder";

        public string RemoteRoot { get; }

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            return _engine.CreatePreviewAsync(options, cancellationToken);
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            return _engine.ExecuteAsync(plan, options, cancellationToken);
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            return _engine.GetSyncLogAsync(cancellationToken);
        }
    }
}
