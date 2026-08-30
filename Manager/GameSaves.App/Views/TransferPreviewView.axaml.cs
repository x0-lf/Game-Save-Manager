using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GameSaves.App.Views
{
    public partial class TransferPreviewView : UserControl
    {
        public TransferPreviewView()
        {
            InitializeComponent();

            // No responsive split grid any more: the workspace surface owns
            // this page's regions and splitters.
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