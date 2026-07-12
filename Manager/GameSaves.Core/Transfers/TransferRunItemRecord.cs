namespace GameSaves.Core.Transfers
{
    public sealed record TransferRunItemRecord(
        string SourceFile,
        string TargetFile,
        long Bytes,
        bool Copied,
        string Status,
        string? Error,
        string? BackupFile);
}
