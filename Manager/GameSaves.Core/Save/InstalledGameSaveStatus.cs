using GameSaves.Core.Steam;

namespace GameSaves.Core.Save
{
    public sealed record InstalledGameSaveStatus(
        SteamGame Game,
        GameSaveStatusKind Status,
        string StatusText,
        int ApprovedMappings,
        int PendingMappings,
        int NeedsFixMappings,
        bool SavePathExists,
        int FileCount,
        long TotalBytes,
        IReadOnlyList <SavePathVerificationResult> VerificationResults,
        string? Error);
}