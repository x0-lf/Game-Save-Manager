using Google;
using System.Diagnostics;
using System.Net;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal enum GoogleDriveApiOperation
    {
        Unknown = 0,
        RootFolderInspection = 1,
        RootFolderDiscovery = 2,
        RootFolderTopLevelMembership = 3,
        RootFolderCreation = 4,
        ObjectMetadataGet = 5,
        ObjectChildList = 6,
        ObjectFolderCreation = 7,
        RootValidationMetadataGet = 8,
        TextContentMetadataGet = 9,
        TextContentDownload = 10,
        TextContentCreate = 11
    }

    internal enum GoogleDriveApiFailure
    {
        InvalidRequest = 0,
        InvalidQuery = 1,
        AuthorizationRevoked = 2,
        InsufficientScope = 3,
        AccessDenied = 4,
        ApiNotEnabled = 5,
        NotFound = 6,
        RateLimited = 7,
        QuotaExceeded = 8,
        Unavailable = 9,
        Failed = 10
    }

    /// <summary>
    /// Sanitized provider diagnostics. The reason is selected from a fixed
    /// allowlist and never includes request URLs, response bodies, IDs, or
    /// account and OAuth data.
    /// </summary>
    internal sealed record GoogleDriveApiFailureDetails(
        GoogleDriveApiOperation Operation,
        HttpStatusCode? HttpStatus,
        string? Reason,
        GoogleDriveApiFailure Failure,
        string SafeErrorCode,
        bool Retryable)
    {
        public override string ToString() =>
            $"{Operation} / {(HttpStatus is null ? "none" : ((int)HttpStatus).ToString())} / " +
            $"{Reason ?? "none"} / {SafeErrorCode} / retryable={Retryable}";
    }

    internal sealed class GoogleDriveApiException : Exception
    {
        public GoogleDriveApiException(GoogleDriveApiFailureDetails details)
            : base("The Google Drive API request did not complete.") =>
            Details = details;

        public GoogleDriveApiFailureDetails Details { get; }

        public GoogleDriveApiFailure Failure => Details.Failure;
    }

    /// <summary>
    /// One authoritative classifier for Google Drive status codes and
    /// allowlisted provider reasons. Callers supply only their stable,
    /// operation-specific safe error-code mapping.
    /// </summary>
    internal static class GoogleDriveApiFailureMapper
    {
        private static readonly HashSet<string> SafeReasons =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "accessNotConfigured",
                "activeItemCreationLimitExceeded",
                "authError",
                "backendError",
                "dailyLimitExceeded",
                "forbidden",
                "insufficientFilePermissions",
                "insufficientPermissions",
                "invalidCredentials",
                "invalidQuery",
                "quotaExceeded",
                "rateLimitExceeded",
                "serviceDisabled",
                "storageQuotaExceeded",
                "userRateLimitExceeded"
            };

        public static GoogleDriveApiException Map(
            Exception exception,
            GoogleDriveApiOperation operation,
            Func<GoogleDriveApiFailure, string> safeErrorCode)
        {
            if (exception is OperationCanceledException)
                throw exception;

            if (exception is GoogleDriveApiException known)
                return known;

            GoogleDriveApiFailureDetails details;

            if (exception is GoogleApiException google)
            {
                HttpStatusCode? status = google.HttpStatusCode;
                string? reason = SafeReason(google);
                GoogleDriveApiFailure failure = Classify(status, reason);
                details = new GoogleDriveApiFailureDetails(
                    operation,
                    status,
                    reason,
                    failure,
                    safeErrorCode(failure),
                    IsRetryable(failure));
            }
            else
            {
                GoogleDriveApiFailure failure =
                    exception is HttpRequestException or TimeoutException
                        ? GoogleDriveApiFailure.Unavailable
                        : GoogleDriveApiFailure.Failed;
                details = new GoogleDriveApiFailureDetails(
                    operation,
                    null,
                    null,
                    failure,
                    safeErrorCode(failure),
                    IsRetryable(failure));
            }

            Trace.TraceWarning("Google Drive API request failed: {0}", details);
            return new GoogleDriveApiException(details);
        }

        public static GoogleDriveApiException Create(
            GoogleDriveApiOperation operation,
            GoogleDriveApiFailure failure,
            string safeErrorCode,
            bool? retryable = null) =>
            new(new GoogleDriveApiFailureDetails(
                operation,
                null,
                null,
                failure,
                safeErrorCode,
                retryable ?? IsRetryable(failure)));

        private static GoogleDriveApiFailure Classify(
            HttpStatusCode? status,
            string? reason)
        {
            if (status == HttpStatusCode.Unauthorized ||
                ReasonIs(reason, "authError", "invalidCredentials"))
            {
                return GoogleDriveApiFailure.AuthorizationRevoked;
            }

            if (ReasonIs(reason, "accessNotConfigured", "serviceDisabled"))
                return GoogleDriveApiFailure.ApiNotEnabled;

            if (status == HttpStatusCode.BadRequest)
            {
                return ReasonIs(reason, "invalidQuery")
                    ? GoogleDriveApiFailure.InvalidQuery
                    : GoogleDriveApiFailure.InvalidRequest;
            }

            if (status == HttpStatusCode.NotFound)
                return GoogleDriveApiFailure.NotFound;

            if (ReasonIs(reason, "insufficientPermissions"))
                return GoogleDriveApiFailure.InsufficientScope;

            if (ReasonIs(
                    reason,
                    "quotaExceeded",
                    "storageQuotaExceeded",
                    "activeItemCreationLimitExceeded",
                    "dailyLimitExceeded"))
            {
                return GoogleDriveApiFailure.QuotaExceeded;
            }

            if (status == HttpStatusCode.TooManyRequests ||
                ReasonIs(reason, "rateLimitExceeded", "userRateLimitExceeded"))
            {
                return GoogleDriveApiFailure.RateLimited;
            }

            if (status is HttpStatusCode.RequestTimeout or
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout ||
                ReasonIs(reason, "backendError"))
            {
                return GoogleDriveApiFailure.Unavailable;
            }

            if (status == HttpStatusCode.Forbidden ||
                ReasonIs(reason, "insufficientFilePermissions", "forbidden"))
            {
                return GoogleDriveApiFailure.AccessDenied;
            }

            return GoogleDriveApiFailure.Failed;
        }

        private static string? SafeReason(GoogleApiException exception) =>
            exception.Error?.Errors?
                .Select(error => error.Reason)
                .FirstOrDefault(reason =>
                    !string.IsNullOrWhiteSpace(reason) && SafeReasons.Contains(reason));

        private static bool ReasonIs(string? reason, params string[] candidates) =>
            reason is not null && candidates.Contains(reason, StringComparer.OrdinalIgnoreCase);

        private static bool IsRetryable(GoogleDriveApiFailure failure) =>
            failure is GoogleDriveApiFailure.RateLimited or
                GoogleDriveApiFailure.Unavailable;
    }
}
