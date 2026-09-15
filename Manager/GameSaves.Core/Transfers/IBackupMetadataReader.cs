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
        /// <param name="allowSidecar">
        /// When true a manifest written beside a 7-Zip archive is preferred, which is how a
        /// remote container is inspected without downloading it. Pass false where the archive
        /// itself is the thing being trusted - during import the payload and its description
        /// have to come from the same file, or an unauthenticated sidecar decides the identity
        /// of a run whose contents came from somewhere else.
        /// </param>
        /// <param name="cancellationToken">
        /// Reading a 7-Zip header parses the whole entry table, which is unbounded work on a
        /// large archive and runs inside operations the user can cancel.
        /// </param>
        bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error,
            bool allowSidecar = true,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to read the root manifest.json and indicates whether it was read from an
        /// external unauthenticated sidecar file rather than from the container itself.
        /// </summary>
        bool TryReadManifest(
            string path,
            out TransferBackupManifest? manifest,
            out string? error,
            out bool isSidecar,
            bool allowSidecar = true,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Probes and builds a TransferBackupRunInfo catalog entry for a folder or archive file.
        /// </summary>
        bool TryBuildRunInfo(
            string path,
            out TransferBackupRunInfo? runInfo,
            out string? error,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads every file in the backup container (folder or archive) and verifies its SHA-256
        /// cryptographic hash byte-for-byte against the manifest.
        /// </summary>
        VerificationStrengthResult VerifyPayloadIntegrity(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously verifies every file in the backup container against its recorded SHA-256 hash.
        /// </summary>
        Task<VerificationStrengthResult> VerifyPayloadIntegrityAsync(
            TransferBackupRunInfo runInfo,
            CancellationToken cancellationToken = default);
    }
}
