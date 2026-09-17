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
        private readonly IRemoteFileSystem _fileSystem;
        private readonly SyncEngine _engine;
        private readonly bool _ownsFileSystem;
        private bool _disposed;

        internal SftpSyncProvider(
            SftpConnectionSettings settings,
            SftpKnownHostsStore knownHosts,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
            : this(
                ValidateSettings(settings),
                new SftpRemoteFileSystem(settings, knownHosts ?? throw new ArgumentNullException(nameof(knownHosts))),
                backupHistoryService,
                historyRepository,
                ownsFileSystem: true)
        {
        }

        internal SftpSyncProvider(
            SftpConnectionSettings settings,
            IRemoteFileSystem fileSystem,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            bool ownsFileSystem = true)
            : this(
                fileSystem,
                (settings ?? throw new ArgumentNullException(nameof(settings))).DisplayRoot,
                backupHistoryService,
                historyRepository,
                ownsFileSystem)
        {
        }

        internal SftpSyncProvider(
            IRemoteFileSystem fileSystem,
            string remoteRoot,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository,
            bool ownsFileSystem = true)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            RemoteRoot = remoteRoot ?? throw new ArgumentNullException(nameof(remoteRoot));
            ArgumentNullException.ThrowIfNull(backupHistoryService);
            ArgumentNullException.ThrowIfNull(historyRepository);
            _ownsFileSystem = ownsFileSystem;

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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_ownsFileSystem && _fileSystem is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private static SftpConnectionSettings ValidateSettings(SftpConnectionSettings settings) =>
            settings ?? throw new ArgumentNullException(nameof(settings));
    }
}
