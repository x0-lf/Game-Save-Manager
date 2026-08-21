using Avalonia.Controls;
using GameSaves.App.Models;
using GameSaves.App.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;

namespace GameSaves.App.Views
{
    public partial class InstalledGamesView : UserControl
    {
        private InstalledGamesViewModel? _viewModel;
        private bool _applyingColumnSettings;

        public InstalledGamesView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_viewModel is not null)
            {
                foreach (InstalledGameColumnOption option in _viewModel.ColumnOptions)
                    option.PropertyChanged -= OnColumnOptionPropertyChanged;
            }

            _viewModel = DataContext as InstalledGamesViewModel;

            if (_viewModel is null)
                return;

            foreach (InstalledGameColumnOption option in _viewModel.ColumnOptions)
                option.PropertyChanged += OnColumnOptionPropertyChanged;

            ApplyColumnSettings();
        }

        private void ApplyColumnSettings()
        {
            if (_viewModel is null)
                return;

            _applyingColumnSettings = true;

            try
            {
                for (int index = 0; index < _viewModel.ColumnOptions.Count; index++)
                {
                    InstalledGameColumnOption option = _viewModel.ColumnOptions[index];
                    DataGridColumn column = FindColumn(option.Key);
                    column.IsVisible = option.IsVisible;
                    column.DisplayIndex = index;
                }
            }
            finally
            {
                _applyingColumnSettings = false;
            }
        }

        private void OnColumnOptionPropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (sender is InstalledGameColumnOption option &&
                e.PropertyName == nameof(InstalledGameColumnOption.IsVisible))
            {
                FindColumn(option.Key).IsVisible = option.IsVisible;
            }
        }

        private void OnColumnReordered(object? sender, DataGridColumnEventArgs e)
        {
            if (_viewModel is null || _applyingColumnSettings)
                return;

            _viewModel.SetColumnOrder(
                GamesGrid.Columns
                    .OrderBy(column => column.DisplayIndex)
                    .Select(column => (string)column.Tag!)
                    .ToArray());
        }

        private DataGridColumn FindColumn(string key)
        {
            return GamesGrid.Columns.Single(column =>
                string.Equals(column.Tag as string, key, StringComparison.Ordinal));
        }
    }
}
