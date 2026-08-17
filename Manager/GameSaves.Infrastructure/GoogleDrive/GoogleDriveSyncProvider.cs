using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Syncs backup runs with a folder in one saved Google Drive profile. All
    /// sync logic lives in the shared SyncEngine; this provider only supplies
    /// the Google Drive file system, exactly as the local-folder and SFTP
    /// providers supply theirs. Google Drive stays inactive in the provider
    /// catalog and factory, so nothing constructs this yet outside its own
    /// internal factory.
    /// </summary>
    internal sealed class GoogleDriveSyncProvider : ISyncProvider
    {
        private readonly SyncEngine _engine;
        private bool _disposed;

        internal GoogleDriveSyncProvider(
            IRemoteFileSystem fileSystem,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(backupHistoryService);
            ArgumentNullException.ThrowIfNull(historyRepository);

            // The sanitized display root is the only root this provider knows.
            // It reaches sync plans and persisted transfer history, so it must
            // never be an account address, object ID, or Drive URL.
            RemoteRoot = fileSystem.DisplayRoot;

            _engine = new SyncEngine(
                fileSystem,
                ProviderName,
                RemoteRoot,
                backupHistoryService,
                historyRepository);
        }

        public string ProviderName => "Google Drive";

        public string RemoteRoot { get; }

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _engine.CreatePreviewAsync(options, cancellationToken);
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _engine.ExecuteAsync(plan, options, cancellationToken);
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _engine.GetSyncLogAsync(cancellationToken);
        }

        /// <summary>
        /// Each Google Drive operation owns its own short-lived authenticated
        /// context, so this provider holds no connection to release. Disposal
        /// therefore only closes the provider to further use, and repeating it
        /// changes nothing. A test asserts the Drive file system is still not
        /// disposable, so making it disposable fails until a release is added
        /// here.
        /// </summary>
        public void Dispose() => _disposed = true;
    }
}
