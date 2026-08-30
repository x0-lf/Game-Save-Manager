using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GameSaves.App.Views
{
    public partial class BackupHistoryView : UserControl
    {
        public BackupHistoryView()
        {
            InitializeComponent();

            // The runs/details split is a workspace region boundary now, so
            // the surface owns both the splitter and the narrow-window
            // collapse that ResponsiveSplitGrid used to provide here.
        }

        // View-level navigation: banners and empty states may point the user
        // at the section that unblocks them. Walking to the shell TabControl
        // keeps ViewModels navigation-free and test surfaces untouched.
        private void NavigateToSection(int index)
        {
            if (this.FindAncestorOfType<TabControl>() is { } tabs)
                tabs.SelectedIndex = index;
        }

        private void OnCreateBackupClick(object? sender, RoutedEventArgs e)
        {
            NavigateToSection(4);
        }
    }
}
