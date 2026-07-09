using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class ProfilesViewModel : ViewModelBase
    {
        private readonly ISteamDiscoveryService _steamDiscoveryService;
        private readonly ISteamProfileDetector _steamProfileDetector;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Ready.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSelectionValid))]
        [NotifyPropertyChangedFor(nameof(SelectionWarning))]
        [NotifyPropertyChangedFor(nameof(SourcePath))]
        private SteamProfileRowViewModel? sourceProfile;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSelectionValid))]
        [NotifyPropertyChangedFor(nameof(SelectionWarning))]
        [NotifyPropertyChangedFor(nameof(TargetPath))]
        private SteamProfileRowViewModel? targetProfile;

        public ObservableCollection<SteamProfileRowViewModel> Profiles { get; } = new();

        public bool HasProfiles => Profiles.Count > 0;

        public string SourcePath => SourceProfile?.UserDataPath ?? "No source profile selected.";

        public string TargetPath => TargetProfile?.UserDataPath ?? "No target profile selected.";

        public bool IsSelectionValid =>
            SourceProfile is not null &&
            TargetProfile is not null &&
            !SourceProfile.AccountId.Equals(
                TargetProfile.AccountId,
                StringComparison.OrdinalIgnoreCase);

        public string SelectionWarning
        {
            get
            {
                if (SourceProfile is null || TargetProfile is null)
                    return "Select a source and target profile.";

                if (SourceProfile.AccountId.Equals(
                        TargetProfile.AccountId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "Source and target profile cannot be the same.";
                }

                return "Source and target profiles are ready for dry-run preview.";
            }
        }

        public ProfilesViewModel(
            ISteamDiscoveryService steamDiscoveryService,
            ISteamProfileDetector steamProfileDetector)
        {
            _steamDiscoveryService = steamDiscoveryService;
            _steamProfileDetector = steamProfileDetector;
        }

        [RelayCommand]
        private async Task RefreshProfilesAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Detecting Steam profiles...";

                IReadOnlyList<SteamProfile> detectedProfiles = await Task.Run(() =>
                {
                    SteamDiscoveryResult discovery = _steamDiscoveryService.Discover(
                        new SteamDiscoveryOptions
                        {
                            FallbackScanMode = SteamFallbackScanMode.WhenNormalDiscoveryFails,
                            FallbackTimeout = TimeSpan.FromSeconds(30),
                            FallbackMaxDepth = 5
                        });

                    return _steamProfileDetector.DetectProfiles(discovery);
                });

                Profiles.Clear();

                foreach (SteamProfile profile in detectedProfiles
                             .OrderByDescending(profile => profile.AppFolderCount)
                             .ThenBy(profile => profile.AccountId))
                {
                    Profiles.Add(new SteamProfileRowViewModel(profile));
                }

                SourceProfile = Profiles.FirstOrDefault();
                TargetProfile = Profiles.Skip(1).FirstOrDefault();

                OnPropertyChanged(nameof(HasProfiles));

                StatusMessage = Profiles.Count == 0
                    ? "No Steam profiles found."
                    : $"Loaded {Profiles.Count} Steam profile(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to detect profiles: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void OpenSourceFolder()
        {
            OpenFolder(SourceProfile?.UserDataPath);
        }

        [RelayCommand]
        private void OpenTargetFolder()
        {
            OpenFolder(TargetProfile?.UserDataPath);
        }

        private void OpenFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                StatusMessage = "Folder does not exist.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to open folder: {ex.Message}";
            }
        }
    }
}