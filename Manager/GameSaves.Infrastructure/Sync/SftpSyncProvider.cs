using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Syncs backup runs with a folder on an SFTP server. All sync logic lives
    /// in the shared SyncEngine; this provider only supplies the SFTP file
    /// system and owns its connection lifetime.
    /// </summary>
    public sealed class SftpSyncProvider : ISyncProvider
    {
        private readonly SftpRemoteFileSystem _fileSystem;
        private readonly SyncEngine _engine;

        internal SftpSyncProvider(
            SftpConnectionSettings settings,
            SftpKnownHostsStore knownHosts,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            RemoteRoot = settings.DisplayRoot;
            _fileSystem = new SftpRemoteFileSystem(settings, knownHosts);

            _engine = new SyncEngine(
                _fileSystem,
                ProviderName,
                RemoteRoot,
                backupHistoryService,
                historyRepository);
        }

        public string ProviderName => "SFTP";

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

        public void Dispose()
        {
            _fileSystem.Dispose();
        }
    }
}
