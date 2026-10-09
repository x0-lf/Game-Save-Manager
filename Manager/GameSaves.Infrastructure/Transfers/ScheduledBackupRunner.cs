using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// How an unattended run ended. The values are the exit codes of
    /// <c>GameSaves.App.exe --run-job &lt;id&gt;</c>; 1 means the command line
    /// itself was malformed.
    /// </summary>
    public enum ScheduledBackupOutcome
    {
        Completed = 0,
        Refused = 2,
        AlreadyRunning = 3,
        Failed = 4
    }

    /// <summary>
    /// Runs one scheduled backup job with nobody at the keyboard. Each run
    /// checks the job against the machine as it is now (destination, game,
    /// profile, then the same preview the Manual backup page builds) and
    /// refuses instead of guessing. The backup itself is the manual backup
    /// engine, so a run only ever adds a new timestamped run: a refusal or a
    /// failure leaves every existing file as it was. Every outcome, refusals
    /// included, is written to History and onto the job.
    /// </summary>
    public sealed class ScheduledBackupRunner
    {
        // HRESULT of ERROR_SHARING_VIOLATION: another process holds the lock.
        private const int SharingViolation = unchecked((int)0x80070020);

        private readonly IScheduledBackupJobRepository _jobs;
        private readonly ISteamDiscoveryService _steamDiscovery;
        private readonly ISteamProfileDetector _profileDetector;
        private readonly IManualBackupService _manualBackup;
        private readonly ITransferHistoryRepository _history;
        private readonly IAppDatabasePathProvider _databasePath;
        private readonly IUtcClock _clock;

        public ScheduledBackupRunner(
            IScheduledBackupJobRepository jobs,
            ISteamDiscoveryService steamDiscovery,
            ISteamProfileDetector profileDetector,
            IManualBackupService manualBackup,
            ITransferHistoryRepository history,
            IAppDatabasePathProvider databasePath,
            IUtcClock clock)
        {
            _jobs = jobs;
            _steamDiscovery = steamDiscovery;
            _profileDetector = profileDetector;
            _manualBackup = manualBackup;
            _history = history;
            _databasePath = databasePath;
            _clock = clock;
        }

        public async Task<ScheduledBackupOutcome> RunAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = _clock.UtcNow;
            ScheduledBackupJob? job;

            try
            {
                job = _jobs.GetAll().FirstOrDefault(j => j.Id == jobId);
            }
            catch (Exception ex)
            {
                return Finish(null, ScheduledBackupOutcome.Failed,
                    $"The scheduled backups could not be read: {ex.Message}", startedUtc);
            }

            // A removed job is revoked: its Windows task may still fire, and
            // each time it is refused and recorded until the task is removed.
            if (job is null)
            {
                return Finish(null, ScheduledBackupOutcome.Refused,
                    $"Scheduled backup {jobId:D} no longer exists. Remove its Windows task.", startedUtc);
            }

            // One run per job. The OS drops the lock with the handle, so a
            // crashed run never leaves the job locked.
            FileStream runLock;

            try
            {
                runLock = new FileStream(
                    LockPath(jobId),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                return Finish(job, ScheduledBackupOutcome.AlreadyRunning,
                    "The previous run of this scheduled backup is still in progress.", startedUtc);
            }
            catch (Exception ex)
            {
                return Finish(job, ScheduledBackupOutcome.Failed,
                    $"The run lock could not be taken: {ex.Message}", startedUtc);
            }

            using (runLock)
            {
                try
                {
                    (ManualBackupPlan? plan, string? refusal) = await PreviewAsync(job, cancellationToken);

                    if (plan is null)
                        return Finish(job, ScheduledBackupOutcome.Refused, refusal!, startedUtc);

                    // The backup service records this run's History row itself.
                    ManualBackupResult result = await _manualBackup.ExecuteAsync(
                        plan,
                        new ManualBackupExecuteOptions
                        {
                            DryRun = false,
                            ConfirmExecution = true,
                            HistoryKind = TransferRunKind.ScheduledBackup
                        },
                        cancellationToken);

                    if (!result.HasErrors)
                    {
                        SaveOutcome(job, ScheduledBackupOutcome.Completed,
                            $"Backed up {result.FilesBackedUp} file(s).");
                        return ScheduledBackupOutcome.Completed;
                    }

                    SaveOutcome(job, ScheduledBackupOutcome.Failed,
                        result.Warnings.FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?.Message ??
                        $"{result.Items.Count(i => i.Status == SaveTransferItemStatus.Failed)} file(s) could not be backed up.");
                    return ScheduledBackupOutcome.Failed;
                }
                catch (Exception ex)
                {
                    return Finish(job, ScheduledBackupOutcome.Failed,
                        $"The run stopped: {ex.Message}", startedUtc);
                }
            }
        }

        // Next to the database, so every host for this user shares it.
        internal string LockPath(Guid jobId) =>
            Path.Combine(
                Path.GetDirectoryName(_databasePath.GetDatabasePath())!,
                $"scheduled-backup-{jobId:N}.lock");

        private async Task<(ManualBackupPlan? Plan, string? Refusal)> PreviewAsync(
            ScheduledBackupJob job,
            CancellationToken cancellationToken)
        {
            // A missing destination usually means an unplugged drive. Creating
            // the folder would hide that, so the run waits for the user.
            if (!Directory.Exists(job.DestinationRoot))
                return (null, $"The destination folder is not available: {job.DestinationRoot}");

            // Only where Steam says its libraries are: no fallback disk scan.
            SteamDiscoveryResult discovery = _steamDiscovery.Discover(
                new SteamDiscoveryOptions { FallbackScanMode = SteamFallbackScanMode.Never },
                null,
                cancellationToken);

            SteamGame? game = discovery.Games.FirstOrDefault(g => g.AppId == job.SteamAppId);

            if (game is null)
                return (null, $"{job.GameName} (app {job.SteamAppId}) is no longer installed.");

            SteamProfile? profile = _profileDetector
                .DetectProfiles(discovery, cancellationToken)
                .FirstOrDefault(p => p.AccountId == job.SteamAccountId);

            if (profile is null)
                return (null, $"Steam profile {job.SteamAccountId} was not found on this computer.");

            ManualBackupPlan plan = await _manualBackup.CreatePreviewAsync(
                game,
                profile,
                job.DestinationRoot,
                new ManualBackupOptions
                {
                    IncludeSteamUserDataGameFolder = job.IncludeSteamUserDataGameFolder,
                    IncludeApprovedMappings = job.IncludeApprovedMappings
                },
                cancellationToken);

            return plan.CanExecute
                ? (plan, null)
                : (null, plan.Warnings.FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?.Message ??
                    "The backup preview found nothing to back up.");
        }

        // Records a run that the backup service did not record itself.
        private ScheduledBackupOutcome Finish(
            ScheduledBackupJob? job,
            ScheduledBackupOutcome outcome,
            string reason,
            DateTimeOffset startedUtc)
        {
            try
            {
                _history.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.ScheduledBackup,
                    GameName: job?.GameName ?? "Unknown scheduled backup",
                    SteamAppId: job?.SteamAppId ?? "",
                    SourceAccountId: job?.SteamAccountId ?? "",
                    TargetAccountId: job?.SteamAccountId ?? "",
                    DryRun: false,
                    OverwriteEnabled: false,
                    BackupEnabled: true,
                    FilesConsidered: 0,
                    FilesCopied: 0,
                    FilesSkipped: 0,
                    FilesFailed: 0,
                    BytesCopied: 0,
                    FilesBackedUp: 0,
                    BackupRootPath: null,
                    BlockedReason: reason,
                    StartedUtc: startedUtc,
                    CompletedUtc: _clock.UtcNow,
                    Items: []));
            }
            catch
            {
                // History is the audit trail, not the run; the exit code still reports.
            }

            if (job is not null)
                SaveOutcome(job, outcome, reason);

            return outcome;
        }

        private void SaveOutcome(ScheduledBackupJob job, ScheduledBackupOutcome outcome, string detail)
        {
            string label = outcome == ScheduledBackupOutcome.AlreadyRunning ? "Skipped" : outcome.ToString();

            try
            {
                _jobs.RecordOutcome(job.Id, _clock.UtcNow, $"{label}: {detail}");
            }
            catch
            {
                // Same as History: the outcome note must never fail the run.
            }
        }
    }
}
