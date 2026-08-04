namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Immutable, Infrastructure-only input for listing ordinary files beneath
    /// one backup-run folder. The folder path is relative to the configured
    /// application root and can never represent that root itself.
    /// </summary>
    internal sealed class GoogleDriveRecursiveFileListingRequest
    {
        public GoogleDriveRecursiveFileListingRequest(
            Guid remoteProfileId,
            GoogleDriveRelativePath folderPath)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A saved remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            ArgumentNullException.ThrowIfNull(folderPath);
            if (folderPath.IsRoot)
            {
                throw new ArgumentException(
                    "A backup-run folder path is required.",
                    nameof(folderPath));
            }

            RemoteProfileId = remoteProfileId;
            FolderPath = folderPath;
        }

        public Guid RemoteProfileId { get; }

        public GoogleDriveRelativePath FolderPath { get; }

        public string CanonicalFolderPath => FolderPath.Canonical;

        public static GoogleDriveRecursiveFileListingRequest Parse(
            Guid remoteProfileId,
            string relativeFolderPath) =>
            new(
                remoteProfileId,
                GoogleDriveRelativePath.Parse(relativeFolderPath));

        public string ToSafeDiagnosticString() =>
            "Google Drive recursive file listing request " +
            $"(segments={FolderPath.Segments.Count})";

        public override string ToString() => ToSafeDiagnosticString();
    }
}
