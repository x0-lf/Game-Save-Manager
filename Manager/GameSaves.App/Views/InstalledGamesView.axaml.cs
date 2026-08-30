using Avalonia.Automation;
using Avalonia.Controls;
using GameSaves.App.Models;
using GameSaves.App.ViewModels;
using System;
using System.Collections.Generic;
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

            // The column chooser hangs off the table itself, so right-clicking
            // a column header — where a table's columns are discovered — opens
            // it. It is built here rather than in XAML because the items must
            // write back to the view model's column options, and a menu built
            // from a Style setter binding cannot be relied on to do that: it
            // would tick and hide nothing, which looks alive and is dead.
            GamesGrid.ContextMenu = new ContextMenu();
            GamesGrid.ContextMenu.Opening += OnColumnMenuOpening;
        }

        // Rebuilt on each open so the ticks match the live column state, which
        // the Settings chooser edits too.
        private void OnColumnMenuOpening(object? sender, CancelEventArgs e)
        {
            if (_viewModel is null || GamesGrid.ContextMenu is not { } menu)
                return;

            var items = new List<Control>();

            foreach (InstalledGameColumnOption option in _viewModel.ColumnOptions)
            {
                var item = new MenuItem
                {
                    Header = option.Header,
                    Icon = new CheckBox
                    {
                        IsChecked = option.IsVisible,
                        IsHitTestVisible = false,
                        Focusable = false,
                    },
                };

                InstalledGameColumnOption captured = option;
                item.Click += (_, _) => captured.IsVisible = !captured.IsVisible;
                AutomationProperties.SetName(
                    item,
                    option.IsVisible
                        ? $"Hide the {option.Header} column"
                        : $"Show the {option.Header} column");

                items.Add(item);
            }

            menu.ItemsSource = items;
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
