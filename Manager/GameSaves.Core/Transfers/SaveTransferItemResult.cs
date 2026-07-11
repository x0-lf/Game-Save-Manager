namespace GameSaves.Core.Transfers
{
    public sealed record SaveTransferItemResult(
        TransferPreviewItem PreviewItem,
        string SourceFile,
        string TargetFile,
        long Bytes,
        bool Copied,
        SaveTransferItemStatus Status,
        string? Error,
        string? BackupFile = null);
}