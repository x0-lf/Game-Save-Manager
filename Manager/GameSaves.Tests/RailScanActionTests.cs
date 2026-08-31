using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using System.Windows.Input;
using Xunit;

namespace GameSaves.Tests;

// The navigation rail's Scan/Refresh action is one button that belongs to
// whichever page is showing. These tests pin the whole mapping: which command
// each page contributes, the wording it carries, the pages that contribute
// nothing, and what happens while a refresh is in flight.
//
// The rail deliberately runs the page's own command object rather than a copy
// of its work, so most of the behaviour below is a consequence of that choice
// rather than of new code: identity of the command is what these tests check.
public sealed class RailScanActionTests
{
    [Theory]
    [InlineData(UiRailLayoutSettings.TabDashboard)]
    [InlineData(UiRailLayoutSettings.TabInstalledGames)]
    [InlineData(UiRailLayoutSettings.TabProfiles)]
    [InlineData(UiRailLayoutSettings.TabTransferPreview)]
    [InlineData(UiRailLayoutSettings.TabManualBackup)]
    [InlineData(UiRailLayoutSettings.TabBackups)]
    [InlineData(UiRailLayoutSettings.TabSync)]
    [InlineData(UiRailLayoutSettings.TabHistory)]
    public void EveryPage_RoutesTheRailActionToThatPagesOwnRefreshCommand(string tabKey)
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.ActiveTabKey = tabKey;

        ICommand expected = tabKey switch
        {
            UiRailLayoutSettings.TabDashboard => viewModel.RefreshCommand,
            UiRailLayoutSettings.TabInstalledGames => viewModel.InstalledGames.RefreshCommand,
            UiRailLayoutSettings.TabProfiles => viewModel.Profiles.RefreshProfilesCommand,
            UiRailLayoutSettings.TabTransferPreview =>
                viewModel.TransferPreview.RefreshInputsCommand,
            UiRailLayoutSettings.TabManualBackup =>
                viewModel.ManualBackup.RefreshInputsCommand,
            UiRailLayoutSettings.TabBackups => viewModel.BackupHistory.RefreshRunsCommand,
            UiRailLayoutSettings.TabSync => viewModel.Sync.CheckSyncStatusCommand,
            _ => viewModel.TransferHistory.RefreshRunsCommand,
        };

        // Same instance, not merely an equivalent one: that is what puts the
        // rail and the page's own Refresh button on a single code path, so
        // loading state, status text and availability cannot diverge.
        Assert.Same(expected, viewModel.RailScanCommand);
    }

    [Fact]
    public void TheSettingsPage_OffersNoRailAction()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabSettings;

        Assert.Null(viewModel.RailScanCommand);

        // Hidden rather than disabled: a disabled button would leave an
        // unexplained affordance on a page that has nothing to refresh.
        Assert.False(viewModel.IsRailScanVisible);
    }

    [Fact]
    public void EveryPageWithARailAction_CarriesTheWordingThatPageIsSpecifiedToShow()
    {
        (string TabKey, string Label, string Description)[] expected =
        {
            (UiRailLayoutSettings.TabDashboard, "Scan",
                "Scan Steam library and refresh Dashboard"),
            (UiRailLayoutSettings.TabInstalledGames, "Scan", "Scan installed games"),
            (UiRailLayoutSettings.TabProfiles, "Refresh", "Refresh profiles"),
            (UiRailLayoutSettings.TabTransferPreview, "Refresh",
                "Refresh Transfer Profiles"),
            (UiRailLayoutSettings.TabManualBackup, "Refresh", "Refresh Manual Backup"),
            (UiRailLayoutSettings.TabBackups, "Refresh", "Refresh backups"),
            (UiRailLayoutSettings.TabSync, "Refresh", "Refresh Sync status"),
            (UiRailLayoutSettings.TabHistory, "Refresh", "Refresh history"),
        };

        Assert.Equal(
            expected.Select(row => row.TabKey),
            MainWindowViewModel.RailScanActions.Select(action => action.TabKey));

        foreach ((string tabKey, string label, string description) in expected)
        {
            MainWindowViewModel.RailScanAction action = Assert.Single(
                MainWindowViewModel.RailScanActions,
                candidate => candidate.TabKey == tabKey);

            Assert.Equal(label, action.Label);
            Assert.Equal(description, action.Description);
        }

        Assert.DoesNotContain(
            MainWindowViewModel.RailScanActions,
            action => action.TabKey == UiRailLayoutSettings.TabSettings);
    }

    [Fact]
    public void NavigatingToAnotherPage_ChangesTheCommandAndTheWordingImmediately()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        viewModel.ActiveTabKey = UiRailLayoutSettings.TabHistory;

        Assert.Same(viewModel.TransferHistory.RefreshRunsCommand, viewModel.RailScanCommand);
        Assert.Equal("Refresh", viewModel.RailScanLabel);
        Assert.Equal("Refresh history", viewModel.RailScanDescription);

        // The button rebinds only if the change is announced, so the rail must
        // not need a second navigation to catch up.
        Assert.Contains(nameof(MainWindowViewModel.RailScanCommand), raised);
        Assert.Contains(nameof(MainWindowViewModel.RailScanLabel), raised);
        Assert.Contains(nameof(MainWindowViewModel.RailScanDescription), raised);
        Assert.Contains(nameof(MainWindowViewModel.IsRailScanVisible), raised);
    }

    [Fact]
    public void TheDashboardIsTheStartingPage_SoTheRailActionIsUsableBeforeAnyNavigation()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        Assert.Equal(UiRailLayoutSettings.TabDashboard, viewModel.ActiveTabKey);
        Assert.Same(viewModel.RefreshCommand, viewModel.RailScanCommand);
        Assert.True(viewModel.IsRailScanVisible);
    }

    [Fact]
    public void SwitchingTheActionOffInSettings_HidesItOnAPageThatHasOne()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabInstalledGames;
        Assert.True(viewModel.IsRailScanVisible);

        viewModel.Settings.ShowScanInNavigationRail = false;
        Assert.False(viewModel.IsRailScanVisible);

        viewModel.Settings.ShowScanInNavigationRail = true;
        Assert.True(viewModel.IsRailScanVisible);
    }

    [Fact]
    public async Task WhileARefreshIsRunning_TheRailActionCannotStartASecond()
    {
        var status = new GatedInstalledGameStatusService();
        MainWindowViewModel viewModel = CreateViewModel(status);
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabInstalledGames;

        ICommand rail = Assert.IsAssignableFrom<ICommand>(viewModel.RailScanCommand);
        Assert.True(rail.CanExecute(null));

        rail.Execute(null);
        await status.Started;

        // The button binds the command, so an unavailable command is a
        // disabled button: a second press cannot reach the service.
        Assert.False(rail.CanExecute(null));

        status.Release();
        await viewModel.InstalledGames.RefreshCommand.ExecutionTask!;

        Assert.True(rail.CanExecute(null));
        Assert.Equal(1, status.Calls);
    }

    [Fact]
    public async Task NavigatingAwayMidRefresh_LeavesTheRunningPageToFinishItsOwnWork()
    {
        var status = new GatedInstalledGameStatusService();
        MainWindowViewModel viewModel = CreateViewModel(status);
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabInstalledGames;

        viewModel.RailScanCommand!.Execute(null);
        await status.Started;

        viewModel.ActiveTabKey = UiRailLayoutSettings.TabHistory;

        // The rail now points somewhere else, but the operation belongs to the
        // page that started it and is still running there.
        Assert.Same(viewModel.TransferHistory.RefreshRunsCommand, viewModel.RailScanCommand);
        Assert.True(viewModel.InstalledGames.IsLoading);

        status.Release();
        await viewModel.InstalledGames.RefreshCommand.ExecutionTask!;

        Assert.False(viewModel.InstalledGames.IsLoading);
        Assert.Equal(1, status.Calls);
    }

    [Fact]
    public async Task AFailedRefresh_ReportsThroughThePageAndLeavesTheActionUsable()
    {
        MainWindowViewModel viewModel = CreateViewModel(new ThrowingInstalledGameStatusService());
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabInstalledGames;

        viewModel.RailScanCommand!.Execute(null);
        await viewModel.InstalledGames.RefreshCommand.ExecutionTask!;

        // The page's own status line carries the failure; the rail adds no
        // error surface of its own.
        Assert.Contains("Failed to load installed games", viewModel.InstalledGames.StatusMessage);
        Assert.False(viewModel.InstalledGames.IsLoading);
        Assert.True(viewModel.RailScanCommand!.CanExecute(null));
    }

    [Fact]
    public async Task OnManualBackup_TheRailRefreshKeepsASelectionThatIsStillValid()
    {
        MainWindowViewModel viewModel = CreateViewModel(new OneGameStatusService());
        viewModel.ActiveTabKey = UiRailLayoutSettings.TabManualBackup;

        viewModel.RailScanCommand!.Execute(null);
        await viewModel.ManualBackup.RefreshInputsCommand.ExecutionTask!;

        InstalledGameRowViewModel? chosen = viewModel.ManualBackup.Games.FirstOrDefault();
        Assert.NotNull(chosen);
        viewModel.ManualBackup.SelectedGame = chosen;

        viewModel.RailScanCommand!.Execute(null);
        await viewModel.ManualBackup.RefreshInputsCommand.ExecutionTask!;

        Assert.Same(chosen, viewModel.ManualBackup.SelectedGame);
    }

    [Fact]
    public void EveryRailPage_EitherRoutesSomewhereOrIsDeliberatelyLeftOut()
    {
        // A page added to the rail later must be given a row here or be an
        // explicit omission, so the action cannot silently keep pointing at
        // whatever page happened to be last in the table.
        string[] withoutAction =
        {
            UiRailLayoutSettings.TabSettings,
        };

        foreach (string page in WorkspaceLayoutCatalog.Pages)
        {
            bool routed = MainWindowViewModel.RailScanActions
                .Any(action => action.TabKey == page);

            Assert.Equal(!withoutAction.Contains(page), routed);
        }
    }

    private static MainWindowViewModel CreateViewModel(
        IInstalledGameSaveStatusService? statusService = null)
    {
        statusService ??= new EmptyInstalledGameStatusService();

        var uiSettings = new InMemoryUiSettingsStore();
        var layout = new WorkspaceLayoutService(uiSettings);
        var themeService = new ThemeService();
        var profiles = new ProfilesViewModel(
            new EmptySteamDiscoveryService(), new NoProfileDetector(), layout);
        var installedGames = new InstalledGamesViewModel(statusService, layout, uiSettings);
        var folderPicker = new SyncProviderSelectionTests.NullFolderPickerService();
        var syncRepository = new InMemorySyncRemoteProfileRepository();

        return new MainWindowViewModel(
            uiSettings,
            themeService,
            new WindowMaterialService(themeService),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(SyncUiSettings.Default),
            new SyncProviderCatalog(),
            new EmptySteamDiscoveryService(),
            new NoProfileDetector(),
            new EmptyMappingRepository(),
            new WindowsPlatformProvider(),
            new TestDatabasePathProvider(@"C:\data\games.db"),
            installedGames,
            profiles,
            new TransferPreviewViewModel(
                new UnusedTransferPreviewService(),
                new UnusedSaveTransferService(),
                profiles,
                installedGames,
                layout),
            new BackupHistoryViewModel(
                new EmptyBackupHistoryService(),
                new UnusedBackupRestoreService(),
                new UnusedBackupCleanupService(),
                new UnusedBackupArchiveService(),
                folderPicker,
                profiles,
                layout),
            new ManualBackupViewModel(
                new UnusedManualBackupService(),
                new EmptyBackupHistoryService(),
                folderPicker,
                new EmptyPresetRepository(),
                profiles,
                installedGames,
                layout),
            new TransferHistoryViewModel(new RecordingHistoryRepository(), layout),
            new SyncViewModel(
                new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
                new SyncProviderCatalog(),
                folderPicker,
                new SyncProviderSelectionTests.InMemorySyncSettingsStore(SyncUiSettings.Default),
                syncRepository,
                new SyncRemoteProfileService(syncRepository, new InMemorySecretStore()),
                new StubSyncRemoteProfileMigrationService(SyncUiSettings.Default),
                new FixedUtcClock(DateTimeOffset.Parse("2026-08-31T12:00:00Z")),
                new StubGoogleDriveOAuthService(),
                layout),
            layout);
    }

    private sealed class InMemoryUiSettingsStore : IUiSettingsStore
    {
        private AppUiSettings _settings = AppUiSettings.Default;

        public string FilePath => "memory://ui-settings.json";

        public AppUiSettings Load() => _settings;

        public void Save(AppUiSettings settings) => _settings = settings;
    }

    private sealed class NoProfileDetector : ISteamProfileDetector
    {
        public IReadOnlyList<SteamProfile> DetectProfiles(
            SteamDiscoveryResult discovery,
            CancellationToken cancellationToken = default) => [];

        public IReadOnlyList<SteamProfile> DetectProfiles(
            string steamRoot,
            CancellationToken cancellationToken = default) => [];
    }

    private sealed class EmptyInstalledGameStatusService : IInstalledGameSaveStatusService
    {
        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledGameSaveStatus>>([]);
    }

    private sealed class OneGameStatusService : IInstalledGameSaveStatusService
    {
        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledGameSaveStatus>>(
            [
                new InstalledGameSaveStatus(
                    new SteamGame(
                        "440", "Team Fortress 2", "Team Fortress 2", "", "", "",
                        true, SteamDiscoveryConfidence.High),
                    GameSaveStatusKind.Ready,
                    "Ready",
                    ApprovedMappings: 1,
                    PendingMappings: 0,
                    NeedsFixMappings: 0,
                    SavePathExists: true,
                    FileCount: 3,
                    TotalBytes: 100,
                    VerificationResults: [],
                    Error: null),
            ]);
    }

    // Blocks inside the refresh so the command is observably in flight.
    private sealed class GatedInstalledGameStatusService : IInstalledGameSaveStatusService
    {
        private readonly TaskCompletionSource _started = new();
        private readonly TaskCompletionSource _release = new();

        public int Calls { get; private set; }

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            _started.TrySetResult();
            await _release.Task;
            return [];
        }
    }

    private sealed class ThrowingInstalledGameStatusService : IInstalledGameSaveStatusService
    {
        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Steam is unreachable.");
    }

    private sealed class EmptyPresetRepository : IManualBackupPresetRepository
    {
        public IReadOnlyList<ManualBackupPreset> GetAll() => [];

        public ManualBackupPreset Save(ManualBackupPreset preset) => preset;

        public void Delete(long id)
        {
        }

        public void MarkUsed(long id)
        {
        }
    }

    // The rail must never reach an execution path. Anything below throwing is
    // a failing test, not a passing one.
    private sealed class UnusedTransferPreviewService : ITransferPreviewService
    {
        public Task<TransferPreviewPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not preview a transfer.");
    }

    private sealed class UnusedSaveTransferService : ISaveTransferService
    {
        public Task<SaveTransferResult> ExecuteAsync(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not execute a transfer.");
    }

    private sealed class UnusedManualBackupService : IManualBackupService
    {
        public Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not preview a backup.");

        public Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not execute a backup.");
    }

    private sealed class UnusedBackupRestoreService : IBackupRestoreService
    {
        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not restore.");

        public Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not resolve restore targets.");
    }

    private sealed class UnusedBackupCleanupService : IBackupCleanupService
    {
        public Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not clean up.");

        public Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not delete a run.");
    }

    private sealed class UnusedBackupArchiveService : IBackupArchiveService
    {
        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not export.");

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string zipPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refresh must not import.");
    }
}
