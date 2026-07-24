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
        private bool _dashboardInitialized;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string databasePath = string.Empty;

        [ObservableProperty]
        private string platform = string.Empty;

        [ObservableProperty]
        private string steamRoot = "Not scanned yet";

        [ObservableProperty]
        private int libraryCount;

        [ObservableProperty]
        private int installedGameCount;

        [ObservableProperty]
        private int steamProfileCount;

        [ObservableProperty]
        private int approvedMappingCount;

        [ObservableProperty]
        private int pendingMappingCount;

        [ObservableProperty]
        private int needsFixMappingCount;

        [ObservableProperty]
        private string statusMessage = "Ready.";
        public InstalledGamesViewModel InstalledGames { get; }
        public ProfilesViewModel Profiles { get; }

        public TransferPreviewViewModel TransferPreview { get; }

        public BackupHistoryViewModel BackupHistory { get; }

        public ManualBackupViewModel ManualBackup { get; }

        public TransferHistoryViewModel TransferHistory { get; }

        public SyncViewModel Sync { get; }

        public MainWindowViewModel(
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
            SyncViewModel sync)
        {
            _steamDiscoveryService = steamDiscoveryService;
            _steamProfileDetector = steamProfileDetector;
            _mappingRepository = mappingRepository;
            _platformProvider = platformProvider;
            _databasePathProvider = databasePathProvider;

            DatabasePath = _databasePathProvider.GetDatabasePath();
            Platform = _platformProvider.GetCurrentPlatformKey();

            InstalledGames = installedGames;
            Profiles = profiles;
            TransferPreview = transferPreview;
            BackupHistory = backupHistory;
            ManualBackup = manualBackup;
            TransferHistory = transferHistory;
            Sync = sync;
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
                LibraryCount = snapshot.LibraryCount;
                InstalledGameCount = snapshot.InstalledGameCount;
                SteamProfileCount = snapshot.SteamProfileCount;
                ApprovedMappingCount = snapshot.ApprovedMappingCount;
                PendingMappingCount = snapshot.PendingMappingCount;
                NeedsFixMappingCount = snapshot.NeedsFixMappingCount;

                StatusMessage = "Scan finished.";
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
            int LibraryCount,
            int InstalledGameCount,
            int SteamProfileCount,
            int ApprovedMappingCount,
            int PendingMappingCount,
            int NeedsFixMappingCount);
    }
}