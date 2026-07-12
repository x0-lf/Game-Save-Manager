using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class TransferHistoryViewModel : ViewModelBase
    {
        private const int MaxRuns = 200;

        private readonly ITransferHistoryRepository _historyRepository;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Refresh to list executed runs.";

        [ObservableProperty]
        private TransferRunRowViewModel? selectedRun;

        public ObservableCollection<TransferRunRowViewModel> Runs { get; } = new();

        public ObservableCollection<TransferRunItemRowViewModel> RunItems { get; } = new();

        public TransferHistoryViewModel(ITransferHistoryRepository historyRepository)
        {
            _historyRepository = historyRepository;
        }

        partial void OnSelectedRunChanged(TransferRunRowViewModel? value)
        {
            RunItems.Clear();

            if (value is null)
                return;

            _ = LoadRunItemsAsync(value.Id);
        }

        private async Task LoadRunItemsAsync(long runId)
        {
            try
            {
                var items = await Task.Run(() => _historyRepository.GetRunItems(runId));

                // The selection may have changed while loading.
                if (SelectedRun?.Id != runId)
                    return;

                RunItems.Clear();

                foreach (TransferRunItemRecord item in items)
                    RunItems.Add(new TransferRunItemRowViewModel(item));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load run items: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task RefreshRunsAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Reading run history...";

                Runs.Clear();
                SelectedRun = null;

                var runs = await Task.Run(() => _historyRepository.GetRecentRuns(MaxRuns));

                foreach (TransferRunInfo run in runs)
                    Runs.Add(new TransferRunRowViewModel(run));

                StatusMessage = Runs.Count == 0
                    ? "No executed runs recorded yet. Transfers, restores, and manual backups are recorded automatically."
                    : $"Showing the {Runs.Count} most recent run(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to read run history: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
