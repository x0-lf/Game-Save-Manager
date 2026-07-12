namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// One executed run (transfer copy, restore, or manual backup) to be
    /// persisted into the transfer_runs / transfer_items history tables.
    /// </summary>
    public sealed record TransferRunRecord(
        TransferRunKind Kind,
        string GameName,
        string SteamAppId,
        string SourceAccountId,
        string TargetAccountId,
        bool DryRun,
        bool OverwriteEnabled,
        bool BackupEnabled,
        int FilesConsidered,
        int FilesCopied,
        int FilesSkipped,
        int FilesFailed,
        long BytesCopied,
        int FilesBackedUp,
        string? BackupRootPath,
        string? BlockedReason,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        IReadOnlyList<TransferRunItemRecord> Items);
}
