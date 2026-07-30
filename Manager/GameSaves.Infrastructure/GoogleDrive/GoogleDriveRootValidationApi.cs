using GameSaves.Core.Sync;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    /// <summary>
    /// The single metadata-only request permitted for remote validation. The
    /// authoritative ID is deliberately omitted from diagnostic formatting.
    /// </summary>
    internal sealed class GoogleDriveRootValidationRequest
    {
        public GoogleDriveRootValidationRequest(string rootFolderId)
        {
            if (string.IsNullOrWhiteSpace(rootFolderId))
            {
                throw new ArgumentException(
                    "A Google Drive root-folder ID is required.",
                    nameof(rootFolderId));
            }

            RootFolderId = rootFolderId;
        }

        public string RootFolderId { get; }

        public string Fields =>
            GoogleDriveRequestContract.RootValidationMetadataFields;

        public bool SupportsAllDrives =>
            GoogleDriveRequestContract.AuthoritativeIdLookupSupportsAllDrives;

        public override string ToString() =>
            "Google Drive root validation metadata request";
    }

    /// <summary>
    /// Read-only root metadata required by future ValidateAsync behavior.
    /// Names are display-only; the authoritative ID remains the caller's saved
    /// profile value and is not repeated in this model.
    /// </summary>
    internal sealed class GoogleDriveRootValidationMetadata
    {
        public GoogleDriveRootValidationMetadata(
            string? name,
            string? mimeType,
            bool trashed,
            IEnumerable<string>? parentIds,
            string? driveId,
            bool canListChildren,
            bool canAddChildren)
        {
            Name = name;
            MimeType = mimeType;
            Trashed = trashed;
            ParentIds = Array.AsReadOnly(
                parentIds?.ToArray() ?? Array.Empty<string>());
            DriveId = string.IsNullOrWhiteSpace(driveId) ? null : driveId;
            CanListChildren = canListChildren;
            CanAddChildren = canAddChildren;
        }

        public string? Name { get; }

        public string? MimeType { get; }

        public bool Trashed { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public string? DriveId { get; }

        public bool CanListChildren { get; }

        public bool CanAddChildren { get; }

        public bool IsFolder => string.Equals(
            MimeType,
            GoogleDriveApplicationRoot.FolderMimeType,
            StringComparison.Ordinal);

        public bool IsInSharedDrive => DriveId is not null;

        public override string ToString() =>
            $"Google Drive root validation metadata: trashed={Trashed}; " +
            $"folder={IsFolder}; sharedDrive={IsInSharedDrive}; " +
            $"canListChildren={CanListChildren}; canAddChildren={CanAddChildren}";
    }

    internal interface IGoogleDriveRootValidationClient : IDisposable
    {
        Task<GoogleDriveRootValidationMetadata> GetAsync(
            GoogleDriveRootValidationRequest request,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveRootValidationClientFactory
    {
        IGoogleDriveRootValidationClient Create(
            GoogleAuthorizedCredential credential);
    }

    internal interface IGoogleDriveRootValidationApi
    {
        Task<GoogleDriveRootValidationMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string rootFolderId,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Retrieves only authoritative root metadata and child capabilities. It
    /// performs no list, create, upload, download, or probe-write operation.
    /// </summary>
    internal sealed class GoogleDriveRootValidationApi
        : IGoogleDriveRootValidationApi
    {
        private readonly IGoogleDriveRootValidationClientFactory _clientFactory;

        public GoogleDriveRootValidationApi(
            IGoogleDriveRootValidationClientFactory clientFactory) =>
            _clientFactory = clientFactory;

        public async Task<GoogleDriveRootValidationMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string rootFolderId,
            CancellationToken cancellationToken)
        {
            var request = new GoogleDriveRootValidationRequest(rootFolderId);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IGoogleDriveRootValidationClient client =
                    _clientFactory.Create(credential);
                return await client.GetAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                throw MapException(ex);
            }
        }

        internal static GoogleDriveApiException MapException(Exception exception) =>
            GoogleDriveApiFailureMapper.Map(
                exception,
                GoogleDriveApiOperation.RootValidationMetadataGet,
                failure => GoogleDriveRemoteValidationMapper
                    .FromApiFailure(failure)
                    .ErrorCode!);
    }

    internal sealed class GoogleDriveRootValidationClientFactory
        : IGoogleDriveRootValidationClientFactory
    {
        public IGoogleDriveRootValidationClient Create(
            GoogleAuthorizedCredential credential) =>
            new GoogleDriveRootValidationClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
                }));
    }

    internal sealed class GoogleDriveRootValidationClient
        : IGoogleDriveRootValidationClient
    {
        private readonly DriveService _drive;

        public GoogleDriveRootValidationClient(DriveService drive) =>
            _drive = drive;

        public async Task<GoogleDriveRootValidationMetadata> GetAsync(
            GoogleDriveRootValidationRequest request,
            CancellationToken cancellationToken)
        {
            FilesResource.GetRequest sdkRequest = CreateGetRequest(_drive, request);
            DriveFile file = await sdkRequest.ExecuteAsync(cancellationToken);
            return Map(file);
        }

        public void Dispose() => _drive.Dispose();

        internal static FilesResource.GetRequest CreateGetRequest(
            DriveService drive,
            GoogleDriveRootValidationRequest request)
        {
            FilesResource.GetRequest sdkRequest =
                drive.Files.Get(request.RootFolderId);
            sdkRequest.Fields = request.Fields;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            return sdkRequest;
        }

        internal static GoogleDriveRootValidationMetadata Map(DriveFile file) =>
            new(
                file.Name,
                file.MimeType,
                file.Trashed ?? false,
                file.Parents,
                file.DriveId,
                file.Capabilities?.CanListChildren ?? false,
                file.Capabilities?.CanAddChildren ?? false);
    }
}
