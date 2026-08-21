using CommunityToolkit.Mvvm.ComponentModel;

namespace GameSaves.App.Models
{
    public sealed partial class InstalledGameColumnOption : ObservableObject
    {
        [ObservableProperty]
        private bool isVisible;

        public InstalledGameColumnOption(string key, string header, bool isVisible)
        {
            Key = key;
            Header = header;
            this.isVisible = isVisible;
        }

        public string Key { get; }

        public string Header { get; }
    }
}
