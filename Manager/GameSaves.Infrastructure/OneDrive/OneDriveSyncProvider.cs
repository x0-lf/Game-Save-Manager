using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.OneDrive
{
    /// <summary>
    /// Syncs backup runs with the sandboxed application folder in one saved Microsoft OneDrive profile.
    /// All sync logic lives in the shared SyncEngine; this provider supplies
    /// the OneDrive file system targeting drive/special/approot.
    /// </summary>
    internal sealed class OneDriveSyncProvider : ISyncProvider
    {
        private readonly SyncEngine _engine;
        private bool _disposed;

        internal OneDriveSyncProvider(
            IRemoteFileSystem fileSystem,
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(backupHistoryService);
            ArgumentNullException.ThrowIfNull(historyRepository);

            RemoteRoot = fileSystem.DisplayRoot;

            _engine = new SyncEngine(
                fileSystem,
                ProviderName,
                RemoteRoot,
                backupHistoryService,
                historyRepository);
        }

        public string ProviderName => "OneDrive";

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

        public void Dispose() => _disposed = true;
    }
}
