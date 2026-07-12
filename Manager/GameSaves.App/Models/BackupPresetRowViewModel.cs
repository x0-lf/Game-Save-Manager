using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class BackupPresetRowViewModel
    {
        public BackupPresetRowViewModel(ManualBackupPreset preset)
        {
            Preset = preset;
        }

        public ManualBackupPreset Preset { get; }

        public long Id => Preset.Id;

        public string Name => Preset.Name;

        public string DestinationRoot => Preset.DestinationRoot;

        public string SourcesDisplay =>
            (Preset.IncludeSteamUserDataGameFolder, Preset.IncludeApprovedMappings) switch
            {
                (true, true) => "userdata + mappings",
                (true, false) => "userdata only",
                (false, true) => "mappings only",
                _ => "no sources"
            };

        public string ComboDisplay => $"{Name}  ({SourcesDisplay})";

        public override string ToString() => ComboDisplay;
    }
}
