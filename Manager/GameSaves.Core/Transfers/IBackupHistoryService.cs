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
    }
}
