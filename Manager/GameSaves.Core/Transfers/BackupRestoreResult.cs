namespace GameSaves.Core.Transfers
{
    public sealed record BackupRestoreResult(
        TransferBackupRunInfo Run,
        bool DryRun,
        int FilesConsidered,
        int FilesRestored,
        int FilesSkipped,
        long BytesRestored,
        IReadOnlyList<BackupRestoreItemResult> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings,
        int FilesBackedUp = 0,
        string? BackupRootPath = null)
    {
        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error) ||
            Items.Any(item => item.Status == BackupRestoreItemStatus.Failed);
    }
}
