using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// The only delete operation in the application. A run folder is only ever
    /// deleted when it is strictly inside the application backup base AND still
    /// contains its manifest.json - anything else is skipped, never removed.
    /// </summary>
    public sealed class BackupCleanupService : IBackupCleanupService
    {
        private readonly IBackupHistoryService _backupHistoryService;
        private readonly ITransferHistoryRepository _historyRepository;

        public BackupCleanupService(
            IBackupHistoryService backupHistoryService,
            ITransferHistoryRepository historyRepository)
        {
            _backupHistoryService = backupHistoryService;
            _historyRepository = historyRepository;
        }

        public async Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            var warnings = new List<TransferPreviewWarning>();
            var items = new List<BackupCleanupItemResult>();

            if (!options.DryRun && !options.ConfirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Cleanup was blocked because execution was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(options, items, warnings, startedUtc);
            }

            if (options.KeepNewestRuns < 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "InvalidKeepCount",
                    "Cleanup was blocked: the number of runs to keep cannot be negative.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(options, items, warnings, startedUtc);
            }

            // Newest-first, base-scoped, manifest-bearing runs only.
            IReadOnlyList<TransferBackupRunInfo> runs =
                await _backupHistoryService.GetRunsAsync(cancellationToken);

            IEnumerable<TransferBackupRunInfo> candidates =
                runs.Skip(options.KeepNewestRuns);

            if (options.DeleteOlderThanDays is int days)
            {
                DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-days);

                candidates = candidates.Where(run =>
                    run.Manifest.StartedUtc < cutoff);
            }

            var candidateList = candidates.ToList();

            if (candidateList.Count == 0)
            {
                warnings.Add(new TransferPreviewWarning(
                    "NothingToCleanUp",
                    "No backup runs match the retention policy. Nothing will be deleted.",
                    TransferWarningSeverity.Info));

                return RecordAndBuild(options, items, warnings, startedUtc);
            }

            string basePath = _backupHistoryService.GetBackupBasePath();

            await Task.Run(() =>
            {
                foreach (TransferBackupRunInfo run in candidateList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    items.Add(DeleteOneRun(run, basePath, options.DryRun));
                }
            }, cancellationToken);

            return RecordAndBuild(options, items, warnings, startedUtc);
        }

        public async Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

            var warnings = new List<TransferPreviewWarning>();
            var items = new List<BackupCleanupItemResult>();

            var options = new BackupCleanupOptions
            {
                DryRun = false,
                ConfirmExecution = confirmExecution
            };

            if (!confirmExecution)
            {
                warnings.Add(new TransferPreviewWarning(
                    "ExecutionNotConfirmed",
                    "Deleting the backup run was blocked because it was not explicitly confirmed.",
                    TransferWarningSeverity.Error));

                return RecordAndBuild(options, items, warnings, startedUtc, run.Manifest.Game);
            }

            string basePath = _backupHistoryService.GetBackupBasePath();

            await Task.Run(
                () => items.Add(DeleteOneRun(run, basePath, dryRun: false)),
                cancellationToken);

            return RecordAndBuild(options, items, warnings, startedUtc, run.Manifest.Game);
        }

        private static BackupCleanupItemResult DeleteOneRun(
            TransferBackupRunInfo run,
            string basePath,
            bool dryRun)
        {
            try
            {
                // Defense in depth: never delete anything that is not strictly
                // inside the backup base, and never delete a folder that no
                // longer looks like a backup run.
                if (!TransferPathGuard.IsStrictlyUnderRoot(run.BackupRootPath, basePath))
                {
                    return new BackupCleanupItemResult(
                        run,
                        0,
                        Deleted: false,
                        BackupCleanupItemStatus.SkippedOutsideBase,
                        "The run folder is not inside the application backup base and is never deleted.");
                }

                if (!File.Exists(Path.Combine(run.BackupRootPath, TransferBackupLocations.ManifestFileName)))
                {
                    return new BackupCleanupItemResult(
                        run,
                        0,
                        Deleted: false,
                        BackupCleanupItemStatus.SkippedInvalidRun,
                        "The folder no longer contains a backup manifest and is never deleted.");
                }

                long bytes = MeasureDirectory(run.BackupRootPath);

                if (dryRun)
                {
                    return new BackupCleanupItemResult(
                        run,
                        bytes,
                        Deleted: false,
                        BackupCleanupItemStatus.DryRun,
                        "Would be deleted by this retention policy.");
                }

                Directory.Delete(run.BackupRootPath, recursive: true);

                return new BackupCleanupItemResult(
                    run,
                    bytes,
                    Deleted: true,
                    BackupCleanupItemStatus.Deleted,
                    null);
            }
            catch (Exception ex)
            {
                return new BackupCleanupItemResult(
                    run,
                    0,
                    Deleted: false,
                    BackupCleanupItemStatus.Failed,
                    ex.Message);
            }
        }

        private BackupCleanupResult RecordAndBuild(
            BackupCleanupOptions options,
            IReadOnlyList<BackupCleanupItemResult> items,
            IReadOnlyList<TransferPreviewWarning> warnings,
            DateTimeOffset startedUtc,
            string? gameName = null)
        {
            int runsDeleted = items.Count(item => item.Deleted);

            int runsSkipped = items.Count(item =>
                !item.Deleted &&
                item.Status != BackupCleanupItemStatus.Unknown);

            long bytesFreed = items
                .Where(item => item.Deleted || item.Status == BackupCleanupItemStatus.DryRun)
                .Sum(item => item.Bytes);

            var result = new BackupCleanupResult(
                DryRun: options.DryRun,
                RunsConsidered: items.Count,
                RunsDeleted: runsDeleted,
                RunsSkipped: runsSkipped,
                BytesFreed: bytesFreed,
                Items: items,
                Warnings: warnings);

            TryRecordHistory(options, result, startedUtc, gameName);

            return result;
        }

        private void TryRecordHistory(
            BackupCleanupOptions options,
            BackupCleanupResult result,
            DateTimeOffset startedUtc,
            string? gameName)
        {
            try
            {
                string? blockedReason = result.Warnings
                    .FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?
                    .Message;

                _historyRepository.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.Cleanup,
                    GameName: gameName ?? "(backup cleanup)",
                    SteamAppId: "-",
                    SourceAccountId: "-",
                    TargetAccountId: "-",
                    DryRun: options.DryRun,
                    OverwriteEnabled: false,
                    BackupEnabled: false,
                    FilesConsidered: result.RunsConsidered,
                    FilesCopied: result.RunsDeleted,
                    FilesSkipped: result.RunsSkipped,
                    FilesFailed: result.Items.Count(i => i.Status == BackupCleanupItemStatus.Failed),
                    BytesCopied: result.BytesFreed,
                    FilesBackedUp: 0,
                    BackupRootPath: null,
                    BlockedReason: blockedReason,
                    StartedUtc: startedUtc,
                    CompletedUtc: DateTimeOffset.UtcNow,
                    Items: result.Items
                        .Select(i => new TransferRunItemRecord(
                            i.Run.BackupRootPath,
                            string.Empty,
                            i.Bytes,
                            i.Deleted,
                            i.Status.ToString(),
                            i.Error,
                            BackupFile: null))
                        .ToList()));
            }
            catch
            {
                // History is an audit trail; a recording failure must never
                // fail the cleanup itself.
            }
        }

        private static long MeasureDirectory(string path)
        {
            long bytes = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Size is informational only.
                    }
                }
            }
            catch
            {
                return bytes;
            }

            return bytes;
        }
    }
}
