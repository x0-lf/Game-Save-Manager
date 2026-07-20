using GameSaves.Core.Transfers;

namespace GameSaves.Core.Sync
{
    /// <summary>One backup run as seen by a sync preview.</summary>
    public sealed record SyncItem(
        string RunName,
        SyncItemAction Action,
        bool ExistsLocally,
        bool ExistsRemotely,
        string? LocalPath,
        string? RemotePath,
        string GameName,
        int FileCount,
        long TotalBytes,
        string StatusText);

    public sealed record SyncPlan(
        string ProviderName,
        string RemoteRoot,
        IReadOnlyList<SyncItem> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings,
        bool CanExecute,
        int UploadCount,
        int DownloadCount,
        int InSyncCount,
        int ConflictCount,
        long BytesToUpload,
        long BytesToDownload)
    {
        /// <summary>
        /// True only when the provider-specific remote validation completed
        /// successfully. Later manifest warnings do not change this result.
        /// </summary>
        public bool ProviderValidationSucceeded { get; init; } = true;

        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error);
    }

    public sealed record SyncItemResult(
        SyncItem Item,
        long Bytes,
        SyncItemStatus Status,
        string? Error);

    public sealed record SyncResult(
        SyncPlan Plan,
        bool DryRun,
        int Uploaded,
        int Downloaded,
        int Skipped,
        long BytesCopied,
        IReadOnlyList<SyncItemResult> Items,
        IReadOnlyList<TransferPreviewWarning> Warnings)
    {
        public bool HasErrors =>
            Warnings.Any(warning => warning.Severity == TransferWarningSeverity.Error) ||
            Items.Any(item => item.Status == SyncItemStatus.Failed);
    }

    /// <summary>Live progress of a running sync, reported after every file.</summary>
    public sealed record SyncProgress(
        string RunName,
        string CurrentFile,
        int RunsDone,
        int RunsTotal,
        long BytesDone,
        long BytesTotal);

    /// <summary>
    /// Version-history metadata: one executed sync, appended to the sync log
    /// stored alongside the remote data.
    /// </summary>
    public sealed record SyncLogEntry(
        string DeviceName,
        DateTimeOffset TimestampUtc,
        int Uploaded,
        int Downloaded,
        int Conflicts,
        long BytesCopied,
        IReadOnlyList<string> UploadedRuns,
        IReadOnlyList<string> DownloadedRuns);
}
