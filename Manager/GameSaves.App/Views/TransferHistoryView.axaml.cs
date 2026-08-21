using Avalonia.Controls;

namespace GameSaves.App.Views
{
    public partial class TransferHistoryView : UserControl
    {
        public TransferHistoryView()
        {
            InitializeComponent();
            ResponsiveSplitGrid.Attach(SplitGrid, threshold: 760);
        }
    }
}
