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
        var viewModel = new InstalledGamesViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Games.Count);          // scenario 18
        Assert.Equal(1, service.CallCount);              // scenario 11 (per-VM)
    }

    [Fact]
    public async Task InstalledGames_InitializeAsync_RunsLoadOnlyOnce()
    {
        var service = new FakeInstalledGameStatusService();
        var viewModel = new InstalledGamesViewModel(service);

        await viewModel.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task InstalledGames_ManualRefresh_ReloadsAndPreservesOrder()
    {
        var service = new FakeInstalledGameStatusService();
        var viewModel = new InstalledGamesViewModel(service);

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
            new FakeSteamProfileDetector());

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
        var viewModel = new ProfilesViewModel(new EmptySteamDiscoveryService(), detector);

        await viewModel.InitializeAsync();
        await viewModel.RefreshProfilesCommand.ExecuteAsync(null);   // scenario 24

        Assert.Equal(2, detector.CallCount);
        Assert.Equal(2, viewModel.Profiles.Count);
    }

    // ----- Transfer Preview -----

    [Fact]
    public async Task TransferPreview_InitializeAsync_LoadsInputsWithoutExecuting()
    {
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector());
        var games = new InstalledGamesViewModel(new FakeInstalledGameStatusService());
        var previewService = new RecordingTransferPreviewService();
        var transferService = new RecordingSaveTransferService();

        var viewModel = new TransferPreviewViewModel(previewService, transferService, profiles, games);

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
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), detector);
        var games = new InstalledGamesViewModel(statusService);

        var viewModel = new TransferPreviewViewModel(
            new RecordingTransferPreviewService(),
            new RecordingSaveTransferService(),
            profiles,
            games);

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
        var profiles = new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector());
        var games = new InstalledGamesViewModel(new FakeInstalledGameStatusService());
        var manualBackup = new RecordingManualBackupService();

        var viewModel = new ManualBackupViewModel(
            manualBackup,
            new FakeBackupHistoryService(),
            new NullFolderPicker(),
            new FakePresetRepository(),
            profiles,
            games);

        await profiles.InitializeAsync();
        await games.InitializeAsync();
        await viewModel.InitializeAsync();

        Assert.NotEmpty(viewModel.Games);                        // scenario 33
        Assert.NotEmpty(viewModel.Profiles);
        Assert.False(manualBackup.BackupWasExecuted);            // scenario 34
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
            new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector()));

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
        var viewModel = new TransferHistoryViewModel(new RecordingHistoryRepository());

        await viewModel.InitializeAsync();

        Assert.Contains("No executed runs recorded yet", viewModel.StatusMessage);
    }

    [Fact]
    public async Task History_InitializeAsync_FailureDoesNotThrow()
    {
        // Scenario 42: a history-load failure is surfaced as status text, never
        // thrown out of initialization.
        var viewModel = new TransferHistoryViewModel(new ThrowingHistoryRepository());

        await viewModel.InitializeAsync();

        Assert.Contains("Failed to read run history", viewModel.StatusMessage);
    }

    [Fact]
    public async Task History_ManualRefresh_StillWorks()
    {
        var repository = new RecordingHistoryRepository();
        var viewModel = new TransferHistoryViewModel(repository);

        await viewModel.InitializeAsync();
        await viewModel.RefreshRunsCommand.ExecuteAsync(null);   // scenario 41

        Assert.Contains("No executed runs recorded yet", viewModel.StatusMessage);
    }

    // ===== Fakes =====

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
