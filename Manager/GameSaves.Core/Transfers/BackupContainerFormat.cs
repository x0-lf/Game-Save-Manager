namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// The physical storage format of a backup run container.
    /// Uncompressed folders, ZIPs, and 7z archives share identical catalog models.
    /// </summary>
    public enum BackupContainerFormat
    {
        /// <summary>Uncompressed directory containing manifest.json and a files/ payload tree.</summary>
        Folder = 0,

        /// <summary>Standard ZIP compressed archive containing manifest.json at its root.</summary>
        Zip = 1,

        /// <summary>High-compression 7-Zip LZMA2 archive containing manifest.json at its root.</summary>
        SevenZip = 2
    }
}
