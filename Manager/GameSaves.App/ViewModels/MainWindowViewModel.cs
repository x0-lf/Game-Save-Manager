using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GameSaves.App.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IInitializableViewModel
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISteamProfileDetector _steamProfileDetector;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ICurrentPlatformProvider _platformProvider;
        private readonly IAppDatabasePathProvider _databasePathProvider;
        private readonly GameSaves.App.Services.WorkspaceLayoutService _workspaceLayout;
        private bool _dashboardInitialized;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string databasePath = string.Empty;

        [ObservableProperty]
        private string platform = string.Empty;

        [ObservableProperty]
        private string steamRoot = "Not scanned yet";

        // True only after a completed scan that found no Steam installation;
        // the Dashboard shows an actionable error banner instead of leaving
        // the failure as passive text inside a stat card.
        [ObservableProperty]
        private bool isSteamMissing;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLibraries))]
        private int libraryCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInstalledGames))]
        private int installedGameCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSteamProfiles))]
        private int steamProfileCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasApprovedMappings))]
        private int approvedMappingCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingMappings))]
        private int pendingMappingCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNeedsFixMappings))]
        private int needsFixMappingCount;

        // Semantic colour is applied to a stat only when it carries real
        // state; a zero is neutral. See ui-critic-round-1 finding 15.
        public bool HasLibraries => LibraryCount > 0;

        public bool HasInstalledGames => InstalledGameCount > 0;

        public bool HasSteamProfiles => SteamProfileCount > 0;

        public bool HasApprovedMappings => ApprovedMappingCount > 0;

        public bool HasPendingMappings => PendingMappingCount > 0;

        public bool HasNeedsFixMappings => NeedsFixMappingCount > 0;

        [ObservableProperty]
        private string statusMessage = "Ready.";

        public InstalledGamesViewModel InstalledGames { get; }
        public ProfilesViewModel Profiles { get; }

        public TransferPreviewViewModel TransferPreview { get; }

        public BackupHistoryViewModel BackupHistory { get; }

        public ManualBackupViewModel ManualBackup { get; }

        public TransferHistoryViewModel TransferHistory { get; }

        public SyncViewModel Sync { get; }

        public SettingsViewModel Settings { get; }

        /// <summary>The Dashboard's panel arrangement.</summary>
        public GameSaves.App.Services.IWorkspaceLayoutPage Workspace { get; }

        /// <summary>
        /// Any page's arrangement, by its stable rail tab key. The shell needs
        /// this to offer a section list on a rail entry's context menu, which
        /// is the route that keeps section visibility reachable whichever edge
        /// the rail is on.
        /// </summary>
        public GameSaves.App.Services.IWorkspaceLayoutPage WorkspacePageFor(string tabKey) =>
            _workspaceLayout.Page(tabKey);

        /// <summary>
        /// One row of the navigation rail's Scan/Refresh action: how it
        /// presents itself on a page, and which of that page's own commands it
        /// runs. Holding the command rather than a copy of its work is what
        /// keeps the rail and the page's own Refresh button on one code path,
        /// so loading state, status text, error reporting and availability are
        /// whatever the page already does.
        /// </summary>
        internal sealed record RailScanAction(
            string TabKey,
            string Label,
            string Description,
            Func<MainWindowViewModel, ICommand> Command);

        /// <summary>
        /// The whole active-page-to-command mapping. A page with no row has no
        /// rail action, which is how Settings hides it: absence, rather than a
        /// disabled button or a gap where one used to be.
        /// </summary>
        internal static readonly IReadOnlyList<RailScanAction> RailScanActions =
            new RailScanAction[]
            {
                new(GameSaves.App.Services.UiRailLayoutSettings.TabDashboard,
                    "Scan",
                    "Scan Steam library and refresh Dashboard",
                    viewModel => viewModel.RefreshCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabInstalledGames,
                    "Scan",
                    "Scan installed games",
                    viewModel => viewModel.InstalledGames.RefreshCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabProfiles,
                    "Refresh",
                    "Refresh profiles",
                    viewModel => viewModel.Profiles.RefreshProfilesCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabTransferPreview,
                    "Refresh",
                    "Refresh Transfer Profiles",
                    viewModel => viewModel.TransferPreview.RefreshInputsCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabManualBackup,
                    "Refresh",
                    "Refresh Manual Backup",
                    viewModel => viewModel.ManualBackup.RefreshInputsCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabBackups,
                    "Refresh",
                    "Refresh backups",
                    viewModel => viewModel.BackupHistory.RefreshRunsCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabSync,
                    "Refresh",
                    "Refresh Sync status",
                    viewModel => viewModel.Sync.CheckSyncStatusCommand),
                new(GameSaves.App.Services.UiRailLayoutSettings.TabHistory,
                    "Refresh",
                    "Refresh history",
                    viewModel => viewModel.TransferHistory.RefreshRunsCommand),
            };

        // The page the rail is pointing at. The shell pushes it on every
        // navigation change; the rail action follows from it and nothing else.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RailScanCommand))]
        [NotifyPropertyChangedFor(nameof(RailScanLabel))]
        [NotifyPropertyChangedFor(nameof(RailScanDescription))]
        [NotifyPropertyChangedFor(nameof(IsRailScanVisible))]
        private string activeTabKey =
            GameSaves.App.Services.UiRailLayoutSettings.TabDashboard;

        private RailScanAction? ActiveRailScanAction =>
            RailScanActions.FirstOrDefault(
                action => string.Equals(
                    action.TabKey, ActiveTabKey, StringComparison.Ordinal));

        /// <summary>
        /// The active page's own refresh command. Binding the command itself
        /// means the rail button disables while that command runs, so a second
        /// press cannot start a duplicate, and an operation already in flight
        /// keeps belonging to the page that started it even if the user
        /// navigates away.
        /// </summary>
        public ICommand? RailScanCommand => ActiveRailScanAction?.Command(this);

        /// <summary>The rail action's visible text on the active page.</summary>
        public string RailScanLabel => ActiveRailScanAction?.Label ?? "Refresh";

        /// <summary>
        /// The rail action's tooltip and accessible name. It stays accurate
        /// while the rail is collapsed, where the label is not rendered.
        /// </summary>
        public string RailScanDescription =>
            ActiveRailScanAction?.Description ?? string.Empty;

        /// <summary>
        /// False on a page with no refresh, and false while the user has the
        /// action switched off in Settings.
        /// </summary>
        public bool IsRailScanVisible =>
            ActiveRailScanAction is not null && Settings.ShowScanInNavigationRail;

        public MainWindowViewModel(
            GameSaves.App.Services.IUiSettingsStore uiSettingsStore,
            GameSaves.App.Services.ThemeService themeService,
            GameSaves.App.Services.WindowMaterialService windowMaterialService,
            GameSaves.App.Services.ISyncSettingsStore syncSettingsStore,
            GameSaves.Core.Sync.ISyncProviderCatalog providerCatalog,
            ISteamDiscoveryService steamDiscoveryService,
            ISteamProfileDetector steamProfileDetector,
            ISavePathMappingRepository mappingRepository,
            ICurrentPlatformProvider platformProvider,
            IAppDatabasePathProvider databasePathProvider,
            InstalledGamesViewModel installedGames,
            ProfilesViewModel profiles,
            TransferPreviewViewModel transferPreview,
            BackupHistoryViewModel backupHistory,
            ManualBackupViewModel manualBackup,
            TransferHistoryViewModel transferHistory,
            SyncViewModel sync,
            GameSaves.App.Services.WorkspaceLayoutService workspaceLayout)
        {
            _workspaceLayout = workspaceLayout;
            _steamDiscoveryService = steamDiscoveryService;
            _steamProfileDetector = steamProfileDetector;
            _mappingRepository = mappingRepository;
            _platformProvider = platformProvider;
            _databasePathProvider = databasePathProvider;

            DatabasePath = _databasePathProvider.GetDatabasePath();
            Platform = _platformProvider.GetCurrentPlatformKey();

            // The Dashboard's content lives in the shell rather than in its own
            // view, so the shell owns its workspace page too. Every other page
            // gets its own from the same service.
            Workspace = workspaceLayout.Page(
                GameSaves.App.Services.UiRailLayoutSettings.TabDashboard);

            InstalledGames = installedGames;
            Profiles = profiles;
            TransferPreview = transferPreview;
            BackupHistory = backupHistory;
            ManualBackup = manualBackup;
            TransferHistory = transferHistory;
            Sync = sync;

            Settings = new SettingsViewModel(
                uiSettingsStore,
                themeService,
                windowMaterialService,
                installedGames,
                syncSettingsStore,
                providerCatalog,
                Platform,
                DatabasePath,
                workspaceLayout);

            // The rail action can also be switched off entirely in Settings,
            // so its visibility depends on that setting as well as the page.
            Settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is null or
                    nameof(SettingsViewModel.ShowScanInNavigationRail))
                {
                    OnPropertyChanged(nameof(IsRailScanVisible));
                }
            };
        }

        // Automatic startup load of the Dashboard. Reuses the same load path as
        // the Refresh Dashboard button and runs at most once.
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_dashboardInitialized)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            _dashboardInitialized = true;
            await LoadDashboardAsync(cancellationToken);
        }

        // The Refresh Dashboard button. Keeps the generated RefreshCommand name.
        [RelayCommand]
        private Task RefreshAsync()
        {
            return LoadDashboardAsync(CancellationToken.None);
        }

        // Single authoritative Dashboard load, shared by startup and Refresh.
        // The Steam scan runs off the UI thread; results are applied afterwards.
        private async Task LoadDashboardAsync(CancellationToken cancellationToken)
        {
            if (IsLoading)
                return;

            string platform = Platform;

            try
            {
                IsLoading = true;
                StatusMessage = "Scanning Steam...";

                DashboardSnapshot snapshot = await Task.Run(
                    () =>
                    {
                        SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(
                            new SteamDiscoveryOptions
                            {
                                FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                                FallbackTimeout = TimeSpan.FromSeconds(30),
                                FallbackMaxDepth = 5
                            });

                        int profileCount = discovery.SteamRoot is not null
                            ? _steamProfileDetector.DetectProfiles(discovery).Count
                            : 0;

                        return new DashboardSnapshot(
                            discovery.SteamRoot ?? "Steam not found",
                            discovery.SteamRoot is null,
                            discovery.Libraries.Count,
                            discovery.Games.Count,
                            profileCount,
                            _mappingRepository.CountApprovedMappings(platform),
                            _mappingRepository.CountPendingMappings(platform),
                            _mappingRepository.CountNeedsFixMappings(platform));
                    },
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                SteamRoot = snapshot.SteamRoot;
                IsSteamMissing = snapshot.SteamMissing;
                LibraryCount = snapshot.LibraryCount;
                InstalledGameCount = snapshot.InstalledGameCount;
                SteamProfileCount = snapshot.SteamProfileCount;
                ApprovedMappingCount = snapshot.ApprovedMappingCount;
                PendingMappingCount = snapshot.PendingMappingCount;
                NeedsFixMappingCount = snapshot.NeedsFixMappingCount;

                StatusMessage = snapshot.SteamMissing
                    ? "Steam not detected."
                    : "Scan complete.";
            }
            catch (OperationCanceledException)
            {
                // Cancelled during shutdown; leave the last valid values in place.
                throw;
            }
            catch (Exception ex)
            {
                // Preserve any previously loaded values; report via status text.
                StatusMessage = $"Dashboard scan failed: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private sealed record DashboardSnapshot(
            string SteamRoot,
            bool SteamMissing,
            int LibraryCount,
            int InstalledGameCount,
            int SteamProfileCount,
            int ApprovedMappingCount,
            int PendingMappingCount,
            int NeedsFixMappingCount);
    }
}