using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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