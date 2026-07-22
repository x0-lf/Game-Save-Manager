namespace GameSaves.Core.Sync
{
    public static class GoogleDriveAuthorizationScopes
    {
        public const string DriveFile =
            "https://www.googleapis.com/auth/drive.file";

        public static string ValidateRequestedScope(string? requestedScope)
        {
            if (!string.Equals(
                    requestedScope,
                    DriveFile,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The requested Google Drive authorization scope is not supported.",
                    nameof(requestedScope));
            }

            return DriveFile;
        }
    }

    /// <summary>
    /// Explicitly persisted, non-secret Google Drive profile settings.
    /// Profile identity and root-folder metadata remain on SyncRemoteProfile.
    /// </summary>
    public sealed record GoogleDriveSyncRemoteSettings : SyncRemoteProfileSettings
    {
        public const int CurrentSchemaVersion = 1;

        public GoogleDriveSyncRemoteSettings(
            string? accountEmail,
            string requestedScope)
            : base(CurrentSchemaVersion)
        {
            AccountEmail = GoogleDriveConnectionSettingsValidation.NormalizeOptional(
                accountEmail,
                GoogleDriveConnectionSettingsValidation.MaximumAccountEmailLength,
                nameof(accountEmail));
            RequestedScope =
                GoogleDriveAuthorizationScopes.ValidateRequestedScope(requestedScope);
        }

        public string? AccountEmail { get; }

        public string RequestedScope { get; }

        public override string ToString() =>
            $"{nameof(GoogleDriveSyncRemoteSettings)} {{ SchemaVersion = {SchemaVersion}, RequestedScope = {RequestedScope} }}";
    }

    public enum GoogleDriveConnectionStatus
    {
        Unknown = 0,
        NotConfigured = 1,
        Disconnected = 2,
        StoredAuthenticationAvailable = 3,
        Connecting = 4,
        Connected = 5,
        ReauthenticationRequired = 6,
        Unavailable = 7,
        Failed = 8
    }

    /// <summary>
    /// Pure runtime view of Google Drive connection metadata. It never owns or
    /// exposes OAuth token contents, and is not persisted as profile truth.
    /// </summary>
    public sealed record GoogleDriveConnectionSettings
    {
        public GoogleDriveConnectionSettings(
            Guid remoteProfileId,
            string? accountDisplayName,
            string? accountEmail,
            string? rootFolderId,
            string? rootFolderDisplayName,
            string requestedScope,
            GoogleDriveConnectionStatus connectionStatus,
            bool hasStoredToken)
        {
            if (remoteProfileId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A non-empty remote profile ID is required.",
                    nameof(remoteProfileId));
            }

            if (!Enum.IsDefined(typeof(GoogleDriveConnectionStatus), connectionStatus))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(connectionStatus),
                    "The Google Drive connection status is not defined.");
            }

            RemoteProfileId = remoteProfileId;
            AccountDisplayName = GoogleDriveConnectionSettingsValidation.NormalizeOptional(
                accountDisplayName,
                GoogleDriveConnectionSettingsValidation.MaximumAccountDisplayNameLength,
                nameof(accountDisplayName));
            AccountEmail = GoogleDriveConnectionSettingsValidation.NormalizeOptional(
                accountEmail,
                GoogleDriveConnectionSettingsValidation.MaximumAccountEmailLength,
                nameof(accountEmail));
            RootFolderId = GoogleDriveConnectionSettingsValidation.NormalizeOptional(
                rootFolderId,
                GoogleDriveConnectionSettingsValidation.MaximumRootFolderIdLength,
                nameof(rootFolderId));
            RootFolderDisplayName = GoogleDriveConnectionSettingsValidation.NormalizeOptional(
                rootFolderDisplayName,
                GoogleDriveConnectionSettingsValidation.MaximumRootFolderDisplayNameLength,
                nameof(rootFolderDisplayName));
            RequestedScope =
                GoogleDriveAuthorizationScopes.ValidateRequestedScope(requestedScope);
            ConnectionStatus = connectionStatus;
            HasStoredToken = hasStoredToken;
        }

        public Guid RemoteProfileId { get; }

        public string? AccountDisplayName { get; }

        public string? AccountEmail { get; }

        public string? RootFolderId { get; }

        public string? RootFolderDisplayName { get; }

        public string RequestedScope { get; }

        public GoogleDriveConnectionStatus ConnectionStatus { get; }

        public bool HasStoredToken { get; }

        public bool HasAccountMetadata =>
            AccountDisplayName is not null || AccountEmail is not null;

        public bool HasRootFolder => RootFolderId is not null;

        public bool RequiresAuthentication =>
            ConnectionStatus is GoogleDriveConnectionStatus.Disconnected or
                GoogleDriveConnectionStatus.ReauthenticationRequired;

        public override string ToString() =>
            $"{nameof(GoogleDriveConnectionSettings)} {{ ConnectionStatus = {ConnectionStatus}, HasStoredToken = {HasStoredToken} }}";
    }

    public enum GoogleDriveConnectionSettingsResultStatus
    {
        Success = 0,
        ProfileNotFound = 1,
        WrongProviderKind = 2,
        SettingsMissing = 3,
        SettingsCorrupted = 4,
        UnsupportedScope = 5,
        SecretStoreUnavailable = 6,
        Failed = 7
    }

    public static class GoogleDriveConnectionErrorCodes
    {
        public const string InvalidProfileId = "GoogleDriveProfileIdInvalid";
        public const string ProfileNotFound = "GoogleDriveProfileNotFound";
        public const string WrongProviderKind = "GoogleDriveWrongProviderKind";
        public const string SettingsMissing = "GoogleDriveSettingsMissing";
        public const string SettingsCorrupted = "GoogleDriveSettingsCorrupted";
        public const string UnsupportedScope = "GoogleDriveUnsupportedScope";
        public const string SecretStoreUnavailable = "GoogleDriveSecretStoreUnavailable";
        public const string ProviderCatalogUnavailable = "GoogleDriveProviderCatalogUnavailable";
        public const string Failed = "GoogleDriveSettingsFailed";
    }

    public sealed record GoogleDriveConnectionSettingsResult
    {
        private GoogleDriveConnectionSettingsResult(
            GoogleDriveConnectionSettingsResultStatus status,
            GoogleDriveConnectionSettings? settings,
            string? errorCode,
            string? errorMessage)
        {
            Status = status;
            Settings = settings;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public GoogleDriveConnectionSettingsResultStatus Status { get; }

        public GoogleDriveConnectionSettings? Settings { get; }

        public string? ErrorCode { get; }

        public string? ErrorMessage { get; }

        public bool Succeeded =>
            Status == GoogleDriveConnectionSettingsResultStatus.Success &&
            Settings is not null;

        public static GoogleDriveConnectionSettingsResult Success(
            GoogleDriveConnectionSettings settings) =>
            new(
                GoogleDriveConnectionSettingsResultStatus.Success,
                settings ?? throw new ArgumentNullException(nameof(settings)),
                null,
                null);

        public static GoogleDriveConnectionSettingsResult Failure(
            GoogleDriveConnectionSettingsResultStatus status,
            string errorCode,
            string errorMessage)
        {
            if (status == GoogleDriveConnectionSettingsResultStatus.Success)
            {
                throw new ArgumentException(
                    "A failure result cannot use the success status.",
                    nameof(status));
            }

            return new(status, null, errorCode, errorMessage);
        }

        public override string ToString() =>
            ErrorCode is null ? Status.ToString() : $"{Status} ({ErrorCode})";
    }

    public interface IGoogleDriveConnectionSettingsService
    {
        Task<GoogleDriveConnectionSettingsResult> GetAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default);
    }

    internal static class GoogleDriveConnectionSettingsValidation
    {
        public const int MaximumAccountDisplayNameLength = 200;
        public const int MaximumAccountEmailLength = 320;
        public const int MaximumRootFolderIdLength = 1024;
        public const int MaximumRootFolderDisplayNameLength = 255;

        public static string? NormalizeOptional(
            string? value,
            int maximumLength,
            string parameterName)
        {
            string? normalized = value?.Trim();

            if (string.IsNullOrEmpty(normalized))
                return null;

            if (normalized.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"The value cannot exceed {maximumLength} characters.",
                    parameterName);
            }

            return normalized;
        }
    }
}
