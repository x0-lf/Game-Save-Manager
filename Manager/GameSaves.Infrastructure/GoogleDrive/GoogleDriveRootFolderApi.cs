using GameSaves.Core.Sync;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
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
                throw MapException(ex, GoogleDriveApiOperation.RootFolderInspection);
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
                            GoogleDriveApiOperation.RootFolderTopLevelMembership,
                            GoogleDriveApiFailure.Unavailable,
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
                    GoogleDriveApiOperation.RootFolderTopLevelMembership);
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
                            GoogleDriveApiOperation.RootFolderDiscovery,
                            GoogleDriveApiFailure.Unavailable,
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
                throw MapException(ex, GoogleDriveApiOperation.RootFolderDiscovery);
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
                throw MapException(ex, GoogleDriveApiOperation.RootFolderCreation);
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

        internal static GoogleDriveApiException MapException(
            Exception exception,
            GoogleDriveApiOperation operation) =>
            GoogleDriveApiFailureMapper.Map(exception, operation, SafeErrorCode);

        private static GoogleDriveApiException Failure(
            GoogleDriveApiOperation operation,
            GoogleDriveApiFailure failure,
            bool retryable) =>
            GoogleDriveApiFailureMapper.Create(
                operation,
                failure,
                SafeErrorCode(failure),
                retryable);

        private static string SafeErrorCode(GoogleDriveApiFailure failure) =>
            failure switch
            {
                GoogleDriveApiFailure.InvalidRequest =>
                    GoogleDriveRootFolderErrorCodes.InvalidRequest,
                GoogleDriveApiFailure.InvalidQuery =>
                    GoogleDriveRootFolderErrorCodes.InvalidQuery,
                GoogleDriveApiFailure.AuthorizationRevoked =>
                    GoogleDriveRootFolderErrorCodes.AuthenticationRequired,
                GoogleDriveApiFailure.InsufficientScope =>
                    GoogleDriveRootFolderErrorCodes.InsufficientScope,
                GoogleDriveApiFailure.AccessDenied =>
                    GoogleDriveRootFolderErrorCodes.AccessDenied,
                GoogleDriveApiFailure.ApiNotEnabled =>
                    GoogleDriveRootFolderErrorCodes.ApiNotEnabled,
                GoogleDriveApiFailure.NotFound =>
                    GoogleDriveRootFolderErrorCodes.Missing,
                GoogleDriveApiFailure.RateLimited =>
                    GoogleDriveRootFolderErrorCodes.RateLimited,
                GoogleDriveApiFailure.QuotaExceeded =>
                    GoogleDriveRootFolderErrorCodes.QuotaExceeded,
                GoogleDriveApiFailure.Unavailable =>
                    GoogleDriveRootFolderErrorCodes.Unavailable,
                _ => GoogleDriveRootFolderErrorCodes.Failed
            };
    }
}
