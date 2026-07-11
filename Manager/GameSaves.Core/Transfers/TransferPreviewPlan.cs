using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;

namespace GameSaves.Core.Transfers
{
    public sealed record TransferPreviewPlan(
        SteamGame Game,
        SteamProfile SourceProfile,
        SteamProfile TargetProfile,
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

        public bool HasAnyCopyableSource =>
            Items.Any(item => item.SourceExists);

        public IReadOnlyList<TransferPreviewItem> BlockedItems =>
            Items.Where(item => item.IsBlocked).ToList();

        public bool HasBlockedItems =>
            Items.Any(item => item.IsBlocked);

        // True when the unblocked items alone could be copied safely. Execution
        // still requires the user to explicitly opt in to skipping blocked items.
        public bool CanExecuteSkippingBlockedItems =>
            !HasErrors &&
            Items.Any(item => item.IsCopyable);
    }
}