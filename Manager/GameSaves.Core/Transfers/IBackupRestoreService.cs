namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Restores the files of one backup run to their original locations.
    /// Restore is copy-only: it never deletes anything, refuses to run without
    /// explicit confirmation, and only overwrites current files when asked to.
    /// </summary>
    public interface IBackupRestoreService
    {
        Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the approved save-path mappings for the backup's Steam AppID
        /// as restore-target candidates, each with its resolved path. Only
        /// approved/enabled mappings are considered.
        /// </summary>
        Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default);
    }
}
