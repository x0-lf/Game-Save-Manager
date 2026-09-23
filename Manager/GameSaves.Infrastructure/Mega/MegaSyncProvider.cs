using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Infrastructure.Mega
{
    /// <summary>
    /// Syncs backup runs with the dedicated application folder in one saved MEGA profile.
    /// All sync logic lives in the shared SyncEngine; this provider supplies
    /// the MEGA file system targeting the user's "GameSave Manager Backups" folder.
    /// </summary>
    internal sealed class MegaSyncProvider : ISyncProvider
    {
        private readonly SyncEngine _engine;
        private bool _disposed;

        internal MegaSyncProvider(
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

        public string ProviderName => "Mega";

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
