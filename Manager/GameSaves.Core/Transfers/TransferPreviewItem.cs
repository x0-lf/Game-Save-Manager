namespace GameSaves.Core.Transfers
{
    public sealed record TransferPreviewItem(
        TransferSourceType SourceType,
        long? MappingId,
        string? MappingTemplate,
        string SteamAppId,
        string GameName,
        string SourceRoot,
        string TargetRoot,
        string SourcePath,
        string TargetPath,
        TransferCopyScope CopyScope,
        bool SourceExists,
        bool TargetExists,
        int FileCount,
        long TotalBytes,
        TransferConflictStatus ConflictStatus,
        string StatusText,
        string ActionText)
    {
        public bool IsSteamUserDataGameFolder =>
            SourceType == TransferSourceType.SteamUserDataGameFolder;
    }
}
