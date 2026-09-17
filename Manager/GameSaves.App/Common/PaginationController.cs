using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GameSaves.App.Common;

/// <summary>
/// Reusable generic pagination controller for collections, providing preset
/// and custom page sizing, bounds clamping, sorting/filtering integration,
/// and accessible navigation commands.
/// </summary>
public partial class PaginationController<T> : ObservableObject
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
    private List<T> _filteredAndSortedItems = [];

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
    private Func<T, bool>? _filterPredicate;
    private Func<IEnumerable<T>, IEnumerable<T>>? _sortFunction;

    public ObservableCollection<T> CurrentPageItems { get; } = [];

    public IReadOnlyList<string> PageSizeOptions => PresetPageSizeOptions;

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

    public int CurrentPage
    {
        get => _currentPage;
        set => GoToPage(value);
    }

    public int PageSize
    {
        get => _pageSize;
        set => SetPageSize(value);
    }

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
                if (_customPageSize > 0)
                {
                    ApplyPageSize(_customPageSize);
                }
            }
            else if (int.TryParse(value, out int parsed) && parsed > 0)
            {
                _selectedPageSizeOption = parsed.ToString();
                IsCustomPageSize = false;
                ApplyPageSize(parsed);
            }

            OnPropertyChanged(nameof(SelectedPageSizeOption));
        }
    }

    public bool IsCustomPageSize
    {
        get => _isCustomPageSize;
        private set => SetProperty(ref _isCustomPageSize, value);
    }

    public int CustomPageSize
    {
        get => _customPageSize;
        set
        {
            if (value <= 0)
                return;

            if (SetProperty(ref _customPageSize, value))
            {
                _customPageSizeText = value.ToString();
                OnPropertyChanged(nameof(CustomPageSizeText));

                if (IsCustomPageSize)
                {
                    ApplyPageSize(value);
                }
            }
        }
    }

    public string CustomPageSizeText
    {
        get => _customPageSizeText;
        set
        {
            if (SetProperty(ref _customPageSizeText, value))
            {
                if (int.TryParse(value, out int parsed) && parsed > 0)
                {
                    _customPageSize = parsed;
                    OnPropertyChanged(nameof(CustomPageSize));

                    if (IsCustomPageSize)
                    {
                        ApplyPageSize(parsed);
                    }
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
            return start == end
                ? $"Showing {start} of {TotalItemCount} {ItemName}"
                : $"Showing {start}–{end} of {TotalItemCount} {PluralItemName}";
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

    public void SetSource(IEnumerable<T>? items)
    {
        _allItems = items?.ToList() ?? [];
        RefreshFilteredAndSorted(resetToFirstPage: false);
    }

    public void ApplyFilter(Func<T, bool>? filterPredicate, bool resetToFirstPage = true)
    {
        _filterPredicate = filterPredicate;
        RefreshFilteredAndSorted(resetToFirstPage);
    }

    public void ApplySort(Func<IEnumerable<T>, IEnumerable<T>>? sortFunction, bool resetToFirstPage = false)
    {
        _sortFunction = sortFunction;
        RefreshFilteredAndSorted(resetToFirstPage);
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
            OnPropertyChanged(nameof(CustomPageSize));
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

    public void NextPage()
    {
        if (HasNextPage)
        {
            GoToPage(CurrentPage + 1);
        }
    }

    public void PreviousPage()
    {
        if (HasPreviousPage)
        {
            GoToPage(CurrentPage - 1);
        }
    }

    public void FirstPage() => GoToPage(1);

    public void LastPage() => GoToPage(TotalPages);

    private void RefreshFilteredAndSorted(bool resetToFirstPage)
    {
        IEnumerable<T> items = _allItems;

        if (_filterPredicate is not null)
        {
            items = items.Where(_filterPredicate);
        }

        if (_sortFunction is not null)
        {
            items = _sortFunction(items);
        }

        _filteredAndSortedItems = items.ToList();
        TotalItemCount = _filteredAndSortedItems.Count;

        if (resetToFirstPage)
        {
            _currentPage = 1;
            OnPropertyChanged(nameof(CurrentPage));
        }

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
        else if (_currentPage < 1)
        {
            _currentPage = 1;
            OnPropertyChanged(nameof(CurrentPage));
        }

        UpdatePageSlice();
    }

    private void UpdatePageSlice()
    {
        var slice = _filteredAndSortedItems
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        IsPaging = true;
        try
        {
            CurrentPageItems.Clear();
            foreach (T item in slice)
            {
                CurrentPageItems.Add(item);
            }
        }
        finally
        {
            IsPaging = false;
        }

        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageSummaryText));
        OnPropertyChanged(nameof(PageNumberText));

        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();

        PageChanged?.Invoke(this, EventArgs.Empty);
    }
}
