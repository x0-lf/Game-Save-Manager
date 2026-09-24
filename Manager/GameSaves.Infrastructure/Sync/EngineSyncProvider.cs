using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// The one ISyncProvider for every backend. All sync logic lives in the
    /// shared SyncEngine; a backend only supplies an IRemoteFileSystem, and its
    /// factory supplies the display name and root. RemoteRoot reaches sync
    /// plans and persisted transfer history, so a factory must only pass a
    /// root that is safe to show and store.
    /// </summary>
    internal sealed class EngineSyncProvider : ISyncProvider
    {
        private readonly IRemoteFileSystem _fileSystem;
        private readonly SyncEngine _engine;
        private bool _disposed;

        internal EngineSyncProvider(
            string providerName,
            string remoteRoot,
            IRemoteFileSystem fileSystem,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
            RemoteRoot = remoteRoot ?? throw new ArgumentNullException(nameof(remoteRoot));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            ArgumentNullException.ThrowIfNull(backupHistoryService);
            ArgumentNullException.ThrowIfNull(historyRepository);

            _engine = new SyncEngine(
                _fileSystem,
                ProviderName,
                RemoteRoot,
                backupHistoryService,
                historyRepository);
        }

        public string ProviderName { get; }

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
        /// Closes the provider to further use and releases the file system when
        /// it holds a connection (SFTP does). The local-folder, Google Drive, and
        /// OneDrive file systems are not disposable: each Drive or Graph
        /// operation owns its own short-lived authenticated context, so there is
        /// nothing to release. Repeating Dispose changes nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            (_fileSystem as IDisposable)?.Dispose();
        }
    }
}
