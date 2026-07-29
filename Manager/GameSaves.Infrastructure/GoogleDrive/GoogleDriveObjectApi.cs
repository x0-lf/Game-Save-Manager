using GameSaves.Core.Sync;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal sealed record GoogleDriveObjectGetRequest(string ObjectId)
    {
        public string Fields => GoogleDriveRequestContract.MetadataFields;

        public bool SupportsAllDrives => false;

        public override string ToString() => "Google Drive object metadata request";
    }

    internal sealed record GoogleDriveObjectListRequest(
        string Query,
        string? PageToken)
    {
        public string Fields => GoogleDriveRequestContract.ListFields;

        public string Spaces => GoogleDriveRequestContract.DriveSpace;

        public string Corpora => GoogleDriveRequestContract.UserCorpus;

        public bool IncludeItemsFromAllDrives => false;

        public bool SupportsAllDrives => false;

        public override string ToString() => "Google Drive exact-name child request";
    }

    internal sealed class GoogleDriveFolderCreateRequest
    {
        public GoogleDriveFolderCreateRequest(string name, string parentId)
        {
            Name = name;
            ParentId = parentId;
            ParentIds = Array.AsReadOnly(new[] { parentId });
        }

        public string Name { get; }

        public string ParentId { get; }

        public IReadOnlyList<string> ParentIds { get; }

        public string MimeType => GoogleDriveApplicationRoot.FolderMimeType;

        public string Fields => GoogleDriveRequestContract.MetadataFields;

        public bool SupportsAllDrives => false;
    }

    internal sealed record GoogleDriveObjectListPage(
        IReadOnlyList<GoogleDriveObjectMetadata> Objects,
        string? NextPageToken,
        bool IncompleteSearch)
    {
        public override string ToString() =>
            $"Google Drive object list page (count={Objects.Count}, incomplete={IncompleteSearch})";
    }

    internal interface IGoogleDriveObjectClient : IDisposable
    {
        Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken);

        Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken);

        Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken);
    }

    internal interface IGoogleDriveObjectClientFactory
    {
        IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential);
    }

    internal interface IGoogleDriveObjectApi
    {
        Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenByExactNameAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken);

        Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken);
    }

    internal sealed class GoogleDriveObjectApi : IGoogleDriveObjectApi
    {
        private readonly GoogleDriveQueryBuilder _queryBuilder;
        private readonly IGoogleDriveObjectClientFactory _clientFactory;

        public GoogleDriveObjectApi(
            GoogleDriveQueryBuilder queryBuilder,
            IGoogleDriveObjectClientFactory clientFactory)
        {
            _queryBuilder = queryBuilder;
            _clientFactory = clientFactory;
        }

        public async Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("A Google Drive object ID is required.", nameof(objectId));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IGoogleDriveObjectClient client = _clientFactory.Create(credential);
                return await client.GetAsync(
                    new GoogleDriveObjectGetRequest(objectId),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveApiOperation.ObjectMetadataGet);
            }
        }

        public async Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            string query = _queryBuilder.BuildExactNameChildQuery(parentId, name);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IGoogleDriveObjectClient client = _clientFactory.Create(credential);
                var objects = new List<GoogleDriveObjectMetadata>();
                string? pageToken = null;

                do
                {
                    GoogleDriveObjectListPage page = await client.ListAsync(
                        new GoogleDriveObjectListRequest(query, pageToken),
                        cancellationToken);

                    if (page.IncompleteSearch)
                    {
                        throw GoogleDriveApiFailureMapper.Create(
                            GoogleDriveApiOperation.ObjectChildList,
                            GoogleDriveApiFailure.Unavailable,
                            SafeErrorCode(GoogleDriveApiFailure.Unavailable),
                            retryable: true);
                    }

                    objects.AddRange(page.Objects);
                    pageToken = string.IsNullOrWhiteSpace(page.NextPageToken)
                        ? null
                        : page.NextPageToken;
                }
                while (pageToken is not null);

                return objects;
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveApiOperation.ObjectChildList);
            }
        }

        public async Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(parentId))
                throw new ArgumentException("A Google Drive parent object ID is required.", nameof(parentId));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A Google Drive folder name is required.", nameof(name));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IGoogleDriveObjectClient client = _clientFactory.Create(credential);
                return await client.CreateFolderAsync(
                    new GoogleDriveFolderCreateRequest(name, parentId),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw MapException(ex, GoogleDriveApiOperation.ObjectFolderCreation);
            }
        }

        internal static GoogleDriveApiException MapException(
            Exception exception,
            GoogleDriveApiOperation operation) =>
            GoogleDriveApiFailureMapper.Map(exception, operation, SafeErrorCode);

        private static string SafeErrorCode(GoogleDriveApiFailure failure) =>
            $"GoogleDriveObject{failure}";
    }

    internal sealed class GoogleDriveObjectClientFactory
        : IGoogleDriveObjectClientFactory
    {
        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential) =>
            new GoogleDriveObjectClient(new DriveService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential.Credential,
                    ApplicationName = "Game Save Manager"
                }));
    }

    internal sealed class GoogleDriveObjectClient : IGoogleDriveObjectClient
    {
        private readonly DriveService _drive;

        public GoogleDriveObjectClient(DriveService drive) => _drive = drive;

        public async Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            FilesResource.GetRequest sdkRequest = CreateGetRequest(_drive, request);

            DriveFile file = await sdkRequest.ExecuteAsync(cancellationToken);
            return Map(file);
        }

        public async Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            FilesResource.ListRequest sdkRequest = CreateListRequest(_drive, request);

            Google.Apis.Drive.v3.Data.FileList page =
                await sdkRequest.ExecuteAsync(cancellationToken);

            IReadOnlyList<GoogleDriveObjectMetadata> objects =
                page.Files?.Select(Map).ToArray() ??
                Array.Empty<GoogleDriveObjectMetadata>();

            return new GoogleDriveObjectListPage(
                objects,
                page.NextPageToken,
                page.IncompleteSearch == true);
        }

        public async Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            DriveFile metadata = CreateFolderMetadata(request);
            FilesResource.CreateRequest sdkRequest =
                CreateFolderRequest(_drive, request, metadata);

            DriveFile created = await sdkRequest.ExecuteAsync(cancellationToken);
            return Map(created);
        }

        public void Dispose() => _drive.Dispose();

        internal static FilesResource.GetRequest CreateGetRequest(
            DriveService drive,
            GoogleDriveObjectGetRequest request)
        {
            FilesResource.GetRequest sdkRequest = drive.Files.Get(request.ObjectId);
            sdkRequest.Fields = request.Fields;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            return sdkRequest;
        }

        internal static FilesResource.ListRequest CreateListRequest(
            DriveService drive,
            GoogleDriveObjectListRequest request)
        {
            FilesResource.ListRequest sdkRequest = drive.Files.List();
            sdkRequest.Q = request.Query;
            sdkRequest.Spaces = request.Spaces;
            sdkRequest.Corpora = request.Corpora;
            sdkRequest.IncludeItemsFromAllDrives = request.IncludeItemsFromAllDrives;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            sdkRequest.Fields = request.Fields;
            sdkRequest.PageToken = request.PageToken;
            return sdkRequest;
        }

        internal static DriveFile CreateFolderMetadata(
            GoogleDriveFolderCreateRequest request) =>
            new()
            {
                Name = request.Name,
                MimeType = request.MimeType,
                Parents = request.ParentIds.ToArray()
            };

        internal static FilesResource.CreateRequest CreateFolderRequest(
            DriveService drive,
            GoogleDriveFolderCreateRequest request,
            DriveFile metadata)
        {
            FilesResource.CreateRequest sdkRequest = drive.Files.Create(metadata);
            sdkRequest.Fields = request.Fields;
            sdkRequest.SupportsAllDrives = request.SupportsAllDrives;
            return sdkRequest;
        }

        private static GoogleDriveObjectMetadata Map(DriveFile file) => new(
            file.Id,
            file.Name,
            file.MimeType,
            file.Trashed ?? false,
            file.Parents,
            file.DriveId);
    }
}
