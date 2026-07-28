using GameSaves.Core.Sync;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Diagnostics;
using System.Net;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal sealed record GoogleDriveFolderMetadata(
        string? Id,
        string? Name,
        string? MimeType,
        bool Trashed,
        IReadOnlyList<string> ParentIds,
        string? DriveId);

    internal enum GoogleDriveRootFolderApiOperation
    {
        Unknown = 0,
        RootFolderInspection = 1,
        RootFolderDiscovery = 2,
        RootFolderTopLevelMembership = 3,
        RootFolderCreation = 4
    }

    internal enum GoogleDriveRootFolderApiFailure
    {
        InvalidRequest,
        InvalidQuery,
        AuthorizationRevoked,
        InsufficientScope,
        AccessDenied,
        ApiNotEnabled,
        NotFound,
        RateLimited,
        QuotaExceeded,
        Unavailable,
        PersistenceFailed,
        Failed
    }

    /// <summary>
    /// Sanitized provider diagnostics. The reason is selected from a fixed
    /// allowlist and never includes request URLs, response bodies, IDs, or
    /// account and OAuth data.
    /// </summary>
    internal sealed record GoogleDriveApiFailureDetails(
        GoogleDriveRootFolderApiOperation Operation,
        HttpStatusCode? HttpStatus,
        string? Reason,
        GoogleDriveRootFolderApiFailure Failure,
        string SafeErrorCode,
        bool Retryable)
    {
        public override string ToString() =>
            $"{Operation} / {(HttpStatus is null ? "none" : ((int)HttpStatus).ToString())} / " +
            $"{Reason ?? "none"} / {SafeErrorCode} / retryable={Retryable}";
    }

    internal sealed class GoogleDriveRootFolderApiException : Exception
    {
        public GoogleDriveRootFolderApiException(
            GoogleDriveRootFolderApiFailure failure)
            : this(new GoogleDriveApiFailureDetails(
                GoogleDriveRootFolderApiOperation.Unknown,
                null,
                null,
                failure,
                SafeErrorCode(failure),
                failure is GoogleDriveRootFolderApiFailure.RateLimited or
                    GoogleDriveRootFolderApiFailure.Unavailable))
        {
        }

        public GoogleDriveRootFolderApiException(
            GoogleDriveApiFailureDetails details)
            : base("The Google Drive root-folder request did not complete.") =>
            Details = details;

        public GoogleDriveApiFailureDetails Details { get; }

        public GoogleDriveRootFolderApiFailure Failure => Details.Failure;

        internal static string SafeErrorCode(GoogleDriveRootFolderApiFailure failure) =>
            failure switch
            {
                GoogleDriveRootFolderApiFailure.InvalidRequest =>
                    GoogleDriveRootFolderErrorCodes.InvalidRequest,
                GoogleDriveRootFolderApiFailure.InvalidQuery =>
                    GoogleDriveRootFolderErrorCodes.InvalidQuery,
                GoogleDriveRootFolderApiFailure.AuthorizationRevoked =>
                    GoogleDriveRootFolderErrorCodes.AuthenticationRequired,
                GoogleDriveRootFolderApiFailure.InsufficientScope =>
                    GoogleDriveRootFolderErrorCodes.InsufficientScope,
                GoogleDriveRootFolderApiFailure.AccessDenied =>
                    GoogleDriveRootFolderErrorCodes.AccessDenied,
                GoogleDriveRootFolderApiFailure.ApiNotEnabled =>
                    GoogleDriveRootFolderErrorCodes.ApiNotEnabled,
                GoogleDriveRootFolderApiFailure.NotFound =>
                    GoogleDriveRootFolderErrorCodes.Missing,
                GoogleDriveRootFolderApiFailure.RateLimited =>
                    GoogleDriveRootFolderErrorCodes.RateLimited,
                GoogleDriveRootFolderApiFailure.QuotaExceeded =>
                    GoogleDriveRootFolderErrorCodes.QuotaExceeded,
                GoogleDriveRootFolderApiFailure.Unavailable =>
                    GoogleDriveRootFolderErrorCodes.Unavailable,
                GoogleDriveRootFolderApiFailure.PersistenceFailed =>
                    GoogleDriveRootFolderErrorCodes.PersistenceFailed,
                _ => GoogleDriveRootFolderErrorCodes.Failed
            };
    }

    internal interface IGoogleDriveRootFolderApi
    {
        Task<GoogleDriveFolderMetadata> GetFolderByIdAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken);

        Task<bool> IsDirectChildOfMyDriveRootAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<GoogleDriveFolderMetadata>> FindTopLevelFoldersByNameAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken);

        Task<GoogleDriveFolderMetadata> CreateTopLevelFolderAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken);
    }

    internal sealed class GoogleDriveRootFolderApi : IGoogleDriveRootFolderApi
    {
        internal const string MetadataFields =
            "id,name,mimeType,trashed,driveId";
        internal const string DiscoveryFields =
            "nextPageToken,incompleteSearch,files(id,name,mimeType,trashed,driveId)";
        internal const string MembershipFields =
            "nextPageToken,incompleteSearch,files(id)";
        internal const string DiscoveryQuery =
            "name = 'GameSave Manager Backups' and " +
            "mimeType = 'application/vnd.google-apps.folder' and " +
            "trashed = false and 'root' in parents";
        internal const string MembershipQuery =
            "trashed = false and 'root' in parents";

        private static readonly HashSet<string> SafeReasons =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "accessNotConfigured",
                "activeItemCreationLimitExceeded",
                "authError",
                "backendError",
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

        public async Task<GoogleDriveFolderMetadata> GetFolderByIdAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken)
        {
            try
            {
                using DriveService drive = CreateDriveService(credential);
                FilesResource.GetRequest request = drive.Files.Get(folderId);
                request.Fields = MetadataFields;
                // A stored authoritative ID may now resolve into a shared drive.
                // Read it so the service can reject that unsupported location.
                request.SupportsAllDrives = true;
                DriveFile file = await request.ExecuteAsync(cancellationToken);
                return Map(file);
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveRootFolderApiOperation.RootFolderInspection);
            }
        }

        public async Task<bool> IsDirectChildOfMyDriveRootAsync(
            GoogleAuthorizedCredential credential,
            string folderId,
            CancellationToken cancellationToken)
        {
            try
            {
                using DriveService drive = CreateDriveService(credential);
                string? pageToken = null;

                do
                {
                    FilesResource.ListRequest request = drive.Files.List();
                    request.Q = MembershipQuery;
                    request.Spaces = "drive";
                    request.Corpora = "user";
                    request.IncludeItemsFromAllDrives = false;
                    request.SupportsAllDrives = false;
                    request.Fields = MembershipFields;
                    request.PageToken = pageToken;

                    Google.Apis.Drive.v3.Data.FileList page =
                        await request.ExecuteAsync(cancellationToken);

                    if (page.IncompleteSearch == true)
                    {
                        throw Failure(
                            GoogleDriveRootFolderApiOperation.RootFolderTopLevelMembership,
                            GoogleDriveRootFolderApiFailure.Unavailable,
                            retryable: true);
                    }

                    if (page.Files?.Any(file =>
                            string.Equals(file.Id, folderId, StringComparison.Ordinal)) == true)
                    {
                        return true;
                    }

                    pageToken = string.IsNullOrWhiteSpace(page.NextPageToken)
                        ? null
                        : page.NextPageToken;
                }
                while (pageToken is not null);

                return false;
            }
            catch (Exception ex)
            {
                throw MapException(
                    ex,
                    GoogleDriveRootFolderApiOperation.RootFolderTopLevelMembership);
            }
        }

        public async Task<IReadOnlyList<GoogleDriveFolderMetadata>>
            FindTopLevelFoldersByNameAsync(
                GoogleAuthorizedCredential credential,
                CancellationToken cancellationToken)
        {
            try
            {
                using DriveService drive = CreateDriveService(credential);
                var folders = new List<GoogleDriveFolderMetadata>();
                string? pageToken = null;

                do
                {
                    FilesResource.ListRequest request = drive.Files.List();
                    request.Q = DiscoveryQuery;
                    request.Spaces = "drive";
                    request.Corpora = "user";
                    request.IncludeItemsFromAllDrives = false;
                    request.SupportsAllDrives = false;
                    request.Fields = DiscoveryFields;
                    request.PageToken = pageToken;

                    Google.Apis.Drive.v3.Data.FileList page =
                        await request.ExecuteAsync(cancellationToken);

                    if (page.IncompleteSearch == true)
                    {
                        throw Failure(
                            GoogleDriveRootFolderApiOperation.RootFolderDiscovery,
                            GoogleDriveRootFolderApiFailure.Unavailable,
                            retryable: true);
                    }

                    if (page.Files is not null)
                        folders.AddRange(page.Files.Select(Map));

                    pageToken = string.IsNullOrWhiteSpace(page.NextPageToken)
                        ? null
                        : page.NextPageToken;
                }
                while (pageToken is not null);

                return folders;
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveRootFolderApiOperation.RootFolderDiscovery);
            }
        }

        public async Task<GoogleDriveFolderMetadata> CreateTopLevelFolderAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            try
            {
                using DriveService drive = CreateDriveService(credential);
                var metadata = new DriveFile
                {
                    Name = GoogleDriveApplicationRoot.DisplayName,
                    MimeType = GoogleDriveApplicationRoot.FolderMimeType,
                    Parents = new[] { "root" }
                };
                FilesResource.CreateRequest request = drive.Files.Create(metadata);
                request.Fields = MetadataFields;
                DriveFile created = await request.ExecuteAsync(cancellationToken);
                return Map(created);
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveRootFolderApiOperation.RootFolderCreation);
            }
        }

        private static DriveService CreateDriveService(
            GoogleAuthorizedCredential credential) =>
            new(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential.Credential,
                ApplicationName = "Game Save Manager"
            });

        private static GoogleDriveFolderMetadata Map(DriveFile file) => new(
            file.Id,
            file.Name,
            file.MimeType,
            file.Trashed ?? false,
            file.Parents?.ToArray() ?? Array.Empty<string>(),
            file.DriveId);

        internal static GoogleDriveRootFolderApiException MapException(
            Exception exception,
            GoogleDriveRootFolderApiOperation operation)
        {
            if (exception is OperationCanceledException)
                throw exception;

            if (exception is GoogleDriveRootFolderApiException known)
                return known;

            GoogleDriveApiFailureDetails details;

            if (exception is GoogleApiException google)
            {
                HttpStatusCode? status = google.HttpStatusCode;
                string? reason = SafeReason(google);
                GoogleDriveRootFolderApiFailure failure = Classify(status, reason);
                details = new GoogleDriveApiFailureDetails(
                    operation,
                    status,
                    reason,
                    failure,
                    GoogleDriveRootFolderApiException.SafeErrorCode(failure),
                    IsRetryable(failure));
            }
            else
            {
                GoogleDriveRootFolderApiFailure failure =
                    exception is HttpRequestException or TimeoutException
                        ? GoogleDriveRootFolderApiFailure.Unavailable
                        : GoogleDriveRootFolderApiFailure.Failed;
                details = new GoogleDriveApiFailureDetails(
                    operation,
                    null,
                    null,
                    failure,
                    GoogleDriveRootFolderApiException.SafeErrorCode(failure),
                    IsRetryable(failure));
            }

            Trace.TraceWarning("Google Drive root-folder request failed: {0}", details);
            return new GoogleDriveRootFolderApiException(details);
        }

        private static GoogleDriveRootFolderApiException Failure(
            GoogleDriveRootFolderApiOperation operation,
            GoogleDriveRootFolderApiFailure failure,
            bool retryable) =>
            new(new GoogleDriveApiFailureDetails(
                operation,
                null,
                null,
                failure,
                GoogleDriveRootFolderApiException.SafeErrorCode(failure),
                retryable));

        private static GoogleDriveRootFolderApiFailure Classify(
            HttpStatusCode? status,
            string? reason)
        {
            if (status == HttpStatusCode.Unauthorized ||
                ReasonIs(reason, "authError", "invalidCredentials"))
            {
                return GoogleDriveRootFolderApiFailure.AuthorizationRevoked;
            }

            if (ReasonIs(reason, "accessNotConfigured", "serviceDisabled"))
                return GoogleDriveRootFolderApiFailure.ApiNotEnabled;

            if (status == HttpStatusCode.BadRequest)
            {
                return ReasonIs(reason, "invalidQuery")
                    ? GoogleDriveRootFolderApiFailure.InvalidQuery
                    : GoogleDriveRootFolderApiFailure.InvalidRequest;
            }

            if (status == HttpStatusCode.NotFound)
                return GoogleDriveRootFolderApiFailure.NotFound;

            if (ReasonIs(reason, "insufficientPermissions"))
                return GoogleDriveRootFolderApiFailure.InsufficientScope;

            if (ReasonIs(
                    reason,
                    "quotaExceeded",
                    "storageQuotaExceeded",
                    "activeItemCreationLimitExceeded"))
            {
                return GoogleDriveRootFolderApiFailure.QuotaExceeded;
            }

            if (status == HttpStatusCode.TooManyRequests ||
                ReasonIs(reason, "rateLimitExceeded", "userRateLimitExceeded"))
            {
                return GoogleDriveRootFolderApiFailure.RateLimited;
            }

            if (status is HttpStatusCode.RequestTimeout or
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout ||
                ReasonIs(reason, "backendError"))
            {
                return GoogleDriveRootFolderApiFailure.Unavailable;
            }

            if (status == HttpStatusCode.Forbidden ||
                ReasonIs(reason, "insufficientFilePermissions", "forbidden"))
            {
                return GoogleDriveRootFolderApiFailure.AccessDenied;
            }

            return GoogleDriveRootFolderApiFailure.Failed;
        }

        private static string? SafeReason(GoogleApiException exception) =>
            exception.Error?.Errors?
                .Select(error => error.Reason)
                .FirstOrDefault(reason =>
                    !string.IsNullOrWhiteSpace(reason) && SafeReasons.Contains(reason));

        private static bool ReasonIs(string? reason, params string[] candidates) =>
            reason is not null && candidates.Contains(reason, StringComparer.OrdinalIgnoreCase);

        private static bool IsRetryable(GoogleDriveRootFolderApiFailure failure) =>
            failure is GoogleDriveRootFolderApiFailure.RateLimited or
                GoogleDriveRootFolderApiFailure.Unavailable;
    }
}
