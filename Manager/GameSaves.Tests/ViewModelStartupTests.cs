using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.Tests;

/// <summary>
/// Startup-initialization regression tests exercised against the real tab
/// ViewModels with in-memory fakes. No real Steam install, SQLite database,
/// backup folder, Google account, SFTP, or network is used.
/// </summary>
public sealed class ViewModelStartupTests
{
    // ----- Installed Games -----

    [Fact]
    public async Task InstalledGames_InitializeAsync_LoadsGamesOnce()
    {
        var service = new FakeInstalledGameStatusService();
        var viewModel = new InstalledGamesViewModel(service, NewWorkspaceLayout());

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Games.Count);          // scenario 18
        Assert.Equal(1, service.CallCount);              // scenario 11 (per-VM)
    }

    [Fact]
    public async Task InstalledGames_InitializeAsync_RunsLoadOnlyOnce()
    {
        var service = new FakeInstalledGameStatusService();
        var viewModel = new InstalledGamesViewModel(service, NewWorkspaceLayout());

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task InstalledGames_ManualRefresh_ReloadsAndPreservesOrder()
    {
        var service = new FakeInstalledGameStatusService();
        var viewModel = new InstalledGamesViewModel(service, NewWorkspaceLayout());

        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);   // scenario 19

        Assert.Equal(2, service.CallCount);
        // Scenario 20: ordering preserved (service order == displayed order).
        Assert.Equal(new[] { "Alpha", "Beta" }, viewModel.Games.Select(g => g.GameName).ToArray());
    }

    // ----- Profiles -----

    [Fact]
    public async Task Profiles_InitializeAsync_LoadsProfilesWithSafeDefaults()
    {
        var viewModel = new ProfilesViewModel(
            new EmptySteamDiscoveryService(),
            new FakeSteamProfileDetector(),
            NewWorkspaceLayout());

        await viewModel.InitializeAsync();               // scenario 23

        Assert.Equal(2, viewModel.Profiles.Count);
        Assert.NotNull(viewModel.SourceProfile);         // scenario 25
        Assert.NotNull(viewModel.TargetProfile);
        // Scenario 26: source and target are never silently the same.
        Assert.NotEqual(
            viewModel.SourceProfile!.AccountId,
            viewModel.TargetProfile!.AccountId);
        Assert.True(viewModel.IsSelectionValid);
    }

    [Fact]
    public async Task Profiles_ManualRefresh_StillWorks()
    {
        var detector = new FakeSteamProfileDetector();
        var viewModel = new ProfilesViewModel(new EmptySteamDiscoveryService(), detector, NewWorkspaceLayout());

        await viewModel.InitializeAsync();
        await viewModel.RefreshProfilesCommand.ExecuteAsync(null);   // scenario 24

        Assert.Equal(2, detector.CallCount);
        Assert.Equal(2, viewModel.Profiles.Count);
    }

    // ----- Transfer Preview -----

    [Fact]
    public async Task TransferPreview_InitializeAsync_LoadsInputsWithoutExecuting()
    {
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), NewWorkspaceLayout());
        var games = new InstalledGamesViewModel(new FakeInstalledGameStatusService(), NewWorkspaceLayout());
        var previewService = new RecordingTransferPreviewService();
        var transferService = new RecordingSaveTransferService();

        var viewModel = new TransferPreviewViewModel(previewService, transferService, profiles, games, NewWorkspaceLayout());

        await profiles.InitializeAsync();
        await games.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.Games);                        // scenario 28
        Assert.NotEmpty(viewModel.Profiles);                     // scenario 29
        Assert.False(previewService.PreviewWasBuilt);            // scenario 30
        Assert.False(transferService.TransferWasExecuted);       // scenario 31
    }

    [Fact]
    public async Task TransferPreview_SharedProfilesAndGames_AreNotRediscovered()
    {
        // Scenario A12: initializing the shared child ViewModels first means the
        // Transfer Preview tab reuses them instead of repeating discovery.
        var detector = new FakeSteamProfileDetector();
        var statusService = new FakeInstalledGameStatusService();
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), detector, NewWorkspaceLayout());
        var games = new InstalledGamesViewModel(statusService, NewWorkspaceLayout());

        var viewModel = new TransferPreviewViewModel(
            new RecordingTransferPreviewService(),
            new RecordingSaveTransferService(),
            profiles,
            games,
            NewWorkspaceLayout());

        await profiles.InitializeAsync();
        await games.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(1, detector.CallCount);        // not re-run by Transfer Preview
        Assert.Equal(1, statusService.CallCount);
    }

    // ----- Manual Backups -----

    [Fact]
    public async Task ManualBackup_InitializeAsync_LoadsInputsWithoutBackingUp()
    {
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), NewWorkspaceLayout());
        var games = new InstalledGamesViewModel(new FakeInstalledGameStatusService(), NewWorkspaceLayout());
        var manualBackup = new RecordingManualBackupService();

        var viewModel = new ManualBackupViewModel(
            manualBackup,
            new FakeBackupHistoryService(),
            new NullFolderPicker(),
            new FakePresetRepository(),
            profiles,
            games,
            NewWorkspaceLayout(),
            new InMemoryScheduledBackupJobRepository(),
            new FakeBackupTaskScheduler(),
            new InMemoryScheduledSyncJobRepository());

        await profiles.InitializeAsync();
        await games.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.Games);                        // scenario 33
        Assert.NotEmpty(viewModel.Profiles);
        Assert.False(manualBackup.BackupWasExecuted);            // scenario 34
    }

    // ----- Scheduled backups (BACKUP-003) -----

    [Fact]
    public async Task ScheduledBackup_IsSavedOnlyFromACleanPreview_AndRemovingItRevokesIt()
    {
        var jobs = new InMemoryScheduledBackupJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(jobs, scheduler);

        // Opt-in starts from a preview: without one, nothing is scheduled.
        await viewModel.SaveScheduledJobCommand.ExecuteAsync(null);
        Assert.Empty(jobs.Jobs);
        Assert.Empty(scheduler.Registered);

        await viewModel.PreviewBackupCommand.ExecuteAsync(null);
        await viewModel.SaveScheduledJobCommand.ExecuteAsync(null);

        ScheduledBackupJob job = Assert.Single(jobs.Jobs);
        Assert.Equal(job.Id, Assert.Single(viewModel.ScheduledJobs).JobId);
        Assert.Equal(viewModel.SelectedGame!.Game.AppId, job.SteamAppId);
        Assert.Equal(viewModel.SelectedProfile!.Profile.AccountId, job.SteamAccountId);
        Assert.Equal(viewModel.DestinationPath, job.DestinationRoot);
        Assert.Equal(job.Id, Assert.Single(scheduler.Registered).JobId);

        await viewModel.RemoveScheduledJobCommand.ExecuteAsync(viewModel.ScheduledJobs[0]);

        Assert.Empty(jobs.Jobs);
        Assert.Empty(viewModel.ScheduledJobs);
        Assert.Equal(job.Id, Assert.Single(scheduler.Removed));
        Assert.Empty(scheduler.Tasks);
    }

    // ----- Windows Task Scheduler (BACKUP-007) -----

    [Fact]
    public async Task ScheduledBackup_RegistersItsTaskWithThePickedSchedule_AndApplyScheduleUpdatesIt()
    {
        var scheduler = new FakeBackupTaskScheduler();
        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(
            new InMemoryScheduledBackupJobRepository(), scheduler);

        viewModel.ScheduleFrequency = BackupScheduleFrequency.Weekly;
        viewModel.ScheduleDay = DayOfWeek.Saturday;
        viewModel.ScheduleTime = new TimeSpan(6, 30, 0);

        await viewModel.PreviewBackupCommand.ExecuteAsync(null);
        await viewModel.SaveScheduledJobCommand.ExecuteAsync(null);

        ScheduledJobRowViewModel row = Assert.Single(viewModel.ScheduledJobs);
        BackupTaskDefinition registered = Assert.Single(scheduler.Registered);

        // The task runs this program with the job's own arguments.
        Assert.Equal(row.JobId, registered.JobId);
        Assert.Equal(viewModel.ScheduledRunProgram, registered.ProgramPath);
        Assert.Equal($"--run-job {row.JobId:D}", registered.Arguments);
        Assert.Equal(
            new BackupSchedule(BackupScheduleFrequency.Weekly, new TimeOnly(6, 30), DayOfWeek.Saturday),
            registered.Schedule);

        Assert.Equal("Every Saturday at 06:30 · Ready", row.ScheduleDisplay);
        Assert.Equal("Not run yet · No next run", row.RunsDisplay);
        Assert.Null(row.Problem);

        viewModel.ScheduleFrequency = BackupScheduleFrequency.Daily;
        viewModel.ScheduleTime = new TimeSpan(21, 5, 0);
        await viewModel.ApplyScheduleCommand.ExecuteAsync(row);

        Assert.Equal(2, scheduler.Registered.Count);
        Assert.Equal(row.JobId, scheduler.Registered[1].JobId);
        Assert.Equal("Daily at 21:05 · Ready", Assert.Single(viewModel.ScheduledJobs).ScheduleDisplay);
        Assert.Contains("Daily at 21:05", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ScheduledBackup_ARefusedRegistration_IsReported_AndChangesNothingElse()
    {
        var jobs = new InMemoryScheduledBackupJobRepository();
        var scheduler = new FakeBackupTaskScheduler
        {
            Refusal = new UnauthorizedAccessException("Access is denied.")
        };
        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(jobs, scheduler);

        await viewModel.PreviewBackupCommand.ExecuteAsync(null);
        await viewModel.SaveScheduledJobCommand.ExecuteAsync(null);

        // The job is kept: it does nothing without a task, and its row says so.
        ScheduledJobRowViewModel row = Assert.Single(viewModel.ScheduledJobs);
        Assert.Single(jobs.Jobs);
        Assert.Contains("did not register its task: Access is denied.", viewModel.StatusMessage);
        Assert.Equal("Not scheduled", row.ScheduleDisplay);
        Assert.Contains("No Windows task", row.Problem);

        // Once Windows accepts, a later refused update leaves the task as it was.
        scheduler.Refusal = null;
        await viewModel.ApplyScheduleCommand.ExecuteAsync(row);
        Assert.Equal("Daily at 20:00 · Ready", viewModel.ScheduledJobs[0].ScheduleDisplay);

        scheduler.Refusal = new UnauthorizedAccessException("Access is denied.");
        viewModel.ScheduleFrequency = BackupScheduleFrequency.Weekly;
        await viewModel.ApplyScheduleCommand.ExecuteAsync(viewModel.ScheduledJobs[0]);

        Assert.Contains("did not update the task", viewModel.StatusMessage);
        Assert.Single(jobs.Jobs);
        ScheduledJobRowViewModel kept = Assert.Single(viewModel.ScheduledJobs);
        Assert.Equal(row.JobId, kept.JobId);
        Assert.Equal("Daily at 20:00 · Ready", kept.ScheduleDisplay);
        Assert.Null(kept.Problem);
    }

    [Fact]
    public async Task ScheduledBackup_RemovingAJob_RemovesItsTaskFirst_AndKeepsTheJobWhenWindowsRefuses()
    {
        var jobs = new InMemoryScheduledBackupJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(jobs, scheduler);

        await viewModel.PreviewBackupCommand.ExecuteAsync(null);
        await viewModel.SaveScheduledJobCommand.ExecuteAsync(null);
        Guid jobId = Assert.Single(jobs.Jobs).Id;

        scheduler.Refusal = new UnauthorizedAccessException("Access is denied.");
        await viewModel.RemoveScheduledJobCommand.ExecuteAsync(viewModel.ScheduledJobs[0]);

        // A job is never removed while its task could still run.
        Assert.Equal(jobId, Assert.Single(jobs.Jobs).Id);
        Assert.Equal(jobId, Assert.Single(viewModel.ScheduledJobs).JobId);
        Assert.Contains("did not remove the task", viewModel.StatusMessage);

        scheduler.Refusal = null;
        await viewModel.RemoveScheduledJobCommand.ExecuteAsync(viewModel.ScheduledJobs[0]);

        Assert.Empty(jobs.Jobs);
        Assert.Empty(scheduler.Tasks);
        Assert.Empty(viewModel.ScheduledJobs);
        Assert.Contains("Backups it already made are kept", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ScheduledBackup_DriftIsShown_ButLoadingNeverChangesATask()
    {
        var jobs = new InMemoryScheduledBackupJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        string program = Environment.ProcessPath!;

        ScheduledBackupJob NewJob(string game) => new(
            Guid.NewGuid(), "10", game, "1", @"D:\Backups", true, true, DateTimeOffset.UtcNow);

        BackupTaskStatus TaskFor(Guid id, string? path, string arguments) => new(
            id, BackupTaskState.Ready, path, arguments,
            new BackupSchedule(BackupScheduleFrequency.Daily, new TimeOnly(20, 0)),
            LastRun: new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero),
            LastResult: 2,
            NextRun: new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));

        ScheduledBackupJob unscheduled = NewJob("No task");
        ScheduledBackupJob moved = NewJob("Old program");
        ScheduledBackupJob edited = NewJob("Edited arguments");
        ScheduledBackupJob healthy = NewJob("Healthy");
        jobs.Jobs.AddRange([unscheduled, moved, edited, healthy]);

        var leftoverId = Guid.NewGuid();
        scheduler.Tasks[moved.Id] = TaskFor(moved.Id, @"C:\Old\GameSaves.App.exe", $"--run-job {moved.Id:D}");
        scheduler.Tasks[edited.Id] = TaskFor(edited.Id, program, "--run-job something-else");
        scheduler.Tasks[healthy.Id] = TaskFor(healthy.Id, program, $"--run-job {healthy.Id:D}");
        scheduler.Tasks[leftoverId] = TaskFor(leftoverId, program, $"--run-job {leftoverId:D}");

        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(jobs, scheduler);
        ScheduledJobRowViewModel Row(ScheduledBackupJob job) =>
            viewModel.ScheduledJobs.Single(row => row.JobId == job.Id);

        Assert.Contains("No Windows task", Row(unscheduled).Problem);
        Assert.Contains(@"starts C:\Old\GameSaves.App.exe, not this app", Row(moved).Problem);
        Assert.Contains("arguments no longer match", Row(edited).Problem);
        Assert.Null(Row(healthy).Problem);
        Assert.Contains("Refused before anything was written", Row(healthy).RunsDisplay);
        Assert.Contains("Next run", Row(healthy).RunsDisplay);
        Assert.Equal(leftoverId, Assert.Single(viewModel.LeftoverTasks).JobId);

        // Reading is not repairing: nothing was registered or removed.
        Assert.Empty(scheduler.Registered);
        Assert.Empty(scheduler.Removed);

        await viewModel.RemoveLeftoverTaskCommand.ExecuteAsync(viewModel.LeftoverTasks[0]);

        Assert.Equal(leftoverId, Assert.Single(scheduler.Removed));
        Assert.Empty(viewModel.LeftoverTasks);
        Assert.Equal(4, jobs.Jobs.Count);
    }

    [Fact]
    public async Task ScheduledBackup_AnUnreadableScheduler_IsReported_NotMistakenForDrift()
    {
        var jobs = new InMemoryScheduledBackupJobRepository();
        jobs.Jobs.Add(new ScheduledBackupJob(
            Guid.NewGuid(), "10", "Game", "1", @"D:\Backups", true, true, DateTimeOffset.UtcNow));
        var scheduler = new FakeBackupTaskScheduler
        {
            ReadFailure = new InvalidOperationException("The Task Scheduler service is not running.")
        };

        ManualBackupViewModel viewModel = await NewScheduledBackupViewModelAsync(jobs, scheduler);

        ScheduledJobRowViewModel row = Assert.Single(viewModel.ScheduledJobs);
        Assert.Equal(
            "The Windows task could not be read: The Task Scheduler service is not running.",
            row.ScheduleDisplay);
        Assert.Null(row.Problem);
        Assert.Empty(viewModel.LeftoverTasks);
    }

    internal static async Task<ManualBackupViewModel> NewScheduledBackupViewModelAsync(
        InMemoryScheduledBackupJobRepository jobs,
        FakeBackupTaskScheduler scheduler,
        InMemoryScheduledSyncJobRepository? syncJobs = null)
    {
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), NewWorkspaceLayout());
        var games = new InstalledGamesViewModel(new FakeInstalledGameStatusService(), NewWorkspaceLayout());

        var viewModel = new ManualBackupViewModel(
            new CleanPreviewManualBackupService(),
            new FakeBackupHistoryService(),
            new NullFolderPicker(),
            new FakePresetRepository(),
            profiles,
            games,
            NewWorkspaceLayout(),
            jobs,
            scheduler,
            syncJobs ?? new InMemoryScheduledSyncJobRepository());

        await profiles.InitializeAsync();
        await games.InitializeAsync();
        await viewModel.InitializeAsync();

        return viewModel;
    }

    // ----- Backups -----

    [Fact]
    public async Task Backups_InitializeAsync_LoadsRunsWithoutRestoringOrDeleting()
    {
        var history = new FakeBackupHistoryService();
        var restore = new RecordingBackupRestoreService();
        var cleanup = new RecordingBackupCleanupService();
        var archive = new RecordingBackupArchiveService();

        var viewModel = new BackupHistoryViewModel(
            history,
            restore,
            cleanup,
            archive,
            new NullFolderPicker(),
            new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), NewWorkspaceLayout()),
            NewWorkspaceLayout());

        await viewModel.InitializeAsync();

        Assert.True(history.GetRunsWasCalled);           // scenario 36
        Assert.False(restore.RestoreWasCalled);          // scenario 38
        Assert.False(cleanup.CleanupWasCalled);          // scenario 39
        Assert.False(archive.ExportWasCalled);
    }

    // ----- History -----

    [Fact]
    public async Task History_InitializeAsync_LoadsRuns()
    {
        // Scenario 40: initialization runs the same load path as manual Refresh.
        // The default status ("Refresh to list executed runs.") is replaced by
        // the loaded status, proving the load executed during initialization.
        var viewModel = new TransferHistoryViewModel(new RecordingHistoryRepository(), NewWorkspaceLayout());

        await viewModel.InitializeAsync();

        Assert.Contains("No executed runs recorded yet", viewModel.StatusMessage);
    }

    [Fact]
    public async Task History_InitializeAsync_FailureDoesNotThrow()
    {
        // Scenario 42: a history-load failure is surfaced as status text, never
        // thrown out of initialization.
        var viewModel = new TransferHistoryViewModel(new ThrowingHistoryRepository(), NewWorkspaceLayout());

        await viewModel.InitializeAsync();

        Assert.Contains("Failed to read run history", viewModel.StatusMessage);
    }

    [Fact]
    public async Task History_ManualRefresh_StillWorks()
    {
        var repository = new RecordingHistoryRepository();
        var viewModel = new TransferHistoryViewModel(repository, NewWorkspaceLayout());

        await viewModel.InitializeAsync();
        await viewModel.RefreshRunsCommand.ExecuteAsync(null);   // scenario 41

        Assert.Contains("No executed runs recorded yet", viewModel.StatusMessage);
    }

    // ===== Fakes =====

    // Every tab ViewModel owns a workspace page. Layout is irrelevant to these
    // startup tests, so each gets a service over a throwaway in-memory store.
    private static WorkspaceLayoutService NewWorkspaceLayout() =>
        new(new InMemoryUiSettingsStore());

    private sealed class InMemoryUiSettingsStore : IUiSettingsStore
    {
        private AppUiSettings _settings = AppUiSettings.Default;

        public string FilePath => "memory://ui-settings.json";

        public AppUiSettings Load() => _settings;

        public void Save(AppUiSettings settings) => _settings = settings;
    }

    private sealed class FakeInstalledGameStatusService : IInstalledGameSaveStatusService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            IReadOnlyList<InstalledGameSaveStatus> result = new[]
            {
                CreateStatus("Alpha", "1"),
                CreateStatus("Beta", "2")
            };
            return Task.FromResult(result);
        }

        private static InstalledGameSaveStatus CreateStatus(string name, string appId) =>
            new(
                new SteamGame(appId, name, name, "", "", "", true, SteamDiscoveryConfidence.High),
                GameSaveStatusKind.Ready,
                "Ready",
                ApprovedMappings: 1,
                PendingMappings: 0,
                NeedsFixMappings: 0,
                SavePathExists: true,
                FileCount: 3,
                TotalBytes: 100,
                VerificationResults: Array.Empty<SavePathVerificationResult>(),
                Error: null);
    }

    private sealed class FakeSteamProfileDetector : ISteamProfileDetector
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<SteamProfile> DetectProfiles(
            SteamDiscoveryResult discovery,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new[]
            {
                new SteamProfile("111", null, "First", "first-root", 5, false),
                new SteamProfile("222", null, "Second", "second-root", 3, false)
            };
        }

        public IReadOnlyList<SteamProfile> DetectProfiles(
            string steamRoot,
            CancellationToken cancellationToken = default) =>
            DetectProfiles(new SteamDiscoveryResult(), cancellationToken);
    }

    private sealed class RecordingTransferPreviewService : ITransferPreviewService
    {
        public bool PreviewWasBuilt { get; private set; }

        public Task<TransferPreviewPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            PreviewWasBuilt = true;
            throw new InvalidOperationException("Preview must not be built during startup.");
        }
    }

    private sealed class RecordingSaveTransferService : ISaveTransferService
    {
        public bool TransferWasExecuted { get; private set; }

        public Task<SaveTransferResult> ExecuteAsync(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken = default)
        {
            TransferWasExecuted = true;
            throw new InvalidOperationException("Transfer must not execute during startup.");
        }
    }

    private sealed class RecordingManualBackupService : IManualBackupService
    {
        public bool BackupWasExecuted { get; private set; }

        public Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not be built during startup.");

        public Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default)
        {
            BackupWasExecuted = true;
            throw new InvalidOperationException("Backup must not execute during startup.");
        }
    }

    private sealed class CleanPreviewManualBackupService : IManualBackupService
    {
        public Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManualBackupPlan(
                game, profile, destinationRoot, [], [], CanExecute: true, TotalFiles: 1, TotalBytes: 1));

        public Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Saving a schedule must not run a backup.");
    }

    private sealed class FakeBackupHistoryService : IBackupHistoryService
    {
        public bool GetRunsWasCalled { get; private set; }

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            GetRunsWasCalled = true;
            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
                Array.Empty<TransferBackupRunInfo>());
        }

        public string GetBackupBasePath() => System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gamesave-tests-backups");
    }

    private sealed class FakePresetRepository : IManualBackupPresetRepository
    {
        public IReadOnlyList<ManualBackupPreset> GetAll() => Array.Empty<ManualBackupPreset>();

        public ManualBackupPreset Save(ManualBackupPreset preset) =>
            throw new InvalidOperationException("Save must not run during startup.");

        public void Delete(long id) =>
            throw new InvalidOperationException("Delete must not run during startup.");

        public void MarkUsed(long id) =>
            throw new InvalidOperationException("MarkUsed must not run during startup.");
    }

    private sealed class RecordingBackupRestoreService : IBackupRestoreService
    {
        public bool RestoreWasCalled { get; private set; }

        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default)
        {
            RestoreWasCalled = true;
            throw new InvalidOperationException("Restore must not run during startup.");
        }

        public Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RestoreMappingTargetOption>>(
                Array.Empty<RestoreMappingTargetOption>());
    }

    private sealed class RecordingBackupCleanupService : IBackupCleanupService
    {
        public bool CleanupWasCalled { get; private set; }

        public Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default)
        {
            CleanupWasCalled = true;
            throw new InvalidOperationException("Cleanup must not run during startup.");
        }

        public Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default)
        {
            CleanupWasCalled = true;
            throw new InvalidOperationException("Delete must not run during startup.");
        }
    }

    private sealed class RecordingBackupArchiveService : IBackupArchiveService
    {
        public bool ExportWasCalled { get; private set; }

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken)
        {
            ExportWasCalled = true;
            throw new InvalidOperationException("Export must not run during startup.");
        }

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            BackupContainerFormat format = BackupContainerFormat.Zip,
            BackupCompressionPreset preset = BackupCompressionPreset.Optimal,
            CancellationToken cancellationToken = default)
        {
            ExportWasCalled = true;
            throw new InvalidOperationException("Export must not run during startup.");
        }

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Import must not run during startup.");
    }

    private sealed class NullFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startLocation = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) =>
            Task.FromResult<string?>(null);
    }
}
