using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Common;

/// <summary>
/// Reusable generic pagination controller for collections, providing preset
/// and custom page sizing, bounds clamping, sorting integration, and
/// accessible navigation commands.
/// </summary>
public class PaginationController<T> : ObservableObject
{
    public const string CustomPageSizeOption = "Custom";

    /// <summary>
    /// Exact standard preset page sizes specified for UI pagination:
    /// [1, 3, 5, 7, 9, 10, 15, 20, 25, 30, 50, 75, 100, Custom].
    /// </summary>
    public static readonly IReadOnlyList<string> PresetPageSizeOptions =
    [
        "1", "3", "5", "7", "9", "10", "15", "20", "25", "30", "50", "75", "100", CustomPageSizeOption
    ];

    private List<T> _allItems = [];
    private List<T> _sortedItems = [];

    private int _currentPage = 1;
    private int _pageSize = 20;
    private int _totalItemCount;
    private int _totalPages = 1;
    private string _selectedPageSizeOption = "20";
    private int _customPageSize = 20;
    private string _customPageSizeText = "20";
    private bool _isCustomPageSize;
    private string _itemName = "item";
    private string _pluralItemName = "items";
    private Func<IEnumerable<T>, IEnumerable<T>>? _sortFunction;

    // One instance for the controller's lifetime; every page change replaces
    // its content with a single Reset.
    public BulkObservableCollection<T> CurrentPageItems { get; } = [];

    public IReadOnlyList<string> PageSizeOptions => PresetPageSizeOptions;

    /// <summary>True while <see cref="CurrentPageItems"/> is being replaced.</summary>
    public bool IsPaging { get; private set; }

    public event EventHandler? PageChanged;

    public string ItemName
    {
        get => _itemName;
        set
        {
            if (SetProperty(ref _itemName, value))
            {
                OnPropertyChanged(nameof(PageSummaryText));
            }
        }
    }

    public string PluralItemName
    {
        get => _pluralItemName;
        set
        {
            if (SetProperty(ref _pluralItemName, value))
            {
                OnPropertyChanged(nameof(PageSummaryText));
            }
        }
    }

    public int CurrentPage => _currentPage;

    public int PageSize => _pageSize;

    public string SelectedPageSizeOption
    {
        get => _selectedPageSizeOption;
        set
        {
            if (string.Equals(_selectedPageSizeOption, value, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(value, CustomPageSizeOption, StringComparison.OrdinalIgnoreCase))
            {
                _selectedPageSizeOption = CustomPageSizeOption;
                IsCustomPageSize = true;
                OnPropertyChanged(nameof(SelectedPageSizeOption));
                ApplyPageSize(_customPageSize);
            }
            else if (int.TryParse(value, out int parsed) && parsed > 0)
            {
                SetPageSize(parsed);
            }
        }
    }

    public bool IsCustomPageSize
    {
        get => _isCustomPageSize;
        private set => SetProperty(ref _isCustomPageSize, value);
    }

    public string CustomPageSizeText
    {
        get => _customPageSizeText;
        set
        {
            if (SetProperty(ref _customPageSizeText, value) &&
                int.TryParse(value, out int parsed) && parsed > 0)
            {
                _customPageSize = parsed;

                if (IsCustomPageSize)
                {
                    ApplyPageSize(parsed);
                }
            }
        }
    }

    public int TotalItemCount
    {
        get => _totalItemCount;
        private set => SetProperty(ref _totalItemCount, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        private set => SetProperty(ref _totalPages, value);
    }

    public bool HasPreviousPage => CurrentPage > 1;

    public bool HasNextPage => CurrentPage < TotalPages;

    public string PageSummaryText
    {
        get
        {
            if (TotalItemCount == 0)
                return $"Showing 0 of 0 {PluralItemName}";

            int start = (CurrentPage - 1) * PageSize + 1;
            int end = Math.Min(CurrentPage * PageSize, TotalItemCount);
            string noun = TotalItemCount == 1 ? ItemName : PluralItemName;
            return start == end
                ? $"Showing {start} of {TotalItemCount} {noun}"
                : $"Showing {start}–{end} of {TotalItemCount} {noun}";
        }
    }

    public string PageNumberText => $"Page {CurrentPage} of {TotalPages}";

    public IRelayCommand FirstPageCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public IRelayCommand LastPageCommand { get; }

    public PaginationController()
    {
        FirstPageCommand = new RelayCommand(FirstPage, () => HasPreviousPage);
        PreviousPageCommand = new RelayCommand(PreviousPage, () => HasPreviousPage);
        NextPageCommand = new RelayCommand(NextPage, () => HasNextPage);
        LastPageCommand = new RelayCommand(LastPage, () => HasNextPage);
    }

    /// <summary>
    /// True when a list control's null selection for <paramref name="item"/>
    /// only means the item is not on the visible page (or the page is being
    /// swapped), so a view model should keep its selection rather than clear it.
    /// </summary>
    public bool IsOffPage(T item) => IsPaging || !CurrentPageItems.Contains(item);

    public void SetSource(IEnumerable<T>? items)
    {
        _allItems = items?.ToList() ?? [];
        RefreshSorted();
    }

    public void ApplySort(Func<IEnumerable<T>, IEnumerable<T>>? sortFunction)
    {
        _sortFunction = sortFunction;
        RefreshSorted();
    }

    public void SetPageSize(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Page size must be a positive integer.");

        string sizeStr = size.ToString();
        if (PresetPageSizeOptions.Contains(sizeStr))
        {
            _selectedPageSizeOption = sizeStr;
            IsCustomPageSize = false;
        }
        else
        {
            _selectedPageSizeOption = CustomPageSizeOption;
            IsCustomPageSize = true;
            _customPageSize = size;
            _customPageSizeText = sizeStr;
            OnPropertyChanged(nameof(CustomPageSizeText));
        }

        OnPropertyChanged(nameof(SelectedPageSizeOption));
        ApplyPageSize(size);
    }

    private void ApplyPageSize(int size)
    {
        if (_pageSize == size)
            return;

        _pageSize = size;
        OnPropertyChanged(nameof(PageSize));
        RecalculatePagesAndRefresh();
    }

    public void GoToPage(int page)
    {
        int clamped = Math.Clamp(page, 1, TotalPages);
        if (_currentPage != clamped)
        {
            _currentPage = clamped;
            OnPropertyChanged(nameof(CurrentPage));
            UpdatePageSlice();
        }
    }

    public void NextPage() => GoToPage(CurrentPage + 1);

    public void PreviousPage() => GoToPage(CurrentPage - 1);

    public void FirstPage() => GoToPage(1);

    public void LastPage() => GoToPage(TotalPages);

    private void RefreshSorted()
    {
        _sortedItems = _sortFunction is null
            ? _allItems
            : _sortFunction(_allItems).ToList();
        TotalItemCount = _sortedItems.Count;

        RecalculatePagesAndRefresh();
    }

    private void RecalculatePagesAndRefresh()
    {
        TotalPages = TotalItemCount == 0 ? 1 : (int)Math.Ceiling((double)TotalItemCount / PageSize);

        if (_currentPage > TotalPages)
        {
            _currentPage = TotalPages;
            OnPropertyChanged(nameof(CurrentPage));
        }

        UpdatePageSlice();
    }

    private void UpdatePageSlice()
    {
        IsPaging = true;
        try
        {
            CurrentPageItems.ReplaceAll(_sortedItems
                .Skip((CurrentPage - 1) * PageSize)
                .Take(PageSize));
        }
        finally
        {
            IsPaging = false;
        }

        OnPropertyChanged(nameof(PageSummaryText));
        OnPropertyChanged(nameof(PageNumberText));

        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();

        PageChanged?.Invoke(this, EventArgs.Empty);
    }
}
