namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// The manifest.json schema written into every backup run folder and read
    /// back by the backup history. Writers and readers share this record.
    /// </summary>
    public sealed record TransferBackupManifest(
        int SchemaVersion,
        string Kind,
        string Game,
        string SteamAppId,
        string SourceAccountId,
        string TargetAccountId,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<TransferOverwriteBackupItem> Items);
}
