namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Retention policy for cleaning up old backup runs. Only run folders
    /// inside the application backup base with a valid manifest are ever
    /// considered; custom-destination backups are never touched.
    /// </summary>
    public sealed class BackupCleanupOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;

        // The newest N runs are always kept; only runs beyond them are
        // candidates for deletion.
        public int KeepNewestRuns { get; init; } = 10;

        // When set, a candidate is only deleted if it is also older than
        // this many days.
        public int? DeleteOlderThanDays { get; init; }
    }
}
