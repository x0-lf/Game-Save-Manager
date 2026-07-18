using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.Reviewer.Data;
using GameSaves.Reviewer.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace GameSaves.Reviewer.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly MappingReviewRepository _repository;
        private IReadOnlyList<MappingReviewItem> _selection = Array.Empty<MappingReviewItem>();
        private bool _schemaInitialized;

        public string DatabasePath { get; }

        public IReadOnlyList<string> StatusFilters { get; } = new[]
        {
            "Pending",
            "Approved",
            "Rejected",
            "NeedsFix"
        };

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Reload to list mappings.";

        [ObservableProperty]
        private string statusFilter = "Pending";

        [ObservableProperty]
        private string searchText = "";

        [ObservableProperty]
        private string limitText = "1000";

        [ObservableProperty]
        private string approvePriorityText = "40";

        [ObservableProperty]
        private string reviewNotes = "";

        [ObservableProperty]
        private MappingReviewItem? selectedItem;

        [ObservableProperty]
        private int selectionCount;

        [ObservableProperty]
        private int pendingCount;

        [ObservableProperty]
        private int approvedCount;

        [ObservableProperty]
        private int rejectedCount;

        [ObservableProperty]
        private int needsFixCount;

        public ObservableCollection<MappingReviewItem> Items { get; } = new();

        public MainWindowViewModel(string databasePath)
        {
            DatabasePath = databasePath;
            _repository = new MappingReviewRepository(databasePath);
        }

        partial void OnStatusFilterChanged(string value)
        {
            _ = ReloadAsync();
        }

        /// <summary>Called by the view whenever the grid selection changes.</summary>
        public void UpdateSelection(IReadOnlyList<MappingReviewItem> selection)
        {
            _selection = selection;
            SelectionCount = selection.Count;
        }

        [RelayCommand]
        public async Task ReloadAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusMessage = "Loading mappings...";

                string status = StatusFilter;
                int limit = ParseLimit();
                string? search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText;

                var (rows, counts) = await Task.Run(() =>
                {
                    if (!_schemaInitialized)
                    {
                        _repository.InitializeReviewColumns();
                        _schemaInitialized = true;
                    }

                    return (
                        _repository.LoadByStatus(status, limit, search),
                        _repository.CountByStatus());
                });

                Items.Clear();

                foreach (MappingReviewItem row in rows)
                    Items.Add(row);

                PendingCount = counts.Pending;
                ApprovedCount = counts.Approved;
                RejectedCount = counts.Rejected;
                NeedsFixCount = counts.NeedsFix;

                StatusMessage = $"Showing {Items.Count} {status} mapping(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load mappings: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public Task ApproveSelectedAsync()
        {
            if (!TryParseApprovePriority(out int priority))
            {
                StatusMessage = "Approve priority must be a number between 1 and 1000.";
                return Task.CompletedTask;
            }

            return RunDecisionAsync(
                "approved",
                ids => _repository.Approve(ids, priority, ReviewNotes));
        }

        [RelayCommand]
        public Task RejectSelectedAsync()
        {
            return RunDecisionAsync(
                "rejected",
                ids => _repository.Reject(ids, ReviewNotes));
        }

        [RelayCommand]
        public Task MarkNeedsFixSelectedAsync()
        {
            return RunDecisionAsync(
                "marked as needs-fix",
                ids => _repository.MarkNeedsFix(ids, ReviewNotes));
        }

        [RelayCommand]
        public Task ResetSelectedAsync()
        {
            return RunDecisionAsync(
                "reset to pending",
                ids => _repository.ResetToPending(ids));
        }

        private async Task RunDecisionAsync(
            string pastTenseAction,
            Action<IReadOnlyList<long>> decision)
        {
            if (IsLoading)
                return;

            IReadOnlyList<long> ids = _selection
                .Select(item => item.Id)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                StatusMessage = "Select one or more mappings first.";
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = $"Updating {ids.Count} mapping(s)...";

                await Task.Run(() => decision(ids));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to update mappings: {ex.Message}";
                IsLoading = false;
                return;
            }

            IsLoading = false;
            await ReloadAsync();
            StatusMessage = $"{ids.Count} mapping(s) {pastTenseAction}.";
        }

        [RelayCommand]
        public void OpenSelectedSource()
        {
            MappingReviewItem? item = SelectedItem ?? _selection.FirstOrDefault();

            if (item is null || string.IsNullOrWhiteSpace(item.SourceUrl))
            {
                StatusMessage = "The selected mapping has no source URL.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = item.SourceUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open the source URL: {ex.Message}";
            }
        }

        private int ParseLimit()
        {
            if (!int.TryParse(LimitText?.Trim(), out int limit))
                return 1000;

            return Math.Clamp(limit, 10, 10000);
        }

        private bool TryParseApprovePriority(out int priority)
        {
            if (int.TryParse(ApprovePriorityText?.Trim(), out priority) &&
                priority is >= 1 and <= 1000)
            {
                return true;
            }

            priority = 0;
            return false;
        }
    }
}
