namespace GameSaves.Core.Transfers
{
    public sealed record BackupCleanupItemResult(
        TransferBackupRunInfo Run,
        long Bytes,
        bool Deleted,
        BackupCleanupItemStatus Status,
        string? Error);
}
