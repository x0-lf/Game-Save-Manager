using System.Diagnostics;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Fixed sanitized categories for every failure that can escape one binary
    /// download. The category is the only classification callers receive.
    /// </summary>
    internal enum GoogleDriveDownloadFailureCategory
    {
        InvalidRequest = 0,
        DestinationUnavailable = 1,
        SourceUnavailable = 2,
        InvalidCompletion = 3,
        ReauthenticationRequired = 4,
        AccessDenied = 5,
        RateLimited = 6,
        QuotaExceeded = 7,
        Unavailable = 8,
        Cancelled = 9,
        Failed = 10
    }

    internal static class GoogleDriveDownloadErrorCodes
    {
        public const string InvalidRequest =
            GoogleDriveBinaryDownloadErrorCodes.InvalidSourcePath;
        public const string DestinationUnavailable =
            GoogleDriveLocalDownloadDestinationErrorCodes.Failed;
        public const string SourceUnavailable =
            GoogleDriveDownloadSourceErrorCodes.NotFound;
        public const string InvalidCompletion =
            GoogleDriveDownloadCompletionErrorCodes.InvalidMetadata;
        public const string AuthenticationRequired =
            "GoogleDriveDownloadAuthenticationRequired";
        public const string AccessDenied = "GoogleDriveDownloadAccessDenied";
        public const string RateLimited = "GoogleDriveDownloadRateLimited";
        public const string QuotaExceeded = "GoogleDriveDownloadQuotaExceeded";
        public const string Unavailable = "GoogleDriveDownloadUnavailable";
        public const string Cancelled = "GoogleDriveDownloadCancelled";
        public const string Failed =
            GoogleDriveBinaryDownloadErrorCodes.Failed;

        public static string ForCategory(
            GoogleDriveDownloadFailureCategory category) =>
            category switch
            {
                GoogleDriveDownloadFailureCategory.InvalidRequest => InvalidRequest,
                GoogleDriveDownloadFailureCategory.DestinationUnavailable =>
                    DestinationUnavailable,
                GoogleDriveDownloadFailureCategory.SourceUnavailable =>
                    SourceUnavailable,
                GoogleDriveDownloadFailureCategory.InvalidCompletion =>
                    InvalidCompletion,
                GoogleDriveDownloadFailureCategory.ReauthenticationRequired =>
                    AuthenticationRequired,
                GoogleDriveDownloadFailureCategory.AccessDenied => AccessDenied,
                GoogleDriveDownloadFailureCategory.RateLimited => RateLimited,
                GoogleDriveDownloadFailureCategory.QuotaExceeded => QuotaExceeded,
                GoogleDriveDownloadFailureCategory.Unavailable => Unavailable,
                GoogleDriveDownloadFailureCategory.Cancelled => Cancelled,
                GoogleDriveDownloadFailureCategory.Failed => Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
    }

    /// <summary>
    /// Immutable sanitized download failure state: a fixed category, a stable
    /// safe error code, and a fixed message only.
    /// </summary>
    internal sealed record GoogleDriveDownloadFailureDetails(
        GoogleDriveDownloadFailureCategory Category,
        string SafeErrorCode,
        string SafeUserMessage,
        bool Retryable)
    {
        public string ToSafeDiagnosticString() =>
            $"Google Drive download failure: category={Category}; " +
            $"code={SafeErrorCode}; retryable={Retryable}";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// The single download failure boundary. Provider classification is
    /// delegated to <see cref="GoogleDriveApiFailureMapper"/>; this type adds
    /// no second HTTP status or provider-reason classifier. Failures that are
    /// already sanitized keep their own stable stage code.
    /// </summary>
    internal static class GoogleDriveDownloadFailureMapper
    {
        public static GoogleDriveDownloadFailureDetails Classify(
            Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return exception switch
            {
                OperationCanceledException =>
                    Details(GoogleDriveDownloadFailureCategory.Cancelled),
                GoogleDriveLocalDownloadDestinationException destination =>
                    Details(
                        GoogleDriveDownloadFailureCategory.DestinationUnavailable,
                        destination.SafeErrorCode),
                GoogleDriveDownloadCompletionException completion => Details(
                    GoogleDriveDownloadFailureCategory.InvalidCompletion,
                    completion.SafeErrorCode),
                GoogleDriveRemoteOperationException remote =>
                    FromRemoteOperation(remote),
                GoogleDriveRecursiveFileListingException listing =>
                    FromListing(listing),
                GoogleDriveApiException api => FromApi(api),
                ArgumentException =>
                    Details(GoogleDriveDownloadFailureCategory.InvalidRequest),
                _ => FromApi(GoogleDriveApiFailureMapper.Map(
                    exception,
                    GoogleDriveApiOperation.FileMediaDownload,
                    ApiSafeErrorCode))
            };
        }

        /// <summary>
        /// Returns the exception that may escape one download. Cancellation and
        /// already-sanitized failures are preserved; every other failure is
        /// replaced by fixed safe state so no provider or local text escapes.
        /// </summary>
        public static Exception ToSafeException(
            Exception exception,
            GoogleDriveDownloadFailureDetails details)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(details);

            return exception is OperationCanceledException or
                GoogleDriveLocalDownloadDestinationException or
                GoogleDriveDownloadCompletionException or
                GoogleDriveRemoteOperationException or
                GoogleDriveRecursiveFileListingException
                ? exception
                : SafeException(details);
        }

        private static GoogleDriveRemoteOperationException SafeException(
            GoogleDriveDownloadFailureDetails details) =>
            new(new GoogleDriveRemoteValidationResult(
                Status(details.Category),
                details.SafeErrorCode,
                details.SafeUserMessage,
                details.Retryable,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));

        /// <summary>
        /// Builds the sanitized failure for a download result that did not
        /// complete. A non-completed result is never success and never reports
        /// completed bytes.
        /// </summary>
        public static Exception FromIncompleteResult(
            GoogleDriveBinaryDownloadResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Status == GoogleDriveBinaryDownloadStatus.Completed)
            {
                throw new ArgumentException(
                    "A completed download is not a failure.",
                    nameof(result));
            }

            return SafeException(Details(
                GoogleDriveDownloadFailureCategory.Failed,
                result.SafeErrorCode));
        }

        /// <summary>
        /// Builds the sanitized failure for a remote source path that is not
        /// one canonical non-root Google Drive path.
        /// </summary>
        public static Exception InvalidSourcePath() =>
            SafeException(Details(
                GoogleDriveDownloadFailureCategory.InvalidRequest));

        /// <summary>
        /// Records one sanitized lifecycle event. Only fixed stage names,
        /// categories, stable codes, and byte counts are ever written: never a
        /// path, name, identifier, query, URL, or provider response.
        /// </summary>
        public static void Log(
            GoogleDriveDownloadStage stage,
            long bytes = 0,
            GoogleDriveDownloadFailureDetails? failure = null)
        {
            if (bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            if (failure is null)
            {
                Trace.TraceInformation(
                    "Google Drive download: stage={0}; bytes={1}",
                    stage,
                    bytes);
                return;
            }

            Trace.TraceWarning(
                "Google Drive download: stage={0}; bytes={1}; {2}",
                stage,
                bytes,
                failure.ToSafeDiagnosticString());
        }

        private static string ApiSafeErrorCode(GoogleDriveApiFailure failure) =>
            GoogleDriveDownloadErrorCodes.ForCategory(Category(failure));

        private static GoogleDriveDownloadFailureDetails FromApi(
            GoogleDriveApiException exception)
        {
            GoogleDriveDownloadFailureCategory category =
                Category(exception.Details.Failure);
            return Details(
                category,
                string.IsNullOrWhiteSpace(exception.Details.SafeErrorCode)
                    ? null
                    : exception.Details.SafeErrorCode);
        }

        private static GoogleDriveDownloadFailureDetails FromRemoteOperation(
            GoogleDriveRemoteOperationException exception)
        {
            GoogleDriveDownloadFailureCategory? code =
                exception.Result.ErrorCode switch
                {
                    GoogleDriveDownloadSourceErrorCodes.NotFound or
                    GoogleDriveDownloadSourceErrorCodes.Ambiguous or
                    GoogleDriveDownloadSourceErrorCodes.CaseCollision or
                    GoogleDriveDownloadSourceErrorCodes.TypeCollision or
                    GoogleDriveDownloadSourceErrorCodes.UnsupportedObject =>
                        GoogleDriveDownloadFailureCategory.SourceUnavailable,
                    _ => null
                };

            return Details(
                code ?? Category(exception.Result.Status),
                exception.Result.ErrorCode);
        }

        private static GoogleDriveDownloadFailureDetails FromListing(
            GoogleDriveRecursiveFileListingException exception) =>
            Details(
                Category(exception.Result.Status),
                exception.Result.SafeErrorCode);

        private static GoogleDriveDownloadFailureCategory Category(
            GoogleDriveApiFailure failure) =>
            failure switch
            {
                GoogleDriveApiFailure.AuthorizationRevoked or
                GoogleDriveApiFailure.InsufficientScope =>
                    GoogleDriveDownloadFailureCategory.ReauthenticationRequired,
                GoogleDriveApiFailure.AccessDenied =>
                    GoogleDriveDownloadFailureCategory.AccessDenied,
                GoogleDriveApiFailure.NotFound =>
                    GoogleDriveDownloadFailureCategory.SourceUnavailable,
                GoogleDriveApiFailure.RateLimited =>
                    GoogleDriveDownloadFailureCategory.RateLimited,
                GoogleDriveApiFailure.QuotaExceeded =>
                    GoogleDriveDownloadFailureCategory.QuotaExceeded,
                GoogleDriveApiFailure.ApiNotEnabled or
                GoogleDriveApiFailure.Unavailable =>
                    GoogleDriveDownloadFailureCategory.Unavailable,
                _ => GoogleDriveDownloadFailureCategory.Failed
            };

        private static GoogleDriveDownloadFailureCategory Category(
            GoogleDriveRemoteValidationStatus status) =>
            status switch
            {
                GoogleDriveRemoteValidationStatus.UnsupportedScope or
                GoogleDriveRemoteValidationStatus.NotConnected or
                GoogleDriveRemoteValidationStatus.AuthenticationCorrupted or
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked or
                GoogleDriveRemoteValidationStatus.ReauthenticationRequired =>
                    GoogleDriveDownloadFailureCategory.ReauthenticationRequired,
                GoogleDriveRemoteValidationStatus.RootInaccessible or
                GoogleDriveRemoteValidationStatus.RootCannotListChildren or
                GoogleDriveRemoteValidationStatus.RootCannotAddChildren =>
                    GoogleDriveDownloadFailureCategory.AccessDenied,
                GoogleDriveRemoteValidationStatus.RootNotConfigured or
                GoogleDriveRemoteValidationStatus.RootMissing or
                GoogleDriveRemoteValidationStatus.RootTrashed or
                GoogleDriveRemoteValidationStatus.RootWrongType or
                GoogleDriveRemoteValidationStatus.RootMoved or
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation =>
                    GoogleDriveDownloadFailureCategory.SourceUnavailable,
                GoogleDriveRemoteValidationStatus.RateLimited =>
                    GoogleDriveDownloadFailureCategory.RateLimited,
                GoogleDriveRemoteValidationStatus.QuotaExceeded =>
                    GoogleDriveDownloadFailureCategory.QuotaExceeded,
                GoogleDriveRemoteValidationStatus.AuthenticationUnavailable or
                GoogleDriveRemoteValidationStatus.Unavailable =>
                    GoogleDriveDownloadFailureCategory.Unavailable,
                GoogleDriveRemoteValidationStatus.Cancelled =>
                    GoogleDriveDownloadFailureCategory.Cancelled,
                _ => GoogleDriveDownloadFailureCategory.Failed
            };

        private static GoogleDriveDownloadFailureCategory Category(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.InvalidPath or
                GoogleDriveRecursiveFileListingStatus.FolderNotFound or
                GoogleDriveRecursiveFileListingStatus.Ambiguous or
                GoogleDriveRecursiveFileListingStatus.CaseCollision or
                GoogleDriveRecursiveFileListingStatus.TypeCollision or
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject or
                GoogleDriveRecursiveFileListingStatus.TrashedObject or
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation or
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    GoogleDriveDownloadFailureCategory.SourceUnavailable,
                GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired =>
                    GoogleDriveDownloadFailureCategory.ReauthenticationRequired,
                GoogleDriveRecursiveFileListingStatus.AccessDenied =>
                    GoogleDriveDownloadFailureCategory.AccessDenied,
                GoogleDriveRecursiveFileListingStatus.RateLimited =>
                    GoogleDriveDownloadFailureCategory.RateLimited,
                GoogleDriveRecursiveFileListingStatus.QuotaExceeded =>
                    GoogleDriveDownloadFailureCategory.QuotaExceeded,
                GoogleDriveRecursiveFileListingStatus.Unavailable =>
                    GoogleDriveDownloadFailureCategory.Unavailable,
                GoogleDriveRecursiveFileListingStatus.Cancelled =>
                    GoogleDriveDownloadFailureCategory.Cancelled,
                _ => GoogleDriveDownloadFailureCategory.Failed
            };

        private static GoogleDriveRemoteValidationStatus Status(
            GoogleDriveDownloadFailureCategory category) =>
            category switch
            {
                GoogleDriveDownloadFailureCategory.ReauthenticationRequired =>
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveDownloadFailureCategory.AccessDenied =>
                    GoogleDriveRemoteValidationStatus.RootInaccessible,
                GoogleDriveDownloadFailureCategory.RateLimited =>
                    GoogleDriveRemoteValidationStatus.RateLimited,
                GoogleDriveDownloadFailureCategory.QuotaExceeded =>
                    GoogleDriveRemoteValidationStatus.QuotaExceeded,
                GoogleDriveDownloadFailureCategory.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                GoogleDriveDownloadFailureCategory.Cancelled =>
                    GoogleDriveRemoteValidationStatus.Cancelled,
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

        private static GoogleDriveDownloadFailureDetails Details(
            GoogleDriveDownloadFailureCategory category,
            string? stageErrorCode = null) =>
            new(
                category,
                string.IsNullOrWhiteSpace(stageErrorCode)
                    ? GoogleDriveDownloadErrorCodes.ForCategory(category)
                    : stageErrorCode,
                SafeUserMessage(category),
                Retryable(category));

        private static bool Retryable(
            GoogleDriveDownloadFailureCategory category) =>
            category is GoogleDriveDownloadFailureCategory.RateLimited or
                GoogleDriveDownloadFailureCategory.Unavailable;

        private static string SafeUserMessage(
            GoogleDriveDownloadFailureCategory category) =>
            category switch
            {
                GoogleDriveDownloadFailureCategory.InvalidRequest =>
                    "The Google Drive download request is invalid.",
                GoogleDriveDownloadFailureCategory.DestinationUnavailable =>
                    "The local download destination could not be prepared.",
                GoogleDriveDownloadFailureCategory.SourceUnavailable =>
                    "The Google Drive file could not be resolved safely.",
                GoogleDriveDownloadFailureCategory.InvalidCompletion =>
                    "The completed Google Drive download did not validate.",
                GoogleDriveDownloadFailureCategory.ReauthenticationRequired =>
                    "Google Drive must be connected again.",
                GoogleDriveDownloadFailureCategory.AccessDenied =>
                    "Google Drive did not allow the download.",
                GoogleDriveDownloadFailureCategory.RateLimited =>
                    "Google Drive is receiving too many requests. Try again later.",
                GoogleDriveDownloadFailureCategory.QuotaExceeded =>
                    "Google Drive quota prevents completing the download.",
                GoogleDriveDownloadFailureCategory.Unavailable =>
                    "Google Drive is temporarily unavailable.",
                GoogleDriveDownloadFailureCategory.Cancelled =>
                    "The Google Drive download was cancelled. No local file was changed.",
                GoogleDriveDownloadFailureCategory.Failed =>
                    "The Google Drive download could not be completed.",
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };

    }

    /// <summary>
    /// Fixed lifecycle stages used for sanitized download diagnostics.
    /// </summary>
    internal enum GoogleDriveDownloadStage
    {
        Started = 0,
        DestinationPrepared = 1,
        SourceResolved = 2,
        Transferred = 3,
        Validated = 4,
        Placed = 5,
        CleanedUp = 6,
        Cancelled = 7,
        Failed = 8
    }
}
