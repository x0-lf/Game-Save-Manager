using GameSaves.Core.Platform;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Runs one scheduled sync with nobody at the keyboard. Each run checks the
    /// job against the machine as it is now (the profile, the local backup
    /// base, the stored sign-in, the provider's health, then the same preview
    /// the Sync page builds) and refuses instead of guessing. The sync itself
    /// is the shared engine, so a run only ever adds the runs missing on one
    /// side: nothing is overwritten or deleted, and conflicts are skipped.
    /// Outcomes are the exit codes of <c>GameSaves.App.exe --run-sync &lt;id&gt;</c>,
    /// shared with scheduled backups. Every outcome is written to History and
    /// onto the job; the engine records an executed run itself.
    /// </summary>
    public sealed class ScheduledSyncRunner
    {
        // HRESULT of ERROR_SHARING_VIOLATION: another process holds the lock.
        private const int SharingViolation = unchecked((int)0x80070020);

        private readonly IScheduledSyncJobRepository _jobs;
        private readonly ISyncRemoteProfileRepository _profiles;
        private readonly ISecretStore _secrets;
        private readonly ISyncProviderFactory _providers;
        private readonly IBackupHistoryService _backups;
        private readonly ITransferHistoryRepository _history;
        private readonly IAppDatabasePathProvider _databasePath;
        private readonly IUtcClock _clock;

        public ScheduledSyncRunner(
            IScheduledSyncJobRepository jobs,
            ISyncRemoteProfileRepository profiles,
            ISecretStore secrets,
            ISyncProviderFactory providers,
            IBackupHistoryService backups,
            ITransferHistoryRepository history,
            IAppDatabasePathProvider databasePath,
            IUtcClock clock)
        {
            _jobs = jobs;
            _profiles = profiles;
            _secrets = secrets;
            _providers = providers;
            _backups = backups;
            _history = history;
            _databasePath = databasePath;
            _clock = clock;
        }

        public async Task<ScheduledBackupOutcome> RunAsync(
            Guid jobId,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset startedUtc = _clock.UtcNow;
            ScheduledSyncJob? job;

            try
            {
                job = _jobs.GetAll().FirstOrDefault(j => j.Id == jobId);
            }
            catch (Exception ex)
            {
                return Finish(null, ScheduledBackupOutcome.Failed,
                    $"The scheduled syncs could not be read: {ex.Message}", startedUtc);
            }

            // A removed job is revoked: its Windows task may still fire, and
            // each time it is refused and recorded until the task is removed.
            if (job is null)
            {
                return Finish(null, ScheduledBackupOutcome.Refused,
                    $"Scheduled sync {jobId:D} no longer exists. Remove its Windows task.", startedUtc);
            }

            // One run per profile, whatever job started it. The OS drops the
            // lock with the handle, so a crashed run never leaves it locked.
            FileStream runLock;

            try
            {
                runLock = new FileStream(
                    LockPath(job.ProfileId),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                return Finish(job, ScheduledBackupOutcome.AlreadyRunning,
                    "The previous sync of this profile is still in progress.", startedUtc);
            }
            catch (Exception ex)
            {
                return Finish(job, ScheduledBackupOutcome.Failed,
                    $"The run lock could not be taken: {ex.Message}", startedUtc);
            }

            using (runLock)
            {
                SyncRemoteProfile? profile = _profiles.GetById(job.ProfileId);

                if (profile is null)
                {
                    return Finish(job, ScheduledBackupOutcome.Refused,
                        $"Remote profile '{job.ProfileName}' no longer exists. Remove this scheduled sync.", startedUtc);
                }

                if (await RefusalAsync(profile, cancellationToken) is { } refusal)
                    return Finish(job, ScheduledBackupOutcome.Refused, refusal, startedUtc);

                ISyncProvider provider;
                SyncPlan plan;

                // Nothing has been written yet, so a remote that cannot be read
                // is a refusal, not a failure.
                try
                {
                    provider = CreateProvider(profile);
                }
                catch (Exception ex)
                {
                    return Finish(job, ScheduledBackupOutcome.Refused,
                        $"The provider could not be prepared: {ex.Message}", startedUtc);
                }

                using (provider)
                {
                    try
                    {
                        ProviderHealthReport health = await provider.CheckHealthAsync(cancellationToken);

                        if (health.State != ProviderHealthState.Healthy)
                        {
                            return Finish(job, ScheduledBackupOutcome.Refused,
                                $"{HealthLabel(health.State)}: {health.Reason}", startedUtc);
                        }

                        plan = await provider.CreatePreviewAsync(
                            new SyncOptions
                            {
                                Upload = job.Upload,
                                Download = job.Download,
                                ArchiveSync = job.ArchiveSync
                            },
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        return Finish(job, ScheduledBackupOutcome.Refused,
                            $"The remote could not be read: {ex.Message}", startedUtc);
                    }

                    if (plan.HasErrors)
                    {
                        return Finish(job, ScheduledBackupOutcome.Refused,
                            plan.Warnings.First(w => w.Severity == TransferWarningSeverity.Error).Message, startedUtc);
                    }

                    // Both sides already agree: the normal result of a recurring
                    // sync, recorded as completed with nothing copied.
                    if (!plan.CanExecute)
                    {
                        return Finish(job, ScheduledBackupOutcome.Completed,
                            $"Nothing to copy: {plan.InSyncCount} run(s) in sync, {plan.ConflictCount} conflict(s) skipped.",
                            startedUtc, plan);
                    }

                    try
                    {
                        // The engine records this run's History row itself.
                        SyncResult result = await provider.ExecuteAsync(
                            plan,
                            new SyncOptions
                            {
                                DryRun = false,
                                ConfirmExecution = true,
                                Upload = job.Upload,
                                Download = job.Download,
                                ArchiveSync = job.ArchiveSync,
                                HistoryKind = TransferRunKind.ScheduledSync,
                                HistoryLabel = profile.DisplayName
                            },
                            cancellationToken);

                        int conflicts = result.Items.Count(i => i.Status == SyncItemStatus.SkippedConflict);

                        if (!result.HasErrors)
                        {
                            SaveOutcome(job, ScheduledBackupOutcome.Completed,
                                $"Uploaded {result.Uploaded} run(s), downloaded {result.Downloaded} run(s), skipped {result.Skipped} ({conflicts} conflict(s)).");
                            return ScheduledBackupOutcome.Completed;
                        }

                        SaveOutcome(job, ScheduledBackupOutcome.Failed,
                            result.Warnings.FirstOrDefault(w => w.Severity == TransferWarningSeverity.Error)?.Message ??
                            $"{result.Items.Count(i => i.Status is SyncItemStatus.Failed or SyncItemStatus.Incomplete)} run(s) failed or stopped part way; nothing that existed was changed.");
                        return ScheduledBackupOutcome.Failed;
                    }
                    catch (Exception ex)
                    {
                        return Finish(job, ScheduledBackupOutcome.Failed,
                            $"The run stopped: {ex.Message}", startedUtc);
                    }
                }
            }
        }

        // Next to the database, so every host for this user shares it. Keyed
        // by the profile: two jobs for one profile must not run at once.
        internal string LockPath(Guid profileId) =>
            Path.Combine(
                Path.GetDirectoryName(_databasePath.GetDatabasePath())!,
                $"scheduled-sync-{profileId:N}.lock");

        private async Task<string?> RefusalAsync(SyncRemoteProfile profile, CancellationToken cancellationToken)
        {
            // The local side of every sync. Missing usually means an unplugged
            // or renamed drive; creating it would hide that.
            string backupBase = _backups.GetBackupBasePath();

            if (!Directory.Exists(backupBase))
                return $"The local backup folder is not available: {backupBase}";

            if (ScheduledSyncJob.UnattendedRefusal(profile.ProviderKind) is { } unattended)
                return unattended;

            if (profile.SettingsError is not null)
                return $"The profile's saved settings cannot be used: {profile.SettingsError}";

            if (profile.ProviderKind == SyncProviderKind.LocalFolder &&
                profile.ProviderSettings is not LocalFolderSyncRemoteSettings)
                return "The profile has no local folder path.";

            // A cloud profile without its stored sign-in cannot refresh, and an
            // unattended run never opens a browser to get one.
            if (StoredSecretName(profile.ProviderKind) is { } secretName &&
                !await _secrets.ExistsAsync(new SecretKey(profile.Id, secretName), cancellationToken))
            {
                return $"No stored sign-in for '{profile.DisplayName}'. Open the app, select the profile and sign in again.";
            }

            return null;
        }

        private static string? StoredSecretName(SyncProviderKind kind) => kind switch
        {
            SyncProviderKind.GoogleDrive => SecretNames.OAuthTokenData,
            SyncProviderKind.OneDrive => SecretNames.OneDriveTokenData,
            SyncProviderKind.WebDav => SecretNames.WebDavPassword,
            _ => null
        };

        private ISyncProvider CreateProvider(SyncRemoteProfile profile) => profile.ProviderKind switch
        {
            SyncProviderKind.LocalFolder =>
                _providers.CreateLocalFolderProvider(((LocalFolderSyncRemoteSettings)profile.ProviderSettings!).LocalFolderPath),
            SyncProviderKind.GoogleDrive => _providers.CreateGoogleDriveProvider(profile.Id),
            SyncProviderKind.OneDrive => _providers.CreateOneDriveProvider(profile.Id),
            SyncProviderKind.WebDav => _providers.CreateWebDavProvider(profile.Id),
            _ => throw new NotSupportedException(ScheduledSyncJob.UnattendedRefusal(profile.ProviderKind))
        };

        // Every state but Healthy refuses, a full remote included even for a
        // download: the user runs that case by hand with the report in view.
        private static string HealthLabel(ProviderHealthState state) => state switch
        {
            ProviderHealthState.RateLimited => "The provider is rate limiting requests",
            ProviderHealthState.QuotaExhausted => "The provider reports no free storage space",
            _ => "The provider is unavailable"
        };

        // Records a run that the engine did not record itself.
        private ScheduledBackupOutcome Finish(
            ScheduledSyncJob? job,
            ScheduledBackupOutcome outcome,
            string detail,
            DateTimeOffset startedUtc,
            SyncPlan? plan = null)
        {
            try
            {
                _history.RecordRun(new TransferRunRecord(
                    Kind: TransferRunKind.ScheduledSync,
                    GameName: job?.ProfileName ?? "Unknown scheduled sync",
                    SteamAppId: "-",
                    SourceAccountId: Environment.MachineName,
                    TargetAccountId: job?.ProfileName ?? "",
                    DryRun: false,
                    OverwriteEnabled: false,
                    BackupEnabled: false,
                    FilesConsidered: plan?.Items.Count ?? 0,
                    FilesCopied: 0,
                    FilesSkipped: plan is null ? 0 : plan.InSyncCount + plan.ConflictCount,
                    FilesFailed: 0,
                    BytesCopied: 0,
                    FilesBackedUp: 0,
                    BackupRootPath: null,
                    BlockedReason: outcome == ScheduledBackupOutcome.Completed ? null : detail,
                    StartedUtc: startedUtc,
                    CompletedUtc: _clock.UtcNow,
                    Items: []));
            }
            catch
            {
                // History is the audit trail, not the run; the exit code still reports.
            }

            if (job is not null)
                SaveOutcome(job, outcome, detail);

            return outcome;
        }

        private void SaveOutcome(ScheduledSyncJob job, ScheduledBackupOutcome outcome, string detail)
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
