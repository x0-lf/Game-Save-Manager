namespace GameSaves.Core.Transfers
{
    public sealed record TransferOverwriteBackupItem(
        string OriginalFile,
        string BackupFile,
        long Bytes,
        string Sha256,
        DateTimeOffset BackedUpUtc);
}
