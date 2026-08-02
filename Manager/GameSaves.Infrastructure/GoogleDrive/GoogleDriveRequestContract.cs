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

        public const string TextCreationResponseFields = MetadataFields;

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
