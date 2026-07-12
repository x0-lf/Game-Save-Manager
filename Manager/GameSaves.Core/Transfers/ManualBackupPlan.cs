using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;

namespace GameSaves.Core.Transfers
{
    public sealed record ManualBackupPlan(
        SteamGame Game,
        SteamProfile Profile,
        string DestinationRoot,
        IReadOnlyList<TransferPreviewItem> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings,
        bool CanExecute,
        int TotalFiles,
        long TotalBytes)
    {
        public bool HasItems => Items.Count > 0;

        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error);

        public IReadOnlyList<TransferPreviewItem> SteamUserDataItems =>
            Items
                .Where(item => item.SourceType == TransferSourceType.SteamUserDataGameFolder)
                .ToList();

        public IReadOnlyList<TransferPreviewItem> ApprovedMappingItems =>
            Items
                .Where(item => item.SourceType == TransferSourceType.ApprovedMapping)
                .ToList();
    }
}
