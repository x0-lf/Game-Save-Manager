using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
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

    internal enum GoogleDriveRootFolderApiFailure
    {
        NotFound,
        AuthorizationRevoked,
        AccessDenied,
        Unavailable,
        Failed
    }

    internal sealed class GoogleDriveRootFolderApiException : Exception
    {
        public GoogleDriveRootFolderApiException(
            GoogleDriveRootFolderApiFailure failure)
            : base("The Google Drive root-folder request did not complete.") =>
            Failure = failure;

        public GoogleDriveRootFolderApiFailure Failure { get; }
    }

    internal interface IGoogleDriveRootFolderApi
    {
        Task<string> GetMyDriveRootIdAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken);

        Task<GoogleDriveFolderMetadata> GetFolderByIdAsync(
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
            "id,name,mimeType,trashed,parents,driveId";
        internal const string DiscoveryFields =
            "nextPageToken,incompleteSearch,files(id,name,mimeType,trashed,parents,driveId)";
        internal const string DiscoveryQuery =
            "name = 'GameSave Manager Backups' and " +
            "mimeType = 'application/vnd.google-apps.folder' and " +
            "trashed = false and 'root' in parents";

        public async Task<string> GetMyDriveRootIdAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            try
            {
                using DriveService drive = CreateDriveService(credential);
                FilesResource.GetRequest request = drive.Files.Get("root");
                request.Fields = "id";
                DriveFile root = await request.ExecuteAsync(cancellationToken);

                return !string.IsNullOrWhiteSpace(root.Id)
                    ? root.Id
                    : throw new GoogleDriveRootFolderApiException(
                        GoogleDriveRootFolderApiFailure.Failed);
            }
            catch (Exception ex)
            {
                throw MapException(ex);
            }
        }

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
                // Read it so the service can reject that unsupported location
                // explicitly; name-based discovery still excludes shared drives.
                request.SupportsAllDrives = true;
                DriveFile file = await request.ExecuteAsync(cancellationToken);
                return Map(file);
            }
            catch (Exception ex)
            {
                throw MapException(ex);
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
                        throw new GoogleDriveRootFolderApiException(
                            GoogleDriveRootFolderApiFailure.Unavailable);
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
                throw MapException(ex);
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
                    Name = GameSaves.Core.Sync.GoogleDriveApplicationRoot.DisplayName,
                    MimeType = GameSaves.Core.Sync.GoogleDriveApplicationRoot.FolderMimeType,
                    Parents = new[] { "root" }
                };
                FilesResource.CreateRequest request = drive.Files.Create(metadata);
                request.Fields = MetadataFields;
                DriveFile created = await request.ExecuteAsync(cancellationToken);
                return Map(created);
            }
            catch (Exception ex)
            {
                throw MapException(ex);
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

        private static Exception MapException(Exception exception)
        {
            if (exception is OperationCanceledException)
                return exception;

            if (exception is GoogleDriveRootFolderApiException)
                return exception;

            if (exception is GoogleApiException google)
            {
                if (google.HttpStatusCode == HttpStatusCode.NotFound)
                {
                    return new GoogleDriveRootFolderApiException(
                        GoogleDriveRootFolderApiFailure.NotFound);
                }

                if (IsConfirmedAuthenticationFailure(google))
                {
                    return new GoogleDriveRootFolderApiException(
                        GoogleDriveRootFolderApiFailure.AuthorizationRevoked);
                }

                if (IsTemporaryFailure(google))
                {
                    return new GoogleDriveRootFolderApiException(
                        GoogleDriveRootFolderApiFailure.Unavailable);
                }

                if (google.HttpStatusCode == HttpStatusCode.Forbidden)
                {
                    return new GoogleDriveRootFolderApiException(
                        GoogleDriveRootFolderApiFailure.AccessDenied);
                }
            }

            return exception is HttpRequestException or TimeoutException
                ? new GoogleDriveRootFolderApiException(
                    GoogleDriveRootFolderApiFailure.Unavailable)
                : new GoogleDriveRootFolderApiException(
                    GoogleDriveRootFolderApiFailure.Failed);
        }

        private static bool IsConfirmedAuthenticationFailure(
            GoogleApiException exception)
        {
            if (exception.HttpStatusCode == HttpStatusCode.Unauthorized)
                return true;

            return exception.HttpStatusCode == HttpStatusCode.Forbidden &&
                exception.Error?.Errors?.Any(error =>
                    string.Equals(
                        error.Reason,
                        "authError",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        error.Reason,
                        "invalidCredentials",
                        StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool IsTemporaryFailure(GoogleApiException exception)
        {
            if (exception.HttpStatusCode is
                HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout)
            {
                return true;
            }

            return exception.Error?.Errors?.Any(error =>
                string.Equals(
                    error.Reason,
                    "rateLimitExceeded",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    error.Reason,
                    "userRateLimitExceeded",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    error.Reason,
                    "backendError",
                    StringComparison.OrdinalIgnoreCase)) == true;
        }
    }
}
