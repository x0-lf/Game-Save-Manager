using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// The minimal remote surface a sync backend must provide. The shared
    /// SyncEngine owns all sync logic; a backend only implements these
    /// primitives. Relative paths use '/' as separator and are relative to
    /// the remote root. Implementations must create parent directories on
    /// write, must never overwrite existing files, and must never delete
    /// anything. Read operations must not create the remote root.
    /// </summary>
    internal interface IRemoteFileSystem
    {
        /// <summary>Human-readable root, used in messages and plan items.</summary>
        string DisplayRoot { get; }

        string GetDisplayPath(string relativePath);

        /// <summary>Backend-specific validation; null when the remote is usable.</summary>
        Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default);

        Task<bool> RootExistsAsync(
            CancellationToken cancellationToken = default);

        /// <summary>Top-level folder names under the remote root.</summary>
        Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default);

        Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default);

        /// <summary>Reads a text file; null when it does not exist.</summary>
        Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        Task WriteTextFileAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default);

        /// <summary>All files under a folder, recursive, relative to that folder.</summary>
        Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default);

        /// <summary>Uploads one file; returns its size in bytes.</summary>
        Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default);

        /// <summary>Downloads one file; returns its size in bytes.</summary>
        Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default);
    }
}
