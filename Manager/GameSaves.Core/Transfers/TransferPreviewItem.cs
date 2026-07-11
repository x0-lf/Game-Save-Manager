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

        // A blocked item is never copied. By default it blocks the whole plan;
        // the user can opt in to skipping blocked items and copying the rest.
        public bool IsBlocked =>
            ConflictStatus is TransferConflictStatus.SameSourceAndTarget
                or TransferConflictStatus.OutsideExpectedRoot
                or TransferConflictStatus.Error;

        public bool IsCopyable => SourceExists && !IsBlocked;
    }
}
