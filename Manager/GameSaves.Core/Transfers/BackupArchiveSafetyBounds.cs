namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Safety boundaries and resource limits enforced during backup archive operations.
    /// Defends against Zip bombs, memory exhaustion, unbounded file handles, and directory attacks.
    /// </summary>
    public sealed class BackupArchiveSafetyBounds
    {
        public const int DefaultMaxFileEntries = 100_000;
        public const long DefaultMaxManifestBytes = 16L * 1024 * 1024; // 16 MB
        public const long DefaultMaxTotalUncompressedBytes = 100L * 1024 * 1024 * 1024; // 100 GB
        public const long DefaultMaxSingleFileBytes = 50L * 1024 * 1024 * 1024; // 50 GB

        /// <summary>
        /// Maximum number of file entries permitted in a single archive.
        /// </summary>
        public int MaxFileEntries { get; init; } = DefaultMaxFileEntries;

        /// <summary>
        /// Maximum byte size of the manifest.json file inside an archive or folder.
        /// </summary>
        public long MaxManifestBytes { get; init; } = DefaultMaxManifestBytes;

        /// <summary>
        /// Maximum cumulative uncompressed byte payload across all entries in an archive.
        /// </summary>
        public long MaxTotalUncompressedBytes { get; init; } = DefaultMaxTotalUncompressedBytes;

        /// <summary>
        /// Maximum uncompressed byte size of any single file entry in an archive.
        /// </summary>
        public long MaxSingleFileBytes { get; init; } = DefaultMaxSingleFileBytes;

        /// <summary>
        /// Default global safety bounds instance.
        /// </summary>
        public static readonly BackupArchiveSafetyBounds Default = new();
    }
}
