using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GameSaves.App.Views
{
    public partial class ManualBackupView : UserControl
    {
        public ManualBackupView()
        {
            // No split grid to make responsive any more: the workspace surface
            // owns the options-rail/run-column split and its narrow-window
            // behaviour, so the page no longer wires one up.
            InitializeComponent();
        }

        // View-level navigation: banners and empty states may point the user
        // at the section that unblocks them. Walking to the shell TabControl
        // keeps ViewModels navigation-free and test surfaces untouched.
        private void NavigateToSection(int index)
        {
            if (this.FindAncestorOfType<TabControl>() is { } tabs)
                tabs.SelectedIndex = index;
        }

        private void OnGoToProfilesClick(object? sender, RoutedEventArgs e)
        {
            NavigateToSection(2);
        }
    }
}
