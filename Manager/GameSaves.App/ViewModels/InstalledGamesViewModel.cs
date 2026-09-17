using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.Core.Save;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameSaves.App.ViewModels
{
    public partial class InstalledGamesViewModel : ViewModelBase, IInitializableViewModel
    {
        private readonly IInstalledGameSaveStatusService _statusService;
        private readonly IUiSettingsStore? _uiSettingsStore;
        private bool _initialized;
        private bool _isBulkLoading;
        private InstalledGameRowViewModel? _selectedGame;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private string statusMessage = "Ready.";

        public InstalledGameRowViewModel? SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (value is null && _selectedGame is not null && Games.Contains(_selectedGame))
                {
                    if (Pagination.IsPaging || !Pagination.CurrentPageItems.Contains(_selectedGame))
                    {
                        return;
                    }
                }

                SetProperty(ref _selectedGame, value);
            }
        }

        public ObservableCollection<InstalledGameRowViewModel> Games { get; } = new();

        public PaginationController<InstalledGameRowViewModel> Pagination { get; } = new()
        {
            ItemName = "game",
            PluralItemName = "games",
        };

        public ObservableCollection<InstalledGameColumnOption> ColumnOptions { get; }

        /// <summary>This page's panel arrangement.</summary>
        public GameSaves.App.Services.IWorkspaceLayoutPage Workspace { get; }

        public InstalledGamesViewModel(
            IInstalledGameSaveStatusService statusService,
            GameSaves.App.Services.WorkspaceLayoutService workspaceLayout,
            IUiSettingsStore? uiSettingsStore = null)
        {
            _statusService = statusService;
            _uiSettingsStore = uiSettingsStore;

            Workspace = workspaceLayout.Page(
                GameSaves.App.Services.UiRailLayoutSettings.TabInstalledGames);

            Pagination.PageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedGame));
            };

            Games.CollectionChanged += (_, _) =>
            {
                if (!_isBulkLoading)
                {
                    Pagination.SetSource(Games);
                }
            };

            AppUiSettings settings = uiSettingsStore?.Load() ?? AppUiSettings.Default;
            var hidden = new HashSet<string>(
                settings.HiddenInstalledGameColumns,
                StringComparer.Ordinal);

            ColumnOptions = new ObservableCollection<InstalledGameColumnOption>(
                settings.InstalledGameColumnOrder.Select(key =>
                    new InstalledGameColumnOption(
                        key,
                        GetColumnHeader(key),
                        !hidden.Contains(key))));

            foreach (InstalledGameColumnOption option in ColumnOptions)
                option.PropertyChanged += OnColumnOptionPropertyChanged;
        }

        public void SetColumnOrder(IReadOnlyList<string> columnKeys)
        {
            IReadOnlyList<string> normalized =
                AppUiSettings.NormalizeInstalledGameColumnOrder(columnKeys);

            for (int index = 0; index < normalized.Count; index++)
            {
                InstalledGameColumnOption option =
                    ColumnOptions.Single(item => item.Key == normalized[index]);
                int currentIndex = ColumnOptions.IndexOf(option);

                if (currentIndex != index)
                    ColumnOptions.Move(currentIndex, index);
            }

            SaveColumnSettings();
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

                _isBulkLoading = true;
                try
                {
                    Games.Clear();

                    foreach (InstalledGameSaveStatus status in statuses)
                        Games.Add(new InstalledGameRowViewModel(status));
                }
                finally
                {
                    _isBulkLoading = false;
                }

                Pagination.SetSource(Games);

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

        public void SortBy(string? sortMemberPath, bool? ascending)
        {
            if (string.IsNullOrWhiteSpace(sortMemberPath) || ascending is null)
            {
                Pagination.ApplySort(null);
                return;
            }

            Func<InstalledGameRowViewModel, object> keySelector = sortMemberPath switch
            {
                "GameName" => game => game.GameName,
                "AppId" => game => game.AppId,
                "InstallPath" => game => game.InstallPath,
                "LibraryPath" => game => game.LibraryPath,
                "ApprovedMappings" => game => game.ApprovedMappings,
                "PendingMappings" => game => game.PendingMappings,
                "NeedsFixMappings" => game => game.NeedsFixMappings,
                "SavePathExists" => game => game.SavePathExists,
                "FileCount" => game => game.FileCount,
                "Status" => game => game.Status,
                _ => game => game.GameName,
            };

            if (ascending.Value)
            {
                Pagination.ApplySort(items => items.OrderBy(keySelector));
            }
            else
            {
                Pagination.ApplySort(items => items.OrderByDescending(keySelector));
            }
        }

        private void OnColumnOptionPropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InstalledGameColumnOption.IsVisible))
                SaveColumnSettings();
        }

        private void SaveColumnSettings()
        {
            if (_uiSettingsStore is null)
                return;

            AppUiSettings settings = _uiSettingsStore.Load();
            _uiSettingsStore.Save(settings with
            {
                SchemaVersion = AppUiSettings.CurrentSchemaVersion,
                InstalledGameColumnOrder = ColumnOptions
                    .Select(option => option.Key)
                    .ToArray(),
                HiddenInstalledGameColumns = ColumnOptions
                    .Where(option => !option.IsVisible)
                    .Select(option => option.Key)
                    .ToArray(),
            });
        }

        private static string GetColumnHeader(string key) => key switch
        {
            AppUiSettings.GameColumn => "Game",
            AppUiSettings.AppIdColumn => "AppID",
            AppUiSettings.InstallPathColumn => "Install path",
            AppUiSettings.LibraryColumn => "Library",
            AppUiSettings.ApprovedColumn => "Approved",
            AppUiSettings.PendingColumn => "Pending",
            AppUiSettings.NeedsFixColumn => "Needs fix",
            AppUiSettings.ExistsColumn => "Exists",
            AppUiSettings.FilesColumn => "Files",
            AppUiSettings.StatusColumn => "Status",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }
}
