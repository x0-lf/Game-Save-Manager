namespace GameSaves.Core.Transfers
{
    public sealed record BackupCleanupResult(
        bool DryRun,
        int RunsConsidered,
        int RunsDeleted,
        int RunsSkipped,
        long BytesFreed,
        IReadOnlyList<BackupCleanupItemResult> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings)
    {
        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error) ||
            Items.Any(item => item.Status == BackupCleanupItemStatus.Failed);
    }
}
