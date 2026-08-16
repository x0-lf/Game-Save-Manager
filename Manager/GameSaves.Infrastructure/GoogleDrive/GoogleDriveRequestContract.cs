namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Shared, non-secret Google Drive request constants. Query values that
    /// can contain object IDs or names remain owned by GoogleDriveQueryBuilder.
    /// </summary>
    internal static class GoogleDriveRequestContract
    {
        public const string ApplicationName = "Game Save Manager";

        public const string MetadataFields =
            "id,name,mimeType,trashed,parents,driveId";

        public const string ListFields =
            "nextPageToken,incompleteSearch," +
            "files(id,name,mimeType,trashed,parents,driveId)";

        public const string RootValidationMetadataFields =
            "id,name,mimeType,trashed,parents,driveId," +
            "capabilities(canListChildren,canAddChildren)";

        public const string TextContentMetadataFields =
            "id,mimeType,trashed,driveId,size,capabilities(canDownload)";

        // Backup download validates identity, exact name, opaque type, parent,
        // My Drive location, and byte length, and requests nothing else.
        public const string BinaryDownloadMetadataFields =
            "id,name,mimeType,trashed,parents,driveId,size";

        public const string TextCreationResponseFields = MetadataFields;

        public const string TextReplacementMetadataFields =
            "id,mimeType,trashed,driveId";

        public const string TextReplacementResponseFields = "id,driveId";

        public const string DriveSpace = "drive";
        public const string UserCorpus = "user";
        public const string MyDriveRootId = "root";
        public const bool IncludeItemsFromAllDrives = false;
        public const bool SupportsAllDrives = false;

        // Authoritative-ID inspection must be able to identify an object that
        // moved into a shared drive so My Drive-only callers can reject it.
        public const bool AuthoritativeIdLookupSupportsAllDrives = true;
    }
}
