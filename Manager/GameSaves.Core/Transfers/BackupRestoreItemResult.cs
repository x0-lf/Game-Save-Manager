namespace GameSaves.Core.Transfers
{
    public sealed record BackupRestoreItemResult(
        TransferOverwriteBackupItem BackupItem,
        string TargetFile,
        long Bytes,
        bool Restored,
        BackupRestoreItemStatus Status,
        string? Error,
        string? PreRestoreBackupFile = null);
}
