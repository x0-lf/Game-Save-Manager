namespace GameSaves.Core.Transfers
{
    public sealed record TransferPreviewItem(
        long MappingId,
        string SteamAppId,
        string GameName,
        string MappingTemplate,
        string SourcePath,
        string TargetPath,
        bool SourceExists,
        bool TargetExists,
        int FileCount,
        long TotalBytes,
        TransferConflictStatus ConflictStatus,
        string StatusText);
}