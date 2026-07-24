namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Stable metadata for the single visible Google Drive folder managed by
    /// the first Drive integration. The folder ID, once known, is authoritative.
    /// </summary>
    public static class GoogleDriveApplicationRoot
    {
        public const string DisplayName = "GameSave Manager Backups";
        public const string FolderMimeType = "application/vnd.google-apps.folder";
    }

    public enum GoogleDriveRootFolderStatus
    {
        Unconfigured = 0,
        Checking = 1,
        Ready = 2,
        Moved = 3,
        Missing = 4,
        Trashed = 5,
        WrongType = 6,
        UnsupportedLocation = 7,
        Ambiguous = 8,
        RecreationConfirmationRequired = 9,
        Creating = 10,
        ReauthenticationRequired = 11,
        Unavailable = 12,
        Failed = 13
    }

    public enum GoogleDriveRootFolderRecreationConfirmation
    {
        NotConfirmed = 0,
        Confirmed = 1
    }

    public static class GoogleDriveRootFolderErrorCodes
    {
        public const string ProfileNotFound = "GoogleDriveRootProfileNotFound";
        public const string WrongProvider = "GoogleDriveRootWrongProvider";
        public const string NotConnected = "GoogleDriveRootNotConnected";
        public const string AuthenticationRequired = "GoogleDriveRootAuthenticationRequired";
        public const string Missing = "GoogleDriveRootMissing";
        public const string Trashed = "GoogleDriveRootTrashed";
        public const string WrongType = "GoogleDriveRootWrongType";
        public const string UnsupportedLocation = "GoogleDriveRootUnsupportedLocation";
        public const string Ambiguous = "GoogleDriveRootAmbiguous";
        public const string ConfirmationRequired = "GoogleDriveRootConfirmationRequired";
        public const string Unavailable = "GoogleDriveRootUnavailable";
        public const string AccessDenied = "GoogleDriveRootAccessDenied";
        public const string CreationFailed = "GoogleDriveRootCreationFailed";
        public const string PersistenceFailed = "GoogleDriveRootPersistenceFailed";
        public const string OperationInProgress = "GoogleDriveRootOperationInProgress";
        public const string Cancelled = "GoogleDriveRootCancelled";
        public const string Failed = "GoogleDriveRootFailed";
    }

    /// <summary>
    /// Provider-neutral root-folder outcome. It contains non-secret metadata
    /// only and deliberately omits raw provider responses and request details.
    /// </summary>
    public sealed record GoogleDriveRootFolderResult(
        GoogleDriveRootFolderStatus Status,
        Guid RemoteProfileId,
        string? FolderId = null,
        string? DisplayName = null,
        bool WasCreated = false,
        bool WasDiscovered = false,
        bool WasValidatedById = false,
        bool WasMoved = false,
        bool RequiresRecreationConfirmation = false,
        string? ErrorCode = null,
        string? Message = null)
    {
        public bool Succeeded =>
            Status is GoogleDriveRootFolderStatus.Ready or
                GoogleDriveRootFolderStatus.Moved;

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    public interface IGoogleDriveRootFolderService
    {
        Task<GoogleDriveRootFolderResult> InspectAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveRootFolderResult> EnsureAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);

        Task<GoogleDriveRootFolderResult> RecreateAsync(
            Guid remoteProfileId,
            GoogleDriveRootFolderRecreationConfirmation confirmation,
            CancellationToken cancellationToken = default);
    }
}
