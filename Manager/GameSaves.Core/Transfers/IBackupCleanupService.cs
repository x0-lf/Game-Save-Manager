namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Deletes old backup runs from the application backup base. This is the
    /// only delete operation in the application: it is preview-first, requires
    /// explicit confirmation, only ever removes manifest-bearing run folders
    /// strictly inside the backup base, and never touches save files or
    /// custom-destination backups.
    /// </summary>
    public interface IBackupCleanupService
    {
        /// <summary>Applies the retention policy (keep newest N, optional age rule).</summary>
        Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>Deletes one explicitly chosen backup run.</summary>
        Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default);
    }
}
