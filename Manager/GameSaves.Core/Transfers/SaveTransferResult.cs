namespace GameSaves.Core.Transfers
{
    public sealed record SaveTransferResult(
        TransferPreviewPlan Plan,
        bool DryRun,
        int FilesConsidered,
        int FilesCopied,
        int FilesSkipped,
        long BytesCopied,
        IReadOnlyList<SaveTransferItemResult> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings)
    {
        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error) ||
            Items.Any(item => item.Status == SaveTransferItemStatus.Failed);
    }
}