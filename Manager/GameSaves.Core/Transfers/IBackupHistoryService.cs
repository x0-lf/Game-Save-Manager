namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Lists backup runs by reading the manifest.json of every run folder,
    /// newest first. Runs without a readable manifest are skipped.
    /// </summary>
    public interface IBackupHistoryService
    {
        Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// The application backup base directory. Runs written here appear in
        /// the backup history; runs written elsewhere are self-contained but
        /// are not listed.
        /// </summary>
        string GetBackupBasePath();

        /// <summary>
        /// Verifies the cryptographic payload integrity of a backup run.
        /// </summary>
        Task<VerificationStrengthResult> VerifyRunIntegrityAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationStrengthResult(
                run.Verification,
                "Integrity verification not supported by this history provider."));

        /// <summary>
        /// Deletes orphaned ephemeral working directories (.staging_*, .export_*, .download_*)
        /// and temporary export files (.export_*.tmp) older than the specified age (default 1 hour).
        /// </summary>
        void PurgeStaleWorkingDirectories(TimeSpan? olderThan = null) { }
    }
}
