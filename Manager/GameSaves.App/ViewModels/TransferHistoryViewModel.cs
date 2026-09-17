using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class TransferHistoryViewModel : ViewModelBase, IInitializableViewModel
    {
        private const int MaxRuns = 200;

        private readonly ITransferHistoryRepository _historyRepository;
        private bool _initialized;
        private bool _isBulkLoading;
        private TransferRunRowViewModel? _selectedRun;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Refresh to list executed runs.";

        public TransferRunRowViewModel? SelectedRun
        {
            get => _selectedRun;
            set
            {
                if (value is null && !_isBulkLoading && _selectedRun is not null && Runs.Contains(_selectedRun))
                {
                    if (Pagination.IsPaging || !Pagination.CurrentPageItems.Contains(_selectedRun))
                    {
                        return; // Ignore null assignment during page switch
                    }
                }

                if (SetProperty(ref _selectedRun, value))
                {
                    OnSelectedRunChanged(value);
                }
            }
        }

        public ObservableCollection<TransferRunRowViewModel> Runs { get; } = new();

        public PaginationController<TransferRunRowViewModel> Pagination { get; } = new()
        {
            ItemName = "run",
            PluralItemName = "runs",
        };

        public ObservableCollection<TransferRunItemRowViewModel> RunItems { get; } = new();

        /// <summary>This page's panel arrangement.</summary>
        public GameSaves.App.Services.IWorkspaceLayoutPage Workspace { get; }

        public TransferHistoryViewModel(
            ITransferHistoryRepository historyRepository,
            GameSaves.App.Services.WorkspaceLayoutService workspaceLayout)
        {
            _historyRepository = historyRepository;

            Workspace = workspaceLayout.Page(
                GameSaves.App.Services.UiRailLayoutSettings.TabHistory);

            Pagination.PageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedRun));
            };

            Runs.CollectionChanged += (_, _) =>
            {
                if (!_isBulkLoading)
                {
                    Pagination.SetSource(Runs);
                }
            };
        }

        // Automatic startup load of the operation history. Reuses the manual
        // Refresh path; history rows are read-only. Runs at most once, and a
        // load failure is surfaced in status text rather than thrown.
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            _initialized = true;
            await RefreshRunsAsync();
        }

        private void OnSelectedRunChanged(TransferRunRowViewModel? value)
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

                _isBulkLoading = true;
                try
                {
                    Runs.Clear();
                    SelectedRun = null;

                    var runs = await Task.Run(() => _historyRepository.GetRecentRuns(MaxRuns));

                    foreach (TransferRunInfo run in runs)
                        Runs.Add(new TransferRunRowViewModel(run));
                }
                finally
                {
                    _isBulkLoading = false;
                }

                Pagination.SetSource(Runs);

                StatusMessage = Runs.Count == 0
                    ? "No executed runs recorded yet."
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
