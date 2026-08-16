using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

/// <summary>
/// Shared hermetic Google Drive fakes: an in-memory object store plus the
/// object and media clients that operate on it. No network, account, or
/// credential value is involved.
/// </summary>
internal sealed record OfflineDriveObject(
    GoogleDriveObjectMetadata Metadata,
    byte[]? Content);

internal sealed record FolderCreateCall(string ParentId, string Name);

internal sealed record MediaUploadCall(
    string FileName,
    string ParentId,
    long Bytes,
    string FileId);

internal sealed class OfflineDriveStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OfflineDriveObject> _objects = [];
    private int _nextFolderId;
    private int _nextFileId;

    public OfflineDriveStore(string rootId)
    {
        RootId = rootId;
        AddFolder(rootId, "Application Root", GoogleDriveRequestContract.MyDriveRootId);
    }

    public string RootId { get; }

    public IReadOnlyList<string> ObjectIds
    {
        get
        {
            lock (_gate)
                return _objects.Keys.ToArray();
        }
    }

    public void AddFolder(string id, string name, string? parentId = null) =>
        Add(new OfflineDriveObject(
            new GoogleDriveObjectMetadata(
                id,
                name,
                GoogleDriveApplicationRoot.FolderMimeType,
                trashed: false,
                parentIds: [parentId ?? RootId],
                driveId: null),
            Content: null));

    public void AddFile(
        string id,
        string name,
        string parentId,
        byte[] content,
        string mediaType = "application/json") =>
        Add(new OfflineDriveObject(
            new GoogleDriveObjectMetadata(
                id,
                name,
                mediaType,
                trashed: false,
                parentIds: [parentId],
                driveId: null),
            content.ToArray()));

    public OfflineDriveObject AddGeneratedFolder(string name, string parentId)
    {
        string id = $"created-folder-{Interlocked.Increment(ref _nextFolderId)}";
        AddFolder(id, name, parentId);
        return GetRequired(id);
    }

    public OfflineDriveObject AddGeneratedFile(
        string name,
        string parentId,
        byte[] content,
        string mediaType)
    {
        string id = $"created-file-{Interlocked.Increment(ref _nextFileId)}";
        AddFile(id, name, parentId, content, mediaType);
        return GetRequired(id);
    }

    public OfflineDriveObject GetRequired(string id)
    {
        lock (_gate)
            return _objects[id];
    }

    public IReadOnlyList<OfflineDriveObject> FindChildren(
        string parentId,
        string? exactName = null)
    {
        lock (_gate)
        {
            return _objects.Values
                .Where(value => value.Metadata.ParentIds.Contains(
                    parentId,
                    StringComparer.Ordinal))
                .Where(value => exactName is null || string.Equals(
                    value.Metadata.Name,
                    exactName,
                    StringComparison.Ordinal))
                .OrderBy(value => value.Metadata.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Metadata.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string GetRequiredFolderId(string name, string parentId)
    {
        OfflineDriveObject value = Assert.Single(FindChildren(parentId, name));
        Assert.Equal(GoogleDriveObjectKind.Folder, value.Metadata.Kind);
        return value.Metadata.Id;
    }

    public byte[] GetRequiredFileBytes(string name, string parentId)
    {
        OfflineDriveObject value = Assert.Single(FindChildren(parentId, name));
        Assert.Equal(GoogleDriveObjectKind.File, value.Metadata.Kind);
        return value.Content!.ToArray();
    }

    public void ReplaceContent(string fileId, byte[] content)
    {
        lock (_gate)
        {
            OfflineDriveObject existing = _objects[fileId];
            Assert.Equal(GoogleDriveObjectKind.File, existing.Metadata.Kind);
            _objects[fileId] = existing with { Content = content.ToArray() };
        }
    }

    private void Add(OfflineDriveObject value)
    {
        lock (_gate)
            _objects.Add(value.Metadata.Id, value);
    }
}

/// <summary>
/// Offline Drive object client. It answers only the request shapes the
/// production code is allowed to issue and pages one object at a time so
/// cross-page behavior is exercised.
/// </summary>
internal sealed class OfflineDriveObjectClientFactory(
    OfflineDriveStore drive,
    GoogleDriveQueryBuilder queryBuilder)
    : IGoogleDriveObjectClientFactory
{
    private int _disposedClients;
    private int _createFolderCalls;

    public List<GoogleDriveObjectListRequest> ListRequests { get; } = [];

    public List<GoogleDriveObjectGetRequest> GetRequests { get; } = [];

    public List<FolderCreateCall> CreatedFolders { get; } = [];

    public Action<FolderCreateCall>? BeforeFolderCreate { get; set; }

    public int DisposedClients => Volatile.Read(ref _disposedClients);

    public int CreateFolderCalls => Volatile.Read(ref _createFolderCalls);

    public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential)
    {
        Assert.False(credential.IsDisposed);
        return new Client(this, drive, queryBuilder);
    }

    private sealed class Client(
        OfflineDriveObjectClientFactory owner,
        OfflineDriveStore drive,
        GoogleDriveQueryBuilder queryBuilder)
        : IGoogleDriveObjectClient
    {
        private bool _disposed;

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.GetRequests.Add(request);
            return Task.FromResult(drive.GetRequired(request.ObjectId).Metadata);
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.ListRequests.Add(request);
            IReadOnlyList<GoogleDriveObjectMetadata> objects =
                ResolveQuery(request.Query);
            int offset = request.PageToken is null
                ? 0
                : int.Parse(request.PageToken["page-".Length..]);
            GoogleDriveObjectMetadata[] page = objects
                .Skip(offset)
                .Take(1)
                .ToArray();
            string? next = offset + page.Length < objects.Count
                ? $"page-{offset + page.Length}"
                : null;
            return Task.FromResult(new GoogleDriveObjectListPage(
                page,
                next,
                IncompleteSearch: false));
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new FolderCreateCall(request.ParentId, request.Name);
            owner.BeforeFolderCreate?.Invoke(call);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref owner._createFolderCalls);
            OfflineDriveObject created = drive.AddGeneratedFolder(
                request.Name,
                request.ParentId);
            owner.CreatedFolders.Add(call);
            return Task.FromResult(created.Metadata);
        }

        private IReadOnlyList<GoogleDriveObjectMetadata> ResolveQuery(string query)
        {
            foreach (string parentId in drive.ObjectIds)
            {
                if (query == queryBuilder.BuildDirectChildrenQuery(
                        parentId,
                        GoogleDriveObjectKind.Folder))
                {
                    return drive.FindChildren(parentId)
                        .Where(value =>
                            value.Metadata.Kind == GoogleDriveObjectKind.Folder)
                        .Select(value => value.Metadata)
                        .ToArray();
                }

                if (query == queryBuilder.BuildDirectChildrenQuery(
                        parentId,
                        expectedKind: null))
                {
                    return drive.FindChildren(parentId)
                        .Select(value => value.Metadata)
                        .ToArray();
                }

                if (query == queryBuilder.BuildExactNameChildQuery(
                        parentId,
                        "manifest.json"))
                {
                    return drive.FindChildren(parentId, "manifest.json")
                        .Select(value => value.Metadata)
                        .ToArray();
                }

                foreach (OfflineDriveObject child in drive.FindChildren(parentId))
                {
                    if (query == queryBuilder.BuildExactNameChildQuery(
                            parentId,
                            child.Metadata.Name))
                    {
                        return drive.FindChildren(parentId, child.Metadata.Name)
                            .Select(value => value.Metadata)
                            .ToArray();
                    }
                }
            }

            throw new InvalidOperationException(
                "The offline Drive client received an unexpected query.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref owner._disposedClients);
        }
    }
}

internal sealed record MediaDownloadCall(string FileId, long BytesWritten);

/// <summary>
/// Deterministic fake media-download client. It streams stored offline Drive
/// content into the destination in fixed chunks and can report progress, fail,
/// or observe cancellation without any network access.
/// </summary>
internal sealed class OfflineDriveMediaDownloadClientFactory(OfflineDriveStore drive)
    : IGoogleDriveMediaDownloadClientFactory
{
    private int _disposedClients;

    public List<MediaDownloadCall> Calls { get; } = [];

    public Func<string, Exception?>? FailureFor { get; set; }

    public Action<long>? ChunkWritten { get; set; }

    public int ChunkSize { get; set; } = 4096;

    public int CreatedClients { get; private set; }

    public int DisposedClients => Volatile.Read(ref _disposedClients);

    public IGoogleDriveMediaDownloadClient Create(
        GoogleAuthorizedCredential credential)
    {
        Assert.False(credential.IsDisposed);
        CreatedClients++;
        return new Client(this, drive);
    }

    private sealed class Client(
        OfflineDriveMediaDownloadClientFactory owner,
        OfflineDriveStore drive)
        : IGoogleDriveMediaDownloadClient
    {
        private bool _disposed;

        public async Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            if (owner.FailureFor?.Invoke(fileId) is Exception failure)
            {
                progress?.Report(new GoogleDriveMediaDownloadProgress(
                    GoogleDriveMediaDownloadProgressStatus.Failed,
                    0));
                throw failure;
            }

            byte[] content = drive.GetRequired(fileId).Content ?? [];
            progress?.Report(new GoogleDriveMediaDownloadProgress(
                GoogleDriveMediaDownloadProgressStatus.NotStarted,
                0));

            long written = 0;
            for (int offset = 0; offset < content.Length; offset += owner.ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(owner.ChunkSize, content.Length - offset);
                await destination.WriteAsync(
                    content.AsMemory(offset, length),
                    cancellationToken);
                written += length;
                progress?.Report(new GoogleDriveMediaDownloadProgress(
                    GoogleDriveMediaDownloadProgressStatus.Downloading,
                    written));
                owner.ChunkWritten?.Invoke(written);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await destination.FlushAsync(cancellationToken);
            progress?.Report(new GoogleDriveMediaDownloadProgress(
                GoogleDriveMediaDownloadProgressStatus.Completed,
                written));
            owner.Calls.Add(new MediaDownloadCall(fileId, written));
            return written;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref owner._disposedClients);
        }
    }
}

/// <summary>
/// Fake Google media-upload API. It stores completed creates in the offline
/// Drive, records their order, and can fail or observe one requested name
/// without touching any other object.
/// </summary>
internal sealed class OfflineDriveMediaUploadClientFactory(OfflineDriveStore drive)
    : IGoogleDriveMediaUploadClientFactory
{
    private int _disposedClients;

    public List<MediaUploadCall> Calls { get; } = [];

    public Func<string, Exception?>? FailureFor { get; set; }

    public Action<string>? BeforeCreate { get; set; }

    public Func<string, GoogleDriveMediaUploadMetadata?>? ResponseFor { get; set; }

    public int CreatedClients { get; private set; }

    public int DisposedClients => Volatile.Read(ref _disposedClients);

    public IGoogleDriveMediaUploadClient Create(
        GoogleAuthorizedCredential credential)
    {
        Assert.False(credential.IsDisposed);
        CreatedClients++;
        return new Client(this, drive);
    }

    private sealed class Client(
        OfflineDriveMediaUploadClientFactory owner,
        OfflineDriveStore drive)
        : IGoogleDriveMediaUploadClient
    {
        private bool _disposed;

        public async Task<GoogleDriveMediaUploadMetadata> UploadAsync(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType,
            IProgress<GoogleDriveMediaUploadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.BeforeCreate?.Invoke(exactFileName);
            cancellationToken.ThrowIfCancellationRequested();

            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (owner.FailureFor?.Invoke(exactFileName) is Exception failure)
                throw failure;

            OfflineDriveObject created = drive.AddGeneratedFile(
                exactFileName,
                parentFolderId,
                buffer.ToArray(),
                mediaType);
            owner.Calls.Add(new MediaUploadCall(
                exactFileName,
                parentFolderId,
                buffer.Length,
                created.Metadata.Id));

            return owner.ResponseFor?.Invoke(exactFileName) ??
                new GoogleDriveMediaUploadMetadata(
                    created.Metadata.Id,
                    exactFileName,
                    mediaType,
                    trashed: false,
                    parentIds: [parentFolderId],
                    driveId: null,
                    size: buffer.Length);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref owner._disposedClients);
        }
    }
}
