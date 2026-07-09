using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Save;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class InstalledGamesViewModel : ViewModelBase
    {
        private readonly IInstalledGameSaveStatusService _statusService;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Ready.";

        [ObservableProperty]
        private InstalledGameRowViewModel? selectedGame;

        public ObservableCollection<InstalledGameRowViewModel> Games { get; } = new();

        public InstalledGamesViewModel(
            IInstalledGameSaveStatusService statusService)
        {
            _statusService = statusService;
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Loading installed games...";

                IReadOnlyList<InstalledGameSaveStatus> statuses =
                    await _statusService.GetInstalledGameStatusesAsync();

                Games.Clear();

                foreach (InstalledGameSaveStatus status in statuses)
                    Games.Add(new InstalledGameRowViewModel(status));

                SelectedGame =
                    Games.FirstOrDefault(game => game.StatusKind == GameSaveStatusKind.Ready)
                    ?? Games.FirstOrDefault();

                StatusMessage = $"Loaded {Games.Count} installed games.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load installed games: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}