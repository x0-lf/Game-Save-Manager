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
        private readonly ISaveTransferService _saveTransferService;
        private TransferPreviewPlan? _lastPlan;

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

        [ObservableProperty]
        private bool confirmRealTransfer;

        [ObservableProperty]
        private bool overwriteExisting;

        [ObservableProperty]
        private string executionStatusMessage = "No transfer executed.";

        [ObservableProperty]
        private int filesCopied;

        [ObservableProperty]
        private int filesSkipped;

        [ObservableProperty]
        private string bytesCopiedDisplay = "0 B";

        public ObservableCollection<SteamProfileRowViewModel> Profiles =>
            _profilesViewModel.Profiles;

        public ObservableCollection<InstalledGameRowViewModel> Games =>
            _installedGamesViewModel.Games;

        public ObservableCollection<TransferPreviewItemRowViewModel> Items { get; } = new();

        public ObservableCollection<TransferWarningRowViewModel> Warnings { get; } = new();

        public ObservableCollection<SaveTransferItemResultRowViewModel> ExecutionResults { get; } = new();

        public TransferPreviewViewModel(
            ITransferPreviewService transferPreviewService,
            ISaveTransferService saveTransferService,
            ProfilesViewModel profilesViewModel,
            InstalledGamesViewModel installedGamesViewModel)
        {
            _transferPreviewService = transferPreviewService;
            _saveTransferService = saveTransferService;
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

                _lastPlan = plan;
                ExecutionResults.Clear();
                ExecutionStatusMessage = "No transfer executed.";
                FilesCopied = 0;
                FilesSkipped = 0;
                BytesCopiedDisplay = "0 B";
                ConfirmRealTransfer = false;

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

        [RelayCommand]
        private async Task ExecuteTransferAsync()
        {
            if (IsLoading)
                return;

            if (_lastPlan is null)
            {
                ExecutionStatusMessage = "Build a transfer preview first.";
                return;
            }

            if (!ConfirmRealTransfer)
            {
                ExecutionStatusMessage = "Real transfer blocked. Confirm the checkbox first.";
                return;
            }

            try
            {
                IsLoading = true;
                ExecutionStatusMessage = "Executing safe local transfer...";

                var options = new SaveTransferOptions
                {
                    DryRun = false,
                    ConfirmExecution = ConfirmRealTransfer,
                    OverwriteExisting = OverwriteExisting,
                    PreserveTimestamps = true
                };

                SaveTransferResult result =
                    await _saveTransferService.ExecuteAsync(
                        _lastPlan,
                        options);

                ExecutionResults.Clear();

                foreach (SaveTransferItemResult item in result.Items)
                    ExecutionResults.Add(new SaveTransferItemResultRowViewModel(item));

                FilesCopied = result.FilesCopied;
                FilesSkipped = result.FilesSkipped;
                BytesCopiedDisplay = FormatBytes(result.BytesCopied);

                ExecutionStatusMessage =
                    $"Transfer finished. Copied {FilesCopied} file(s), skipped {FilesSkipped} file(s).";
            }
            catch (Exception ex)
            {
                ExecutionStatusMessage = $"Transfer failed: {ex.Message}";
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