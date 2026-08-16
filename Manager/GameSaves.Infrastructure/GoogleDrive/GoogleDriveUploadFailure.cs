namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// Fixed sanitized categories for every failure that can escape one binary
    /// upload. The category is the only classification callers receive.
    /// </summary>
    internal enum GoogleDriveUploadFailureCategory
    {
        InvalidSource = 0,
        ParentPreparation = 1,
        TargetCollision = 2,
        InvalidResponse = 3,
        ReauthenticationRequired = 4,
        AccessDenied = 5,
        RateLimited = 6,
        QuotaExceeded = 7,
        Unavailable = 8,
        Cancelled = 9,
        IndeterminateCompletion = 10,
        Failed = 11
    }

    internal static class GoogleDriveUploadErrorCodes
    {
        public const string InvalidTargetPath =
            "GoogleDriveUploadInvalidTargetPath";
        public const string ParentPreparation = "GoogleDriveUploadParentFailed";
        public const string TargetCollision =
            "GoogleDriveUploadTargetCollision";
        public const string AuthenticationRequired =
            "GoogleDriveUploadAuthenticationRequired";
        public const string AccessDenied = "GoogleDriveUploadAccessDenied";
        public const string RateLimited = "GoogleDriveUploadRateLimited";
        public const string QuotaExceeded = "GoogleDriveUploadQuotaExceeded";
        public const string Unavailable = "GoogleDriveUploadUnavailable";
        public const string Cancelled = "GoogleDriveUploadCancelled";

        public static string ForCategory(
            GoogleDriveUploadFailureCategory category) =>
            category switch
            {
                GoogleDriveUploadFailureCategory.InvalidSource =>
                    GoogleDriveLocalUploadSourceErrorCodes.Failed,
                GoogleDriveUploadFailureCategory.ParentPreparation =>
                    ParentPreparation,
                GoogleDriveUploadFailureCategory.TargetCollision =>
                    TargetCollision,
                GoogleDriveUploadFailureCategory.InvalidResponse =>
                    GoogleDriveUploadResponseErrorCodes.InvalidResponse,
                GoogleDriveUploadFailureCategory.ReauthenticationRequired =>
                    AuthenticationRequired,
                GoogleDriveUploadFailureCategory.AccessDenied => AccessDenied,
                GoogleDriveUploadFailureCategory.RateLimited => RateLimited,
                GoogleDriveUploadFailureCategory.QuotaExceeded => QuotaExceeded,
                GoogleDriveUploadFailureCategory.Unavailable => Unavailable,
                GoogleDriveUploadFailureCategory.Cancelled => Cancelled,
                GoogleDriveUploadFailureCategory.IndeterminateCompletion =>
                    GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate,
                GoogleDriveUploadFailureCategory.Failed =>
                    GoogleDriveBinaryUploadErrorCodes.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
    }

    /// <summary>
    /// Immutable sanitized upload failure state. It carries a fixed category,
    /// a stable safe error code, and a fixed message only; no credential,
    /// path, name, identifier, query, session URL, or provider response text
    /// may reach it.
    /// </summary>
    internal sealed record GoogleDriveUploadFailureDetails(
        GoogleDriveUploadFailureCategory Category,
        string SafeErrorCode,
        string SafeUserMessage,
        bool Retryable)
    {
        public string ToSafeDiagnosticString() =>
            $"Google Drive upload failure: category={Category}; " +
            $"code={SafeErrorCode}; retryable={Retryable}";

        public override string ToString() => ToSafeDiagnosticString();
    }

    /// <summary>
    /// The single upload failure boundary. Provider classification is
    /// delegated to <see cref="GoogleDriveApiFailureMapper"/>; this type adds
    /// no second HTTP status or provider-reason classifier. Failures that are
    /// already sanitized keep their own stable stage code.
    /// </summary>
    internal static class GoogleDriveUploadFailureMapper
    {
        public static GoogleDriveUploadFailureDetails Classify(
            Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return exception switch
            {
                OperationCanceledException =>
                    Details(GoogleDriveUploadFailureCategory.Cancelled),
                GoogleDriveLocalUploadSourceException source => Details(
                    GoogleDriveUploadFailureCategory.InvalidSource,
                    source.SafeErrorCode),
                GoogleDriveUploadResponseException response => Details(
                    GoogleDriveUploadFailureCategory.InvalidResponse,
                    response.SafeErrorCode),
                GoogleDriveUploadCompletionIndeterminateException
                    indeterminate => Details(
                        GoogleDriveUploadFailureCategory
                            .IndeterminateCompletion,
                        indeterminate.SafeErrorCode),
                GoogleDriveRemoteOperationException remote =>
                    FromRemoteOperation(remote),
                GoogleDriveRecursiveFileListingException listing =>
                    FromListing(listing),
                GoogleDriveApiException api => FromApi(api),
                _ => FromApi(GoogleDriveApiFailureMapper.Map(
                    exception,
                    GoogleDriveApiOperation.FileMediaUpload,
                    ApiSafeErrorCode))
            };
        }

        /// <summary>
        /// Returns the exception that may escape one upload. Cancellation and
        /// already-sanitized failures are preserved unchanged; every other
        /// failure is replaced by fixed safe state so no provider or local
        /// text can escape.
        /// </summary>
        public static Exception ToSafeException(
            Exception exception,
            GoogleDriveUploadFailureDetails details)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(details);

            return exception is OperationCanceledException or
                GoogleDriveLocalUploadSourceException or
                GoogleDriveUploadResponseException or
                GoogleDriveUploadCompletionIndeterminateException or
                GoogleDriveRemoteOperationException or
                GoogleDriveRecursiveFileListingException
                ? exception
                : SafeException(details);
        }

        private static GoogleDriveRemoteOperationException SafeException(
            GoogleDriveUploadFailureDetails details) =>
            new(new GoogleDriveRemoteValidationResult(
                Status(details.Category),
                details.SafeErrorCode,
                details.SafeUserMessage,
                details.Retryable,
                rootDisplayName: null,
                wasAuthenticationRefreshed: false,
                cacheInvalidated: false));

        /// <summary>
        /// Builds the sanitized failure for an upload result that did not
        /// complete. A non-completed result is never success and never
        /// reports completed bytes.
        /// </summary>
        public static Exception FromIncompleteResult(
            GoogleDriveBinaryUploadResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Status == GoogleDriveBinaryUploadStatus.Completed)
            {
                throw new ArgumentException(
                    "A completed upload is not a failure.",
                    nameof(result));
            }

            GoogleDriveUploadFailureCategory category =
                result.Status == GoogleDriveBinaryUploadStatus.Indeterminate
                    ? GoogleDriveUploadFailureCategory.IndeterminateCompletion
                    : GoogleDriveUploadFailureCategory.Failed;
            return SafeException(Details(category, result.SafeErrorCode));
        }

        /// <summary>
        /// Builds the sanitized failure for a remote target path that is not
        /// one canonical non-root Google Drive path.
        /// </summary>
        public static Exception InvalidTargetPath() =>
            SafeException(Details(
                GoogleDriveUploadFailureCategory.Failed,
                GoogleDriveUploadErrorCodes.InvalidTargetPath));

        private static string ApiSafeErrorCode(GoogleDriveApiFailure failure) =>
            GoogleDriveUploadErrorCodes.ForCategory(Category(failure));

        private static GoogleDriveUploadFailureDetails FromApi(
            GoogleDriveApiException exception)
        {
            GoogleDriveUploadFailureCategory category =
                Category(exception.Details.Failure);
            return Details(
                category,
                string.IsNullOrWhiteSpace(exception.Details.SafeErrorCode)
                    ? null
                    : exception.Details.SafeErrorCode);
        }

        private static GoogleDriveUploadFailureDetails FromRemoteOperation(
            GoogleDriveRemoteOperationException exception)
        {
            GoogleDriveUploadFailureCategory? code =
                exception.Result.ErrorCode switch
                {
                    GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists or
                    GoogleDriveCreateOnlyUploadTargetErrorCodes.CaseCollision or
                    GoogleDriveCreateOnlyUploadTargetErrorCodes.TypeCollision =>
                        GoogleDriveUploadFailureCategory.TargetCollision,
                    GoogleDriveUploadParentPreparationErrorCodes.Ambiguous or
                    GoogleDriveUploadParentPreparationErrorCodes.CaseCollision or
                    GoogleDriveUploadParentPreparationErrorCodes.TypeCollision or
                    GoogleDriveUploadParentPreparationErrorCodes
                        .UnsupportedObject or
                    GoogleDriveUploadParentPreparationErrorCodes
                        .UnsupportedLocation or
                    GoogleDriveUploadParentPreparationErrorCodes
                        .InvalidMetadata or
                    GoogleDriveUploadParentPreparationErrorCodes.CreateFailed or
                    GoogleDriveUploadParentPreparationErrorCodes
                        .InvalidCreateResponse or
                    GoogleDriveUploadParentPreparationErrorCodes.CacheRejected =>
                        GoogleDriveUploadFailureCategory.ParentPreparation,
                    _ => null
                };

            return Details(
                code ?? Category(exception.Result.Status),
                exception.Result.ErrorCode);
        }

        private static GoogleDriveUploadFailureDetails FromListing(
            GoogleDriveRecursiveFileListingException exception) =>
            Details(
                Category(exception.Result.Status),
                exception.Result.SafeErrorCode);

        private static GoogleDriveUploadFailureCategory Category(
            GoogleDriveApiFailure failure) =>
            failure switch
            {
                GoogleDriveApiFailure.AuthorizationRevoked or
                GoogleDriveApiFailure.InsufficientScope =>
                    GoogleDriveUploadFailureCategory.ReauthenticationRequired,
                GoogleDriveApiFailure.AccessDenied =>
                    GoogleDriveUploadFailureCategory.AccessDenied,
                GoogleDriveApiFailure.NotFound =>
                    GoogleDriveUploadFailureCategory.ParentPreparation,
                GoogleDriveApiFailure.RateLimited =>
                    GoogleDriveUploadFailureCategory.RateLimited,
                GoogleDriveApiFailure.QuotaExceeded =>
                    GoogleDriveUploadFailureCategory.QuotaExceeded,
                GoogleDriveApiFailure.ApiNotEnabled or
                GoogleDriveApiFailure.Unavailable =>
                    GoogleDriveUploadFailureCategory.Unavailable,
                _ => GoogleDriveUploadFailureCategory.Failed
            };

        private static GoogleDriveUploadFailureCategory Category(
            GoogleDriveRemoteValidationStatus status) =>
            status switch
            {
                GoogleDriveRemoteValidationStatus.UnsupportedScope or
                GoogleDriveRemoteValidationStatus.NotConnected or
                GoogleDriveRemoteValidationStatus.AuthenticationCorrupted or
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked or
                GoogleDriveRemoteValidationStatus.ReauthenticationRequired =>
                    GoogleDriveUploadFailureCategory.ReauthenticationRequired,
                GoogleDriveRemoteValidationStatus.RootInaccessible or
                GoogleDriveRemoteValidationStatus.RootCannotListChildren or
                GoogleDriveRemoteValidationStatus.RootCannotAddChildren =>
                    GoogleDriveUploadFailureCategory.AccessDenied,
                GoogleDriveRemoteValidationStatus.RootNotConfigured or
                GoogleDriveRemoteValidationStatus.RootMissing or
                GoogleDriveRemoteValidationStatus.RootTrashed or
                GoogleDriveRemoteValidationStatus.RootWrongType or
                GoogleDriveRemoteValidationStatus.RootMoved or
                GoogleDriveRemoteValidationStatus.RootUnsupportedLocation =>
                    GoogleDriveUploadFailureCategory.ParentPreparation,
                GoogleDriveRemoteValidationStatus.RateLimited =>
                    GoogleDriveUploadFailureCategory.RateLimited,
                GoogleDriveRemoteValidationStatus.QuotaExceeded =>
                    GoogleDriveUploadFailureCategory.QuotaExceeded,
                GoogleDriveRemoteValidationStatus.AuthenticationUnavailable or
                GoogleDriveRemoteValidationStatus.Unavailable =>
                    GoogleDriveUploadFailureCategory.Unavailable,
                GoogleDriveRemoteValidationStatus.Cancelled =>
                    GoogleDriveUploadFailureCategory.Cancelled,
                _ => GoogleDriveUploadFailureCategory.Failed
            };

        private static GoogleDriveUploadFailureCategory Category(
            GoogleDriveRecursiveFileListingStatus status) =>
            status switch
            {
                GoogleDriveRecursiveFileListingStatus.Ambiguous or
                GoogleDriveRecursiveFileListingStatus.CaseCollision or
                GoogleDriveRecursiveFileListingStatus.TypeCollision =>
                    GoogleDriveUploadFailureCategory.TargetCollision,
                GoogleDriveRecursiveFileListingStatus.InvalidPath or
                GoogleDriveRecursiveFileListingStatus.FolderNotFound or
                GoogleDriveRecursiveFileListingStatus.UnsupportedObject or
                GoogleDriveRecursiveFileListingStatus.TrashedObject or
                GoogleDriveRecursiveFileListingStatus.UnsupportedLocation or
                GoogleDriveRecursiveFileListingStatus.InvalidMetadata =>
                    GoogleDriveUploadFailureCategory.ParentPreparation,
                GoogleDriveRecursiveFileListingStatus
                    .ReauthenticationRequired =>
                    GoogleDriveUploadFailureCategory.ReauthenticationRequired,
                GoogleDriveRecursiveFileListingStatus.AccessDenied =>
                    GoogleDriveUploadFailureCategory.AccessDenied,
                GoogleDriveRecursiveFileListingStatus.RateLimited =>
                    GoogleDriveUploadFailureCategory.RateLimited,
                GoogleDriveRecursiveFileListingStatus.QuotaExceeded =>
                    GoogleDriveUploadFailureCategory.QuotaExceeded,
                GoogleDriveRecursiveFileListingStatus.Unavailable =>
                    GoogleDriveUploadFailureCategory.Unavailable,
                GoogleDriveRecursiveFileListingStatus.Cancelled =>
                    GoogleDriveUploadFailureCategory.Cancelled,
                _ => GoogleDriveUploadFailureCategory.Failed
            };

        private static GoogleDriveRemoteValidationStatus Status(
            GoogleDriveUploadFailureCategory category) =>
            category switch
            {
                GoogleDriveUploadFailureCategory.ReauthenticationRequired =>
                    GoogleDriveRemoteValidationStatus.ReauthenticationRequired,
                GoogleDriveUploadFailureCategory.AccessDenied =>
                    GoogleDriveRemoteValidationStatus.RootInaccessible,
                GoogleDriveUploadFailureCategory.RateLimited =>
                    GoogleDriveRemoteValidationStatus.RateLimited,
                GoogleDriveUploadFailureCategory.QuotaExceeded =>
                    GoogleDriveRemoteValidationStatus.QuotaExceeded,
                GoogleDriveUploadFailureCategory.Unavailable =>
                    GoogleDriveRemoteValidationStatus.Unavailable,
                GoogleDriveUploadFailureCategory.Cancelled =>
                    GoogleDriveRemoteValidationStatus.Cancelled,
                _ => GoogleDriveRemoteValidationStatus.Failed
            };

        private static GoogleDriveUploadFailureDetails Details(
            GoogleDriveUploadFailureCategory category,
            string? stageErrorCode = null) =>
            new(
                category,
                string.IsNullOrWhiteSpace(stageErrorCode)
                    ? GoogleDriveUploadErrorCodes.ForCategory(category)
                    : stageErrorCode,
                SafeUserMessage(category),
                Retryable(category));

        private static bool Retryable(
            GoogleDriveUploadFailureCategory category) =>
            category is GoogleDriveUploadFailureCategory.RateLimited or
                GoogleDriveUploadFailureCategory.Unavailable;

        private static string SafeUserMessage(
            GoogleDriveUploadFailureCategory category) =>
            category switch
            {
                GoogleDriveUploadFailureCategory.InvalidSource =>
                    "The local backup file could not be prepared for upload.",
                GoogleDriveUploadFailureCategory.ParentPreparation =>
                    "The Google Drive upload folder could not be prepared safely.",
                GoogleDriveUploadFailureCategory.TargetCollision =>
                    "A Google Drive object already uses the upload target name.",
                GoogleDriveUploadFailureCategory.InvalidResponse =>
                    "Google Drive returned an invalid upload response.",
                GoogleDriveUploadFailureCategory.ReauthenticationRequired =>
                    "Google Drive must be connected again.",
                GoogleDriveUploadFailureCategory.AccessDenied =>
                    "Google Drive did not allow the upload.",
                GoogleDriveUploadFailureCategory.RateLimited =>
                    "Google Drive is receiving too many requests. Try again later.",
                GoogleDriveUploadFailureCategory.QuotaExceeded =>
                    "Google Drive quota prevents completing the upload.",
                GoogleDriveUploadFailureCategory.Unavailable =>
                    "Google Drive is temporarily unavailable.",
                GoogleDriveUploadFailureCategory.Cancelled =>
                    "The Google Drive upload was cancelled. No backup data was changed.",
                GoogleDriveUploadFailureCategory.IndeterminateCompletion =>
                    "The Google Drive upload completion is uncertain.",
                GoogleDriveUploadFailureCategory.Failed =>
                    "The Google Drive upload could not be completed.",
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
    }
}
