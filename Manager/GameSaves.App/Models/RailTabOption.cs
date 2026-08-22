using CommunityToolkit.Mvvm.ComponentModel;

namespace GameSaves.App.Models
{
    // One row of the Settings navigation list: visibility plus position in
    // the rail. Mirrors InstalledGameColumnOption, which the Installed-Games
    // column list already uses for the same live-edit-and-persist pattern.
    public sealed partial class RailTabOption : ObservableObject
    {
        [ObservableProperty]
        private bool isVisible;

        [ObservableProperty]
        private bool canMoveUp;

        [ObservableProperty]
        private bool canMoveDown;

        public RailTabOption(string key, string header, bool isVisible, bool canHide)
        {
            Key = key;
            Header = header;
            CanHide = canHide;
            this.isVisible = isVisible;
        }

        public string Key { get; }

        public string Header { get; }

        // False only for Dashboard and Settings, which are pinned so the rail
        // always contains the operational home and the settings surface
        // itself. The Settings checkbox is disabled for pinned tabs.
        public bool CanHide { get; }
    }
}
