using Avalonia.Controls;

namespace GameSaves.App.Views
{
    public partial class TransferHistoryView : UserControl
    {
        public TransferHistoryView()
        {
            // The runs/files split is now the workspace surface's left and
            // centre regions, so there is no SplitGrid left to make responsive.
            InitializeComponent();
        }
    }
}
