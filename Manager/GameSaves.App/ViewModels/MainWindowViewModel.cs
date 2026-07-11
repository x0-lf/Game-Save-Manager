using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using System;
using System.Collections.Generic;

namespace GameSaves.App.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISteamProfileDetector _steamProfileDetector;
        private readonly ISavePathMappingRepository _mappingRepository;
        private readonly ICurrentPlatformProvider _platformProvider;
        private readonly IAppDatabasePathProvider _databasePathProvider;

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

        public MainWindowViewModel(
            ISteamDiscoveryService steamDiscoveryService,
            ISteamProfileDetector steamProfileDetector,
            ISavePathMappingRepository mappingRepository,
            ICurrentPlatformProvider platformProvider,
            IAppDatabasePathProvider databasePathProvider,
            InstalledGamesViewModel installedGames,
            ProfilesViewModel profiles,
            TransferPreviewViewModel transferPreview,
            BackupHistoryViewModel backupHistory)
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
        }

        [RelayCommand]
        private void Refresh()
        {
            StatusMessage = "Scanning Steam...";

            SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(
                new SteamDiscoveryOptions
                {
                    FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                    FallbackTimeout = TimeSpan.FromSeconds(30),
                    FallbackMaxDepth = 5
                });

            SteamRoot = discovery.SteamRoot ?? "Steam not found";
            LibraryCount = discovery.Libraries.Count;
            InstalledGameCount = discovery.Games.Count;

            if (discovery.SteamRoot is not null)
            {
                IReadOnlyList<SteamProfile> profiles =
                    _steamProfileDetector.DetectProfiles(discovery);

                SteamProfileCount = profiles.Count;
            }
            else
            {
                SteamProfileCount = 0;
            }

            ApprovedMappingCount = _mappingRepository.CountApprovedMappings(Platform);
            PendingMappingCount = _mappingRepository.CountPendingMappings(Platform);
            NeedsFixMappingCount = _mappingRepository.CountNeedsFixMappings(Platform);

            StatusMessage = "Scan finished.";
        }
    }
}