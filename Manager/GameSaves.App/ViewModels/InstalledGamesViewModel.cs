using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Save;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class InstalledGamesViewModel : ViewModelBase, IInitializableViewModel
    {
        private readonly IInstalledGameSaveStatusService _statusService;
        private bool _initialized;

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

        // Automatic startup load. Reuses the manual Refresh path so both produce
        // identical results, and runs at most once.
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            _initialized = true;
            await RefreshAsync();
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

                StatusMessage = Games.Count switch
                {
                    0 => "No installed games found.",
                    1 => "1 installed game found.",
                    _ => $"{Games.Count} installed games found.",
                };
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