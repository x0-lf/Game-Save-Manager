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
        /// Validation first, then capacity, both read-only and through the same
        /// retry wrapper as a sync, so a throttled check waits the same bounded
        /// backoff before it gives up. A blocking validation answer ends the
        /// check without reading capacity. Only the user's cancellation escapes;
        /// every other failure becomes an Unavailable or RateLimited report.
        /// </summary>
        public async Task<ProviderHealthReport> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                TransferPreviewWarning? warning = await _fileSystem.ValidateAsync(cancellationToken);

                if (warning is { Severity: TransferWarningSeverity.Error })
                {
                    return new ProviderHealthReport(
                        RateLimitedWarningCodes.Contains(warning.Code)
                            ? ProviderHealthState.RateLimited
                            : ProviderHealthState.Unavailable,
                        warning.Message);
                }

                RemoteCapacity? capacity = await _fileSystem.GetCapacityAsync(cancellationToken);

                return capacity is { FreeBytes: <= 0 }
                    ? new ProviderHealthReport(
                        ProviderHealthState.QuotaExhausted,
                        "The provider reports no free storage space. Uploads will fail until space is freed.",
                        capacity)
                    : new ProviderHealthReport(
                        ProviderHealthState.Healthy,
                        warning?.Message ?? "The remote answered and is ready to sync.",
                        capacity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new ProviderHealthReport(
                    _fileSystem.IsRateLimited(exception)
                        ? ProviderHealthState.RateLimited
                        : ProviderHealthState.Unavailable,
                    exception.Message);
            }
        }

        // Validation answers that mean "throttled", not "broken". These
        // backends report throttling during validation as a warning rather
        // than as an exception the retry wrapper could classify.
        private static readonly HashSet<string> RateLimitedWarningCodes = new(StringComparer.Ordinal)
        {
            GoogleDrive.GoogleDriveRemoteValidationErrorCodes.RateLimited,
            OneDrive.OneDriveRemoteFileSystem.RateLimitedWarningCode
        };

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
