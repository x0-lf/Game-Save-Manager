using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class TransferPreviewViewModel : ViewModelBase
    {
        private readonly ITransferPreviewService _transferPreviewService;
        private readonly ProfilesViewModel _profilesViewModel;
        private readonly InstalledGamesViewModel _installedGamesViewModel;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Select source profile, target profile, and game.";

        [ObservableProperty]
        private SteamProfileRowViewModel? selectedSourceProfile;

        [ObservableProperty]
        private SteamProfileRowViewModel? selectedTargetProfile;

        [ObservableProperty]
        private InstalledGameRowViewModel? selectedGame;

        [ObservableProperty]
        private int totalFiles;

        [ObservableProperty]
        private string totalSizeDisplay = "0 B";

        [ObservableProperty]
        private bool canExecuteTransferLater;

        public ObservableCollection<SteamProfileRowViewModel> Profiles =>
            _profilesViewModel.Profiles;

        public ObservableCollection<InstalledGameRowViewModel> Games =>
            _installedGamesViewModel.Games;

        public ObservableCollection<TransferPreviewItemRowViewModel> Items { get; } = new();

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public TransferPreviewViewModel(
            ITransferPreviewService transferPreviewService,
            ProfilesViewModel profilesViewModel,
            InstalledGamesViewModel installedGamesViewModel)
        {
            _transferPreviewService = transferPreviewService;
            _profilesViewModel = profilesViewModel;
            _installedGamesViewModel = installedGamesViewModel;
        }

        [RelayCommand]
        private async Task RefreshInputsAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Refreshing profiles and installed games...";

                if (Profiles.Count == 0)
                    await _profilesViewModel.RefreshProfilesCommand.ExecuteAsync(null);

                if (Games.Count == 0)
                    await _installedGamesViewModel.RefreshCommand.ExecuteAsync(null);

                SelectedSourceProfile = _profilesViewModel.SourceProfile ?? Profiles.FirstOrDefault();
                SelectedTargetProfile = _profilesViewModel.TargetProfile ?? Profiles.Skip(1).FirstOrDefault();
                SelectedGame = _installedGamesViewModel.SelectedGame ?? Games.FirstOrDefault();

                StatusMessage = "Inputs refreshed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to refresh inputs: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task PreviewTransferAsync()
        {
            if (IsLoading)
                return;

            Items.Clear();
            Warnings.Clear();
            TotalFiles = 0;
            TotalSizeDisplay = "0 B";
            CanExecuteTransferLater = false;

            if (SelectedSourceProfile is null)
            {
                StatusMessage = "Select a source profile.";
                return;
            }

            if (SelectedTargetProfile is null)
            {
                StatusMessage = "Select a target profile.";
                return;
            }

            if (SelectedGame is null)
            {
                StatusMessage = "Select an installed game.";
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Building dry-run transfer preview...";

                TransferPreviewPlan plan =
                    await _transferPreviewService.CreatePreviewAsync(
                        SelectedGame.Game,
                        SelectedSourceProfile.Profile,
                        SelectedTargetProfile.Profile);

                foreach (TransferPreviewItem item in plan.Items)
                    Items.Add(new TransferPreviewItemRowViewModel(item));

                foreach (TransferPreviewWarning warning in plan.Warnings)
                    Warnings.Add(new TransferWarningRowViewModel(warning));

                TotalFiles = plan.TotalFiles;
                TotalSizeDisplay = FormatBytes(plan.TotalBytes);
                CanExecuteTransferLater = plan.CanExecute;

                StatusMessage = plan.HasItems
                    ? $"Preview ready for {plan.Game.Name}. No files were copied."
                    : "Preview created no items. Check mappings and selected profiles.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to build transfer preview: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            double kb = bytes / 1024.0;

            if (kb < 1024)
                return $"{kb:0.##} KB";

            double mb = kb / 1024.0;

            if (mb < 1024)
                return $"{mb:0.##} MB";

            double gb = mb / 1024.0;

            return $"{gb:0.##} GB";
        }
    }
}