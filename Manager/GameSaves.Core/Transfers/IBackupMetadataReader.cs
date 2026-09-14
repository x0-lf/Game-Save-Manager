namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Reads backup run metadata and manifests directly without extracting archive payloads.
    /// Supports folders, ZIP archives, and 7-Zip archives under a uniform catalog model.
    /// </summary>
    public interface IBackupMetadataReader
    {
        /// <summary>
        /// Probes a path (directory or file) to detect its backup container format.
        /// </summary>
        BackupContainerFormat DetectContainerFormat(string path);

        /// <summary>
        /// Attempts to read the root manifest.json from an uncompressed folder, ZIP, or 7z archive
        /// without extracting full payloads.
        /// </summary>
        bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error);

        /// <summary>
        /// Probes and builds a TransferBackupRunInfo catalog entry for a folder or archive file.
        /// </summary>
        bool TryBuildRunInfo(
            string path,
            out TransferBackupRunInfo? runInfo,
            out string? error);
    }
}
