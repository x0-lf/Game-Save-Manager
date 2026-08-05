using GameSaves.Core.Sync;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal sealed record GoogleDriveObjectGetRequest(string ObjectId)
    {
        public string Fields => GoogleDriveRequestContract.MetadataFields;

        // Authoritative-ID inspection must see an object that moved into a
        // shared drive so the caller can reject it explicitly. This does not
        // enable shared-drive listing or mutation.
        public bool SupportsAllDrives =>
            GoogleDriveRequestContract.AuthoritativeIdLookupSupportsAllDrives;

        public override string ToString() => "Google Drive object metadata request";
    }

    internal sealed record GoogleDriveObjectListRequest(
        string Query,
        string? PageToken)
    {
        public string Fields => GoogleDriveRequestContract.ListFields;

        public string Spaces => GoogleDriveRequestContract.DriveSpace;

        public string Corpora => GoogleDriveRequestContract.UserCorpus;

        public bool IncludeItemsFromAllDrives =>
            GoogleDriveRequestContract.IncludeItemsFromAllDrives;

        public bool SupportsAllDrives => GoogleDriveRequestContract.SupportsAllDrives;

        public override string ToString() => "Google Drive child-list request";
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

        public bool SupportsAllDrives => GoogleDriveRequestContract.SupportsAllDrives;
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

    internal interface IGoogleDriveObjectListingApi
    {
        Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken);
    }

    internal sealed class GoogleDriveObjectApi
        : IGoogleDriveObjectApi, IGoogleDriveObjectListingApi
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
                GoogleDriveObjectMetadata metadata = await client.GetAsync(
                    new GoogleDriveObjectGetRequest(objectId),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return metadata;
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

            return await ListAllPagesAsync(
                credential,
                query,
                validateObject: null,
                cancellationToken);
        }

        public async Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken)
        {
            string query = _queryBuilder.BuildDirectChildrenQuery(
                parentFolderId,
                expectedKind);

            return await ListAllPagesAsync(
                credential,
                query,
                metadata => ValidateDirectChild(
                    metadata,
                    parentFolderId,
                    expectedKind),
                cancellationToken);
        }

        private async Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListAllPagesAsync(
            GoogleAuthorizedCredential credential,
            string query,
            Action<GoogleDriveObjectMetadata>? validateObject,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IGoogleDriveObjectClient client = _clientFactory.Create(credential);
                var objects = new List<GoogleDriveObjectMetadata>();
                string? pageToken = null;

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GoogleDriveObjectListPage page = await client.ListAsync(
                        new GoogleDriveObjectListRequest(query, pageToken),
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (page.IncompleteSearch)
                    {
                        throw GoogleDriveApiFailureMapper.Create(
                            GoogleDriveApiOperation.ObjectChildList,
                            GoogleDriveApiFailure.Unavailable,
                            SafeErrorCode(GoogleDriveApiFailure.Unavailable),
                            retryable: true);
                    }

                    if (validateObject is not null)
                    {
                        foreach (GoogleDriveObjectMetadata metadata in page.Objects)
                            validateObject(metadata);
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

        private static void ValidateDirectChild(
            GoogleDriveObjectMetadata metadata,
            string expectedParentId,
            GoogleDriveObjectKind? expectedKind)
        {
            if (metadata.DriveId is not null)
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.ObjectChildList,
                    GoogleDriveApiFailure.AccessDenied,
                    "GoogleDriveObjectUnsupportedLocation",
                    retryable: false);
            }

            if (metadata.Trashed)
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.ObjectChildList,
                    GoogleDriveApiFailure.Failed,
                    "GoogleDriveObjectTrashed",
                    retryable: false);
            }

            if (!metadata.ParentIds.Contains(expectedParentId, StringComparer.Ordinal))
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.ObjectChildList,
                    GoogleDriveApiFailure.Failed,
                    "GoogleDriveObjectParentMismatch",
                    retryable: false);
            }

            if (expectedKind is not null && metadata.Kind != expectedKind.Value)
            {
                throw GoogleDriveApiFailureMapper.Create(
                    GoogleDriveApiOperation.ObjectChildList,
                    GoogleDriveApiFailure.Failed,
                    "GoogleDriveObjectTypeMismatch",
                    retryable: false);
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
                GoogleDriveObjectMetadata created = await client.CreateFolderAsync(
                    new GoogleDriveFolderCreateRequest(name, parentId),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return created;
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
                    ApplicationName = GoogleDriveRequestContract.ApplicationName
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
