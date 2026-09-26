using GameSaves.Core.Transfers;

namespace GameSaves.Core.Sync
{
    /// <summary>One explicitly chosen saved profile in a multi-profile upload.</summary>
    public sealed record SyncDestination(Guid ProfileId, string DisplayName);

    /// <summary>What one destination's own preview found.</summary>
    public sealed record SyncDestinationPreview(
        SyncDestination Destination,
        SyncPlan? Plan,
        string? Error)
    {
        /// <summary>
        /// Executable only when this destination's preview succeeded and found
        /// something to copy; a failed or empty preview is skipped, never
        /// guessed at.
        /// </summary>
        public bool CanExecute => Error is null && Plan is { CanExecute: true };
    }

    public enum SyncDestinationOutcome
    {
        Completed = 0,
        CompletedWithErrors = 1,
        Failed = 2,
        Skipped = 3,
        Cancelled = 4,
        NotStarted = 5
    }

    public sealed record SyncDestinationResult(
        SyncDestination Destination,
        SyncDestinationOutcome Outcome,
        SyncResult? Result,
        string? Message);

    /// <summary>
    /// Uploads one backup set to several saved profiles in one workflow
    /// (SYNC-003). Each destination has its own provider, and therefore its own
    /// SyncEngine, plan, and history row; nothing is shared between them.
    /// Destinations run one at a time in the order chosen, so progress,
    /// throttling, and failures always belong to exactly one destination, and
    /// one destination failing never stops the others. Only the user's
    /// cancellation stops the workflow: the destination running then reports
    /// Cancelled and the rest NotStarted.
    ///
    /// Upload only. Downloading from several remotes into one local base could
    /// meet the same run name with different content on two of them, and the
    /// engine compares each remote with the local side only.
    /// </summary>
    public sealed class MultiTargetSyncCoordinator : IDisposable
    {
        private readonly List<Entry> _entries = [];
        private readonly bool _archiveSync;
        private bool _executed;
        private bool _disposed;

        private MultiTargetSyncCoordinator(bool archiveSync) => _archiveSync = archiveSync;

        public IReadOnlyList<SyncDestinationPreview> Previews =>
            _entries.Select(entry => entry.Preview).ToList();

        /// <summary>
        /// Builds one dry-run plan per destination. A destination whose provider
        /// cannot be created or whose preview throws is recorded with its error
        /// and the others are still previewed.
        /// </summary>
        public static async Task<MultiTargetSyncCoordinator> PreviewAsync(
            IReadOnlyList<SyncDestination> destinations,
            Func<SyncDestination, ISyncProvider> createProvider,
            bool archiveSync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destinations);
            ArgumentNullException.ThrowIfNull(createProvider);

            if (destinations.Count == 0)
                throw new ArgumentException("Choose at least one destination.", nameof(destinations));

            if (destinations.Select(destination => destination.ProfileId).Distinct().Count() != destinations.Count)
                throw new ArgumentException("A destination can be chosen only once.", nameof(destinations));

            var coordinator = new MultiTargetSyncCoordinator(archiveSync);

            try
            {
                foreach (SyncDestination destination in destinations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ISyncProvider? provider = null;

                    try
                    {
                        provider = createProvider(destination);
                        SyncPlan plan = await provider.CreatePreviewAsync(
                            coordinator.Options(dryRun: true),
                            cancellationToken);
                        coordinator._entries.Add(new Entry(
                            provider,
                            new SyncDestinationPreview(destination, plan, null)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        provider?.Dispose();
                        throw;
                    }
                    catch (Exception exception)
                    {
                        provider?.Dispose();
                        coordinator._entries.Add(new Entry(
                            null,
                            new SyncDestinationPreview(destination, null, exception.Message)));
                    }
                }

                return coordinator;
            }
            catch
            {
                coordinator.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Uploads to every destination whose preview can execute, one after
        /// another, through the provider that built its plan. Runs once.
        /// </summary>
        public async Task<IReadOnlyList<SyncDestinationResult>> ExecuteAsync(
            Func<SyncDestination, IProgress<SyncProgress>?>? progressFor = null,
            Action<SyncDestinationResult>? destinationFinished = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_executed)
                throw new InvalidOperationException("These previews were already executed. Build a new preview.");

            _executed = true;
            var results = new List<SyncDestinationResult>(_entries.Count);

            foreach (Entry entry in _entries)
            {
                // Once the user cancels, the destination running reports
                // Cancelled and every later one is not started.
                SyncDestination destination = entry.Preview.Destination;
                SyncDestinationResult result = cancellationToken.IsCancellationRequested
                    ? new SyncDestinationResult(
                        destination,
                        SyncDestinationOutcome.NotStarted,
                        null,
                        "Not started: the upload was cancelled.")
                    : await RunAsync(entry, progressFor?.Invoke(destination), cancellationToken);

                results.Add(result);
                destinationFinished?.Invoke(result);
            }

            return results;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (Entry entry in _entries)
                entry.Provider?.Dispose();
        }

        private async Task<SyncDestinationResult> RunAsync(
            Entry entry,
            IProgress<SyncProgress>? progress,
            CancellationToken cancellationToken)
        {
            SyncDestinationPreview preview = entry.Preview;
            SyncDestination destination = preview.Destination;

            if (entry.Provider is null || preview.Plan is null)
            {
                return new SyncDestinationResult(
                    destination,
                    SyncDestinationOutcome.Skipped,
                    null,
                    $"Not uploaded: the preview failed. {preview.Error}");
            }

            if (!preview.CanExecute)
            {
                string reason = preview.Plan.Warnings
                    .FirstOrDefault(warning => warning.Severity == TransferWarningSeverity.Error)?
                    .Message ?? "nothing new to upload.";

                return new SyncDestinationResult(
                    destination,
                    SyncDestinationOutcome.Skipped,
                    null,
                    $"Not uploaded: {reason}");
            }

            try
            {
                SyncResult result = await entry.Provider.ExecuteAsync(
                    preview.Plan,
                    Options(dryRun: false, progress),
                    cancellationToken);

                TransferPreviewWarning? blocker = result.Warnings
                    .FirstOrDefault(warning => warning.Severity == TransferWarningSeverity.Error);

                return blocker is not null
                    ? new SyncDestinationResult(destination, SyncDestinationOutcome.Failed, result, blocker.Message)
                    : result.HasErrors
                        ? new SyncDestinationResult(
                            destination,
                            SyncDestinationOutcome.CompletedWithErrors,
                            result,
                            "Some runs were not uploaded completely. Nothing was deleted; syncing again is safe.")
                        : new SyncDestinationResult(destination, SyncDestinationOutcome.Completed, result, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new SyncDestinationResult(
                    destination,
                    SyncDestinationOutcome.Cancelled,
                    null,
                    "Cancelled. Files already copied are kept and nothing was deleted.");
            }
            catch (Exception exception)
            {
                // Isolation: this destination failed, the next one still runs.
                return new SyncDestinationResult(
                    destination,
                    SyncDestinationOutcome.Failed,
                    null,
                    exception.Message);
            }
        }

        // Upload only, whatever the single-profile page is set to; the preview
        // and the execution use the same container choice.
        private SyncOptions Options(bool dryRun, IProgress<SyncProgress>? progress = null) => new()
        {
            DryRun = dryRun,
            ConfirmExecution = !dryRun,
            Upload = true,
            Download = false,
            ArchiveSync = _archiveSync,
            Progress = progress
        };

        private sealed record Entry(ISyncProvider? Provider, SyncDestinationPreview Preview);
    }
}
