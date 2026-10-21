using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// BACKUP-008 at the Sync page: scheduling is opt-in from a clean preview of
/// a saved profile, goes through the fake scheduler, and never touches a
/// real task or provider.
/// </summary>
public sealed class ScheduledSyncViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("5d6e7f80-1111-4a2b-9c3d-0e1f2a3b4c5d");

    [Fact]
    public async Task AProfile_IsScheduledOnlyFromACleanPreview_AndOnlyOnce()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        SyncViewModel viewModel = CreateViewModel(jobs, scheduler);

        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);

        Assert.Contains("Preview the sync first", viewModel.StatusMessage);
        Assert.Empty(jobs.Jobs);
        Assert.Empty(scheduler.Registered);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        Assert.True(viewModel.CanExecuteSync);

        viewModel.ScheduleFrequency = BackupScheduleFrequency.Weekly;
        viewModel.ScheduleDay = DayOfWeek.Sunday;
        viewModel.ScheduleTime = new TimeSpan(4, 30, 0);
        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);

        ScheduledSyncJob job = Assert.Single(jobs.Jobs);
        Assert.Equal(ProfileId, job.ProfileId);
        Assert.Equal("Home NAS", job.ProfileName);
        Assert.True(job.Upload);
        Assert.True(job.Download);
        Assert.False(job.ArchiveSync);

        BackupTaskDefinition registered = Assert.Single(scheduler.Registered);
        Assert.Equal(job.Id, registered.JobId);
        Assert.Equal(viewModel.ScheduledRunProgram, registered.ProgramPath);
        Assert.Equal($"--run-sync {job.Id:D}", registered.Arguments);
        Assert.Equal(
            new BackupSchedule(BackupScheduleFrequency.Weekly, new TimeOnly(4, 30), DayOfWeek.Sunday),
            registered.Schedule);
        Assert.Contains("including runs made later", viewModel.StatusMessage);

        ScheduledJobRowViewModel row = Assert.Single(viewModel.ScheduledSyncJobs);
        Assert.Equal("Home NAS", row.Title);
        Assert.Equal("Local or mounted folder · Upload and download", row.Subtitle);
        Assert.Equal("Every Sunday at 04:30 · Ready", row.ScheduleDisplay);
        Assert.Null(row.Problem);

        // One schedule per profile.
        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);
        Assert.Contains("already has a scheduled sync", viewModel.StatusMessage);
        Assert.Single(jobs.Jobs);
        Assert.Single(scheduler.Registered);
    }

    [Fact]
    public async Task ChangingTheDirectionAfterThePreview_InvalidatesIt_SoNothingCanBeScheduled()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        SyncViewModel viewModel = CreateViewModel(jobs, new FakeBackupTaskScheduler());

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        viewModel.DownloadEnabled = false;

        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);

        Assert.Contains("Preview the sync first", viewModel.StatusMessage);
        Assert.Empty(jobs.Jobs);
    }

    [Fact]
    public async Task AnSftpProfile_CannotBeScheduled_AndTheReasonIsShown()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        SyncViewModel viewModel = CreateViewModel(jobs, scheduler, new SyncRemoteProfile(
            ProfileId, "Home NAS", SyncProviderKind.Sftp, null, null,
            new SftpSyncRemoteSettings("nas", 22, "me", SftpAuthMethod.Password, null, "/backups"),
            Now, Now, null, null, null));

        viewModel.SftpPassword = "session-only";
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        Assert.True(viewModel.CanExecuteSync);

        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);

        Assert.Contains("never stored", viewModel.StatusMessage);
        Assert.Empty(jobs.Jobs);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task ARefusedRegistration_KeepsTheJob_AndApplyScheduleTriesAgain()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        var scheduler = new FakeBackupTaskScheduler
        {
            Refusal = new UnauthorizedAccessException("Access is denied.")
        };
        SyncViewModel viewModel = CreateViewModel(jobs, scheduler);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);

        Assert.Contains("did not register its task", viewModel.StatusMessage);
        Assert.Single(jobs.Jobs);
        ScheduledJobRowViewModel row = Assert.Single(viewModel.ScheduledSyncJobs);
        Assert.Contains("No Windows task", row.Problem);

        scheduler.Refusal = null;
        await viewModel.ApplySyncScheduleCommand.ExecuteAsync(row);

        Assert.Equal(row.JobId, Assert.Single(scheduler.Registered).JobId);
        Assert.Null(Assert.Single(viewModel.ScheduledSyncJobs).Problem);
        Assert.Contains("now runs while you are signed in", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Remove_DeletesTheTaskFirst_AndKeepsTheJobWhenWindowsRefuses()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        var scheduler = new FakeBackupTaskScheduler();
        SyncViewModel viewModel = CreateViewModel(jobs, scheduler);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);
        Guid jobId = Assert.Single(jobs.Jobs).Id;

        scheduler.Refusal = new InvalidOperationException("The task is running.");
        await viewModel.RemoveScheduledSyncCommand.ExecuteAsync(viewModel.ScheduledSyncJobs[0]);

        Assert.Equal(jobId, Assert.Single(jobs.Jobs).Id);
        Assert.Contains("did not remove the task", viewModel.StatusMessage);

        scheduler.Refusal = null;
        await viewModel.RemoveScheduledSyncCommand.ExecuteAsync(viewModel.ScheduledSyncJobs[0]);

        Assert.Equal(jobId, Assert.Single(scheduler.Removed));
        Assert.Empty(jobs.Jobs);
        Assert.Empty(viewModel.ScheduledSyncJobs);
        Assert.Contains("Backup runs on both sides are kept", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AProfileWithAScheduledSync_CannotBeDeleted_UntilTheScheduleIsRemoved()
    {
        var jobs = new InMemoryScheduledSyncJobRepository();
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncViewModel viewModel = CreateViewModel(jobs, new FakeBackupTaskScheduler(), repository: repository);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        await viewModel.ScheduleSyncCommand.ExecuteAsync(null);
        Assert.Single(jobs.Jobs);

        viewModel.ConfirmDeleteRemoteProfile = true;
        await viewModel.DeleteRemoteProfileCommand.ExecuteAsync(null);

        Assert.Contains("Remove it in the Scheduled syncs panel first", viewModel.StatusMessage);
        Assert.NotNull(repository.GetById(ProfileId));
    }

    [Fact]
    public async Task SyncAndBackupTasks_ShareTheFolder_WithoutShowingAsEachOthersLeftovers()
    {
        string program = Environment.ProcessPath!;
        var syncJobs = new InMemoryScheduledSyncJobRepository();
        var backupJobs = new InMemoryScheduledBackupJobRepository();
        var scheduler = new FakeBackupTaskScheduler();

        var syncJob = new ScheduledSyncJob(Guid.NewGuid(), ProfileId, "Home NAS", true, true, false, Now);
        var backupJob = new ScheduledBackupJob(Guid.NewGuid(), "10", "Game", "1", @"D:\Backups", true, true, Now);
        var editedSync = new ScheduledSyncJob(Guid.NewGuid(), Guid.NewGuid(), "Old profile", true, false, false, Now);
        syncJobs.Jobs.AddRange([syncJob, editedSync]);
        backupJobs.Jobs.Add(backupJob);

        BackupTaskStatus TaskFor(Guid id, string arguments) => new(
            id, BackupTaskState.Ready, program, arguments,
            new BackupSchedule(BackupScheduleFrequency.Daily, new TimeOnly(21, 0)),
            LastRun: null, LastResult: 0x41303, NextRun: null);

        var orphanId = Guid.NewGuid();
        scheduler.Tasks[syncJob.Id] = TaskFor(syncJob.Id, $"--run-sync {syncJob.Id:D}");
        scheduler.Tasks[backupJob.Id] = TaskFor(backupJob.Id, $"--run-job {backupJob.Id:D}");
        // A sync task edited to run as a backup is drift, not a leftover.
        scheduler.Tasks[editedSync.Id] = TaskFor(editedSync.Id, $"--run-job {editedSync.Id:D}");
        scheduler.Tasks[orphanId] = TaskFor(orphanId, $"--run-sync {orphanId:D}");

        ManualBackupViewModel backups = await ViewModelStartupTests.NewScheduledBackupViewModelAsync(backupJobs, scheduler, syncJobs);
        Assert.Equal(orphanId, Assert.Single(backups.LeftoverTasks).JobId);
        Assert.Null(Assert.Single(backups.ScheduledJobs).Problem);

        SyncViewModel sync = CreateViewModel(syncJobs, scheduler);
        await sync.RefreshScheduledSyncsCommand.ExecuteAsync(null);

        Assert.Equal(2, sync.ScheduledSyncJobs.Count);
        Assert.Null(sync.ScheduledSyncJobs.Single(row => row.JobId == syncJob.Id).Problem);
        ScheduledJobRowViewModel edited = sync.ScheduledSyncJobs.Single(row => row.JobId == editedSync.Id);
        Assert.Contains("arguments no longer match", edited.Problem);
        Assert.Contains("no longer exists", edited.Subtitle);

        // Reading is not repairing.
        Assert.Empty(scheduler.Registered);
        Assert.Empty(scheduler.Removed);
    }

    [Fact]
    public void WithoutTheSchedulingServices_ThePageSaysSo_AndNothingIsRegistered()
    {
        SyncViewModel viewModel = CreateViewModel(null, null);

        Assert.False(viewModel.IsSchedulingAvailable);
    }

    // ---- helpers ----

    private static SyncViewModel CreateViewModel(
        InMemoryScheduledSyncJobRepository? jobs,
        FakeBackupTaskScheduler? scheduler,
        SyncRemoteProfile? profile = null,
        InMemorySyncRemoteProfileRepository? repository = null)
    {
        repository ??= new InMemorySyncRemoteProfileRepository();
        profile ??= new SyncRemoteProfile(
            ProfileId, "Home NAS", SyncProviderKind.LocalFolder, null, null,
            new LocalFolderSyncRemoteSettings(@"D:\MountedBackups"),
            Now, Now, null, null, null);
        repository.Create(profile);

        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = profile.ProviderKind,
            SelectedRemoteProfileId = profile.Id
        };

        return new SyncViewModel(
            new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(Now),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout(),
            scheduledSyncJobs: jobs,
            taskScheduler: scheduler);
    }
}
