using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class TransferHistoryViewModel : ViewModelBase, IInitializableViewModel
    {
        private const int MaxRuns = 200;

        private readonly ITransferHistoryRepository _historyRepository;
        private bool _initialized;
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
                // A list control writes null when the selected run is merely on
                // another page; the selection itself is kept.
                if (value is null && _selectedRun is not null && Runs.Contains(_selectedRun) && Pagination.IsOffPage(_selectedRun))
                    return;

                if (SetProperty(ref _selectedRun, value))
                {
                    OnSelectedRunChanged(value);
                }
            }
        }

        public BulkObservableCollection<TransferRunRowViewModel> Runs { get; } = new();

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

                var runs = await Task.Run(() => _historyRepository.GetRecentRuns(MaxRuns));

                // Swapped only once the read succeeded, so the old rows stay
                // consistent with the old page until the new ones replace both.
                Runs.ReplaceAll(runs.Select(run => new TransferRunRowViewModel(run)));
                SelectedRun = null;

                StatusMessage = Runs.Count == 0
                    ? "No executed runs recorded yet."
                    : $"Showing the {Runs.Count} most recent run(s).";
            }
            catch (Exception ex)
            {
                // Nothing stale stays clickable under a failed read.
                Runs.Clear();
                SelectedRun = null;
                StatusMessage = $"Failed to read run history: {ex.Message}";
            }
            finally
            {
                Pagination.SetSource(Runs);
                IsLoading = false;
            }
        }
    }
}
