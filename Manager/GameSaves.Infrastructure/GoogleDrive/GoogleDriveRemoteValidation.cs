using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Stable Infrastructure-only outcomes for validation of a saved Google
    /// Drive remote. These values describe validation only; they do not make
    /// Google Drive an implemented sync provider.
    /// </summary>
    internal enum GoogleDriveRemoteValidationStatus
    {
        Valid = 0,
        ProfileNotFound = 1,
        WrongProviderKind = 2,
        UnsupportedScope = 3,
        NotConnected = 4,
        AuthenticationUnavailable = 5,
        AuthenticationCorrupted = 6,
        AuthorizationRevoked = 7,
        ReauthenticationRequired = 8,
        RootNotConfigured = 9,
        RootMissing = 10,
        RootTrashed = 11,
        RootWrongType = 12,
        RootMoved = 13,
        RootUnsupportedLocation = 14,
        RootInaccessible = 15,
        RootCannotListChildren = 16,
        RootCannotAddChildren = 17,
        RateLimited = 18,
        QuotaExceeded = 19,
        Unavailable = 20,
        Cancelled = 21,
        Superseded = 22,
        Failed = 23
    }

    internal static class GoogleDriveRemoteValidationErrorCodes
    {
        public const string ProfileNotFound = "GoogleDriveProfileNotFound";
        public const string WrongProvider = "GoogleDriveWrongProvider";
        public const string UnsupportedScope = "GoogleDriveUnsupportedScope";
        public const string NotConnected = "GoogleDriveNotConnected";
        public const string AuthenticationUnavailable =
            "GoogleDriveAuthenticationUnavailable";
        public const string AuthenticationCorrupted =
            "GoogleDriveAuthenticationCorrupted";
        public const string AuthorizationRevoked =
            "GoogleDriveAuthorizationRevoked";
        public const string ReauthenticationRequired =
            "GoogleDriveReauthenticationRequired";
        public const string RootNotConfigured = "GoogleDriveRootNotConfigured";
        public const string RootMissing = "GoogleDriveRootMissing";
        public const string RootTrashed = "GoogleDriveRootTrashed";
        public const string RootWrongType = "GoogleDriveRootWrongType";
        public const string RootMoved = "GoogleDriveRootMoved";
        public const string RootUnsupportedLocation =
            "GoogleDriveRootUnsupportedLocation";
        public const string RootInaccessible = "GoogleDriveRootInaccessible";
        public const string RootCannotListChildren =
            "GoogleDriveRootCannotListChildren";
        public const string RootCannotAddChildren =
            "GoogleDriveRootCannotAddChildren";
        public const string RateLimited = "GoogleDriveRateLimited";
        public const string QuotaExceeded = "GoogleDriveQuotaExceeded";
        public const string Unavailable = "GoogleDriveUnavailable";
        public const string Cancelled = "GoogleDriveValidationCancelled";
        public const string Superseded = "GoogleDriveValidationSuperseded";
        public const string Failed = "GoogleDriveValidationFailed";
    }

    /// <summary>
    /// Immutable, sanitized validation state. RootDisplayName is available to
    /// trusted Infrastructure consumers for display, but is deliberately
    /// omitted from warnings and diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveRemoteValidationResult
    {
        internal GoogleDriveRemoteValidationResult(
            GoogleDriveRemoteValidationStatus status,
            string? errorCode,
            string userMessage,
            bool retryable,
            string? rootDisplayName,
            bool wasAuthenticationRefreshed,
            bool cacheInvalidated)
        {
            if (!Enum.IsDefined(status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (status == GoogleDriveRemoteValidationStatus.Valid &&
                errorCode is not null)
            {
                throw new ArgumentException(
                    "A valid Google Drive remote cannot have an error code.",
                    nameof(errorCode));
            }
            if (status != GoogleDriveRemoteValidationStatus.Valid &&
                string.IsNullOrWhiteSpace(errorCode))
            {
                throw new ArgumentException(
                    "A non-valid Google Drive remote requires a safe error code.",
                    nameof(errorCode));
            }
            if (string.IsNullOrWhiteSpace(userMessage))
                throw new ArgumentException("A safe user message is required.", nameof(userMessage));

            Status = status;
            ErrorCode = errorCode;
            UserMessage = userMessage;
            Retryable = retryable;
            RootDisplayName = string.IsNullOrWhiteSpace(rootDisplayName)
                ? null
                : rootDisplayName;
            WasAuthenticationRefreshed = wasAuthenticationRefreshed;
            CacheInvalidated = cacheInvalidated;
        }

        public GoogleDriveRemoteValidationStatus Status { get; }

        public string? ErrorCode { get; }

        public string UserMessage { get; }

        public bool Retryable { get; }

        public string? RootDisplayName { get; }

        public bool WasAuthenticationRefreshed { get; }

        public bool CacheInvalidated { get; }

        public string ToSafeDiagnosticString() =>
            $"Google Drive remote validation: status={Status}; " +
            $"retryable={Retryable}; " +
            $"authenticationRefreshed={WasAuthenticationRefreshed}; " +
            $"cacheInvalidated={CacheInvalidated}";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// The single translation boundary from existing Google Drive session,
    /// API, root-folder, and object-resolution outcomes into remote validation
    /// state and provider-neutral preview warnings.
    /// </summary>
    internal static class GoogleDriveRemoteValidationMapper
    {
        public static GoogleDriveRemoteValidationResult FromStatus(
            GoogleDriveRemoteValidationStatus status,
            string? rootDisplayName = null,
            bool wasAuthenticationRefreshed = false,
            bool cacheInvalidated = false)
        {
            ValidationDefinition definition = Definition(status);
            return new GoogleDriveRemoteValidationResult(
                status,
                definition.ErrorCode,
                definition.UserMessage,
                definition.Retryable,
                rootDisplayName,
                wasAuthenticationRefreshed,
                cacheInvalidated);
        }

        public static GoogleDriveRemoteValidationResult FromSessionFailure(
            GoogleDriveAuthorizedSessionFailure failure,
            string? rootDisplayName = null,
            bool cacheInvalidated = false)
        {
            if (!Enum.IsDefined(failure))
                throw new ArgumentOutOfRangeException(nameof(failure));

            GoogleDriveRemoteValidationStatus status = failure switch
            {
                GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication =>
                    GoogleDriveRemoteValidationStatus.NotConnected,
                GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable or
                GoogleDriveAuthorizedSessionFailure.ClientConfigurationMissing =>
                    GoogleDriveRemoteValidationStatus.AuthenticationUnavailable,
                GoogleDriveAuthorizedSessionFailure.TokenCorrupted =>
                    GoogleDriveRemoteValidationStatus.AuthenticationCorrupted,
                GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked or
                GoogleDriveAuthorizedSessionFailure.RevokedTokenCleanupFailed =>
                    GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
                GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired =>
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveAuthorizedSessionFailure.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

            return FromStatus(status, rootDisplayName, cacheInvalidated: cacheInvalidated);
        }

        public static GoogleDriveRemoteValidationResult FromApiFailure(
            GoogleDriveApiFailureDetails details,
            string? rootDisplayName = null,
            bool cacheInvalidated = false)
        {
            ArgumentNullException.ThrowIfNull(details);

            GoogleDriveRemoteValidationStatus status = details.Failure switch
            {
                GoogleDriveApiFailure.AuthorizationRevoked =>
                    GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
                GoogleDriveApiFailure.InsufficientScope =>
                    GoogleDriveRemoteValidationStatus.UnsupportedScope,
                GoogleDriveApiFailure.AccessDenied =>
                    GoogleDriveRemoteValidationStatus.RootInaccessible,
                GoogleDriveApiFailure.NotFound =>
                    GoogleDriveRemoteValidationStatus.RootMissing,
                GoogleDriveApiFailure.RateLimited =>
                    GoogleDriveRemoteValidationStatus.RateLimited,
                GoogleDriveApiFailure.QuotaExceeded =>
                    GoogleDriveRemoteValidationStatus.QuotaExceeded,
                GoogleDriveApiFailure.ApiNotEnabled or
                GoogleDriveApiFailure.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

            return FromStatus(status, rootDisplayName, cacheInvalidated: cacheInvalidated);
        }

        public static GoogleDriveRemoteValidationResult FromObjectResolution(
            GoogleDriveObjectResolutionResult resolution,
            string? rootDisplayName = null,
            bool cacheInvalidated = false)
        {
            ArgumentNullException.ThrowIfNull(resolution);

            GoogleDriveRemoteValidationStatus status = resolution.Status switch
            {
                GoogleDriveObjectResolutionStatus.Found =>
                    GoogleDriveRemoteValidationStatus.Valid,
                GoogleDriveObjectResolutionStatus.NotFound =>
                    GoogleDriveRemoteValidationStatus.RootMissing,
                GoogleDriveObjectResolutionStatus.TypeMismatch =>
                    GoogleDriveRemoteValidationStatus.RootWrongType,
                GoogleDriveObjectResolutionStatus.Trashed =>
                    GoogleDriveRemoteValidationStatus.RootTrashed,
                GoogleDriveObjectResolutionStatus.UnsupportedLocation =>
                    GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
                GoogleDriveObjectResolutionStatus.ReauthenticationRequired =>
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveObjectResolutionStatus.AccessDenied =>
                    GoogleDriveRemoteValidationStatus.RootInaccessible,
                GoogleDriveObjectResolutionStatus.RateLimited =>
                    GoogleDriveRemoteValidationStatus.RateLimited,
                GoogleDriveObjectResolutionStatus.QuotaExceeded =>
                    GoogleDriveRemoteValidationStatus.QuotaExceeded,
                GoogleDriveObjectResolutionStatus.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

            return FromStatus(status, rootDisplayName, cacheInvalidated: cacheInvalidated);
        }

        public static GoogleDriveRemoteValidationResult FromRootFolderResult(
            GoogleDriveRootFolderResult rootResult,
            bool cacheInvalidated = false)
        {
            ArgumentNullException.ThrowIfNull(rootResult);

            GoogleDriveRemoteValidationStatus status = rootResult.Status switch
            {
                GoogleDriveRootFolderStatus.Unconfigured =>
                    GoogleDriveRemoteValidationStatus.RootNotConfigured,
                GoogleDriveRootFolderStatus.Ready =>
                    GoogleDriveRemoteValidationStatus.Valid,
                GoogleDriveRootFolderStatus.Moved =>
                    GoogleDriveRemoteValidationStatus.RootMoved,
                GoogleDriveRootFolderStatus.Missing =>
                    GoogleDriveRemoteValidationStatus.RootMissing,
                GoogleDriveRootFolderStatus.Trashed =>
                    GoogleDriveRemoteValidationStatus.RootTrashed,
                GoogleDriveRootFolderStatus.WrongType =>
                    GoogleDriveRemoteValidationStatus.RootWrongType,
                GoogleDriveRootFolderStatus.UnsupportedLocation =>
                    GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
                GoogleDriveRootFolderStatus.ReauthenticationRequired =>
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveRootFolderStatus.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                GoogleDriveRootFolderStatus.RecreationConfirmationRequired =>
                    FromRecreationRequired(rootResult.ErrorCode),
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

            return FromStatus(
                status,
                rootResult.DisplayName,
                cacheInvalidated: cacheInvalidated);
        }

        public static TransferPreviewWarning? ToTransferPreviewWarning(
            GoogleDriveRemoteValidationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.Status == GoogleDriveRemoteValidationStatus.Valid)
                return null;

            TransferWarningSeverity severity =
                result.Status == GoogleDriveRemoteValidationStatus.RootMoved
                    ? TransferWarningSeverity.Warning
                    : TransferWarningSeverity.Error;

            return new TransferPreviewWarning(
                result.ErrorCode!,
                result.UserMessage,
                severity);
        }

        private static GoogleDriveRemoteValidationStatus FromRecreationRequired(
            string? rootErrorCode) =>
            rootErrorCode switch
            {
                GoogleDriveRootFolderErrorCodes.Missing =>
                    GoogleDriveRemoteValidationStatus.RootMissing,
                GoogleDriveRootFolderErrorCodes.Trashed =>
                    GoogleDriveRemoteValidationStatus.RootTrashed,
                GoogleDriveRootFolderErrorCodes.WrongType =>
                    GoogleDriveRemoteValidationStatus.RootWrongType,
                GoogleDriveRootFolderErrorCodes.UnsupportedLocation =>
                    GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
                _ => GoogleDriveRemoteValidationStatus.RootInaccessible
            };

        private static ValidationDefinition Definition(
            GoogleDriveRemoteValidationStatus status) =>
            status switch
            {
                GoogleDriveRemoteValidationStatus.Valid => new(
                    null,
                    "Google Drive validation succeeded.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.ProfileNotFound => new(
                    GoogleDriveRemoteValidationErrorCodes.ProfileNotFound,
                    "The saved Google Drive profile no longer exists.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.WrongProviderKind => new(
                    GoogleDriveRemoteValidationErrorCodes.WrongProvider,
                    "The selected profile is not a Google Drive profile.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.UnsupportedScope => new(
                    GoogleDriveRemoteValidationErrorCodes.UnsupportedScope,
                    "The saved Google Drive profile does not use the required drive.file scope.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.NotConnected => new(
                    GoogleDriveRemoteValidationErrorCodes.NotConnected,
                    "Connect the saved Google Drive account before checking synchronization.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.AuthenticationUnavailable => new(
                    GoogleDriveRemoteValidationErrorCodes.AuthenticationUnavailable,
                    "Google Drive authentication or protected storage is temporarily unavailable.",
                    Retryable: true),
                GoogleDriveRemoteValidationStatus.AuthenticationCorrupted => new(
                    GoogleDriveRemoteValidationErrorCodes.AuthenticationCorrupted,
                    "Stored Google Drive authentication is unreadable. Remove the local authentication and connect again.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked => new(
                    GoogleDriveRemoteValidationErrorCodes.AuthorizationRevoked,
                    "Google Drive authorization is no longer valid. Reconnect the account.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.ReauthenticationRequired => new(
                    GoogleDriveRemoteValidationErrorCodes.ReauthenticationRequired,
                    "Google Drive authentication must be renewed. Reconnect the account.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootNotConfigured => new(
                    GoogleDriveRemoteValidationErrorCodes.RootNotConfigured,
                    "Set up the Google Drive backup folder before checking synchronization.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootMissing => new(
                    GoogleDriveRemoteValidationErrorCodes.RootMissing,
                    "The configured Google Drive backup folder no longer exists or is not accessible.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootTrashed => new(
                    GoogleDriveRemoteValidationErrorCodes.RootTrashed,
                    "The configured Google Drive backup folder is in the trash.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootWrongType => new(
                    GoogleDriveRemoteValidationErrorCodes.RootWrongType,
                    "The configured Google Drive root no longer refers to a folder.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootMoved => new(
                    GoogleDriveRemoteValidationErrorCodes.RootMoved,
                    "The configured Google Drive backup folder was moved within My Drive and remains linked by its Drive identity.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation => new(
                    GoogleDriveRemoteValidationErrorCodes.RootUnsupportedLocation,
                    "The configured Google Drive backup folder is in an unsupported location. Only My Drive is supported.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootInaccessible => new(
                    GoogleDriveRemoteValidationErrorCodes.RootInaccessible,
                    "The configured Google Drive backup folder is not accessible.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootCannotListChildren => new(
                    GoogleDriveRemoteValidationErrorCodes.RootCannotListChildren,
                    "Game Save Manager cannot read the contents of the configured Google Drive folder.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RootCannotAddChildren => new(
                    GoogleDriveRemoteValidationErrorCodes.RootCannotAddChildren,
                    "Game Save Manager cannot create backup folders inside the configured Google Drive folder.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.RateLimited => new(
                    GoogleDriveRemoteValidationErrorCodes.RateLimited,
                    "Google Drive temporarily rate-limited validation. Try again later.",
                    Retryable: true),
                GoogleDriveRemoteValidationStatus.QuotaExceeded => new(
                    GoogleDriveRemoteValidationErrorCodes.QuotaExceeded,
                    "Google Drive reported that the account or project quota has been exceeded.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.Unavailable => new(
                    GoogleDriveRemoteValidationErrorCodes.Unavailable,
                    "Google Drive is temporarily unavailable. Try again later.",
                    Retryable: true),
                GoogleDriveRemoteValidationStatus.Cancelled => new(
                    GoogleDriveRemoteValidationErrorCodes.Cancelled,
                    "Google Drive validation was cancelled. No backup data was changed.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.Superseded => new(
                    GoogleDriveRemoteValidationErrorCodes.Superseded,
                    "Google Drive validation was superseded by a newer operation.",
                    Retryable: false),
                GoogleDriveRemoteValidationStatus.Failed => new(
                    GoogleDriveRemoteValidationErrorCodes.Failed,
                    "Google Drive validation failed. Try again after reviewing the saved profile and connection state.",
                    Retryable: false),
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };

        private sealed record ValidationDefinition(
            string? ErrorCode,
            string UserMessage,
            bool Retryable);
    }
}
