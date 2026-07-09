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
    }
}