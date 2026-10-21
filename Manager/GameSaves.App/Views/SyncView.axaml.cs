using Avalonia.Controls;
using GameSaves.App.ViewModels;

namespace GameSaves.App.Views
{
    public partial class SyncView : UserControl
    {
        public SyncView()
        {
            InitializeComponent();

            // The Sync page stays out of startup loading (see AppServices), so
            // its scheduled syncs are read each time the page is shown instead.
            // Reading never changes a task.
            Loaded += (_, _) =>
                (DataContext as SyncViewModel)?.RefreshScheduledSyncsCommand.Execute(null);
        }
    }
}
