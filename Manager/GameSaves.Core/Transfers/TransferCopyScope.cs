namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Describes how the source of a transfer preview item is copied to the target.
    /// </summary>
    public enum TransferCopyScope
    {
        /// <summary>
        /// Enumerate all files recursively under the source root and copy them
        /// into the target root, preserving relative paths. Used for
        /// Steam userdata game folders and directory mappings.
        /// </summary>
        DirectoryContents = 0,

        /// <summary>
        /// Copy exactly one file to the target file path.
        /// </summary>
        SingleFile = 1,

        /// <summary>
        /// Copy the source folder itself as a child of the target root
        /// (reserved for future use).
        /// </summary>
        WholeDirectoryAsDirectory = 2
    }
}
