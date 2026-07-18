using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GameSaves.Reviewer.Models;
using GameSaves.Reviewer.ViewModels;

namespace GameSaves.Reviewer.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            KeyDown += OnWindowKeyDown;
            Loaded += OnWindowLoaded;
        }

        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

        private void OnWindowLoaded(object? sender, RoutedEventArgs e)
        {
            _ = ViewModel?.ReloadAsync();
        }

        private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ViewModel is null || sender is not DataGrid grid)
                return;

            ViewModel.UpdateSelection(
                grid.SelectedItems.OfType<MappingReviewItem>().ToList());
        }

        private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
        {
            ViewModel?.OpenSelectedSourceCommand.Execute(null);
        }

        // Review shortcuts use bare letter keys for speed, so they are handled
        // here rather than as window KeyBindings: bare keys must not fire
        // while the user is typing in a text box.
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel is null)
                return;

            if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ViewModel.ReloadCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
                return;

            switch (e.Key)
            {
                case Key.A:
                    ViewModel.ApproveSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.R:
                    ViewModel.RejectSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.F:
                    ViewModel.MarkNeedsFixSelectedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.O:
                    ViewModel.OpenSelectedSourceCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}
