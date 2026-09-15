namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Compression preset levels for archive generation.
    /// Controls trade-offs between compression ratio, CPU expenditure, and creation speed.
    /// </summary>
    public enum BackupCompressionPreset
    {
        /// <summary>
        /// Fastest execution, least CPU. ZIP stores entries uncompressed; 7-Zip has
        /// no stored mode through the managed writer and uses its lowest LZMA level,
        /// so a .7z written with this preset is still compressed.
        /// </summary>
        Store = 0,

        /// <summary>Fast compression. Balanced speed with moderate space savings.</summary>
        Fast = 1,

        /// <summary>Optimal standard compression. Recommended balance of compression ratio and speed.</summary>
        Optimal = 2,

        /// <summary>Maximum high compression (Ultra). Smallest archive size with higher CPU utilization.</summary>
        Ultra = 3
    }
}
