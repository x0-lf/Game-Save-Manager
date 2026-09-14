namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Exports a backup run as a single self-contained archive (ZIP or 7-Zip) and imports
    /// such an archive back into the application backup base, where it becomes
    /// visible and restorable again. Export never modifies the run; import
    /// never overwrites anything and rewrites the manifest's backup-file paths
    /// to the extracted location, verifying each against the extracted files.
    /// </summary>
    public interface IBackupArchiveService
    {
        Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken);

        Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            BackupContainerFormat format = BackupContainerFormat.Zip,
            BackupCompressionPreset preset = BackupCompressionPreset.Optimal,
            CancellationToken cancellationToken = default);

        Task<BackupArchiveImportResult> ImportArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default);
    }
}
