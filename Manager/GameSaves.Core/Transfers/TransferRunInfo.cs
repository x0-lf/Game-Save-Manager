namespace GameSaves.Core.Transfers
{
    /// <summary>A persisted run as read back from the transfer_runs table.</summary>
    public sealed record TransferRunInfo(
        long Id,
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
        DateTimeOffset CompletedUtc)
    {
        public bool WasBlocked => !string.IsNullOrWhiteSpace(BlockedReason);
    }
}
