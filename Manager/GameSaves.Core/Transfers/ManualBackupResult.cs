namespace GameSaves.Core.Transfers
{
    public sealed record ManualBackupResult(
        ManualBackupPlan Plan,
        bool DryRun,
        int FilesConsidered,
        int FilesBackedUp,
        int FilesSkipped,
        long BytesBackedUp,
        IReadOnlyList<SaveTransferItemResult> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings,
        string? BackupRootPath = null,
        string? ManifestPath = null)
    {
        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error) ||
            Items.Any(item => item.Status == SaveTransferItemStatus.Failed);
    }
}
