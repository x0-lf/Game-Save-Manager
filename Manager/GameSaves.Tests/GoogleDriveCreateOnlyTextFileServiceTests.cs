using System.Collections.Concurrent;
using System.Text;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveCreateOnlyTextFileServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("1d425ff5-c2b9-43c0-b6f1-749a18bbab6e");
    private const string RootId = "private-create-only-root-id";
    private const string ParentId = "private-create-only-parent-id";
    private const string FileId = "private-created-file-id";
    private const string FileName = "manifest.json";

    [Fact]
    public async Task CreateAsync_EnsuresOnlyParentSearchesTwiceCreatesAndCaches()
    {
        var resolver = new RecordingResolver
        {
            EnsureResult = FoundParent("run/nested", ParentId),
            FindHandler = (_, _, _, _) => Task.FromResult(NotFound(FileName))
        };
        var api = new RecordingTextCreationApi();
        var cache = new GoogleDriveObjectIdCache();
        var service = Service(resolver, api, cache);
        const string content = "{\"version\":1,\"name\":\"é\"}";

        await service.CreateAsync(
            ProfileId,
            "run/nested/manifest.json",
            content,
            CancellationToken.None);

        Assert.Equal(new[] { "run/nested" }, resolver.EnsuredPaths);
        Assert.Equal(2, resolver.FindCalls);
        Assert.All(resolver.FindParentIds, id => Assert.Equal(ParentId, id));
        Assert.All(resolver.FindNames, name => Assert.Equal(FileName, name));
        Assert.All(resolver.FindKinds,
            kind => Assert.Equal(GoogleDriveObjectKind.File, kind));
        Assert.Equal(1, api.Calls);
        Assert.Equal(ParentId, api.ParentIds.Single());
        Assert.Equal(FileName, api.FileNames.Single());
        Assert.Equal(
            GoogleDriveTextCreationMediaTypes.Json,
            api.MediaTypes.Single());
        Assert.Equal(Encoding.UTF8.GetBytes(content), api.Contents.Single());

        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootId),
            ParentId,
            FileName,
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal(FileId, entry.ObjectId);
        Assert.Equal(FileName, entry.Metadata.Name);
        Assert.Equal(new[] { ParentId }, entry.Metadata.ParentIds);
        Assert.False(entry.Metadata.Trashed);
        Assert.Null(entry.Metadata.DriveId);
    }

    [Fact]
    public async Task CreateAsync_FileDirectlyUnderRoot_DoesNotEnsureAnyFolder()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, _, _) => Task.FromResult(NotFound(FileName))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api);

        await service.CreateAsync(ProfileId, FileName, "{}", CancellationToken.None);

        Assert.Empty(resolver.EnsuredPaths);
        Assert.All(resolver.FindParentIds, id => Assert.Equal(RootId, id));
        Assert.Equal(RootId, api.ParentIds.Single());
    }

    [Fact]
    public async Task CreateAsync_ExistingFileFailsBeforeLockOrMutation()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (_, parentId, name, _) =>
                Task.FromResult(FoundFile(parentId, name, "existing-file-id"))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.CreateAsync(
                    ProfileId,
                    "run/manifest.json",
                    "replacement",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveCreateOnlyTextFileErrorCodes.AlreadyExists,
            exception.Result.ErrorCode);
        Assert.Equal(1, resolver.FindCalls);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task CreateAsync_SameNameFolderFailsClosedWithoutCreation()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (_, parentId, name, _) =>
                Task.FromResult(TypeMismatchFolder(parentId, name))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.CreateAsync(
                    ProfileId,
                    "run/manifest.json",
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
            exception.Result.ErrorCode);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task CreateAsync_DuplicateSameNameObjectsFailClosed()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, name, _) => Task.FromResult(Ambiguous(name))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.CreateAsync(
                    ProfileId,
                    "run/manifest.json",
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveObjectResolutionErrorCodes.Ambiguous,
            exception.Result.ErrorCode);
        Assert.Equal(0, api.Calls);
        Assert.DoesNotContain(RootId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ParentId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_CandidateAppearingInsideLockBlocksCreation()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (call, parentId, name, _) => Task.FromResult(
                call == 1
                    ? NotFound(name)
                    : FoundFile(parentId, name, "late-file-id"))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.CreateAsync(
                    ProfileId,
                    "run/manifest.json",
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveCreateOnlyTextFileErrorCodes.AlreadyExists,
            exception.Result.ErrorCode);
        Assert.Equal(2, resolver.FindCalls);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task CreateAsync_ConcurrentInProcessCallsCreateExactlyOnce()
    {
        var initialSearchesReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int initialSearches = 0;
        var api = new RecordingTextCreationApi();
        var resolver = new RecordingResolver
        {
            FindHandler = async (call, parentId, name, cancellationToken) =>
            {
                if (call <= 2)
                {
                    if (Interlocked.Increment(ref initialSearches) == 2)
                        initialSearchesReady.TrySetResult();
                    await initialSearchesReady.Task.WaitAsync(cancellationToken);
                    return NotFound(name);
                }

                return api.Created
                    ? FoundFile(parentId, name, FileId)
                    : NotFound(name);
            }
        };
        var cache = new GoogleDriveObjectIdCache();
        var service = Service(
            resolver,
            api,
            cache,
            creationCoordinator: new GoogleDriveObjectCreationCoordinator());

        Task<Exception?>[] calls = Enumerable.Range(0, 2)
            .Select(async _ =>
            {
                try
                {
                    await service.CreateAsync(
                        ProfileId,
                        "run/manifest.json",
                        "{}",
                        CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .ToArray();

        Exception?[] outcomes = await Task.WhenAll(calls);

        Assert.Single(outcomes, outcome => outcome is null);
        GoogleDriveRemoteOperationException failure =
            Assert.IsType<GoogleDriveRemoteOperationException>(
                Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal(
            GoogleDriveCreateOnlyTextFileErrorCodes.AlreadyExists,
            failure.Result.ErrorCode);
        Assert.Equal(1, api.Calls);
        Assert.Equal(4, resolver.FindCalls);
        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootId),
            ParentId,
            FileName,
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? entry));
        Assert.Equal(FileId, entry!.ObjectId);
    }

    [Fact]
    public async Task CreateAsync_InvalidCreateResponseFailsWithoutCaching()
    {
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, name, _) => Task.FromResult(NotFound(name))
        };
        var api = new RecordingTextCreationApi
        {
            Handler = (_, _, _, _, _) =>
                Task.FromResult<GoogleDriveTextCreationResult>(null!)
        };
        var cache = new GoogleDriveObjectIdCache();
        var service = Service(resolver, api, cache);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.CreateAsync(
                    ProfileId,
                    "run/manifest.json",
                    "{}",
                    CancellationToken.None));

        Assert.Equal(
            GoogleDriveCreateOnlyTextFileErrorCodes.InvalidCreateResponse,
            exception.Result.ErrorCode);
        Assert.False(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, RootId),
            ParentId,
            FileName,
            GoogleDriveObjectKind.File,
            out _));
    }

    [Fact]
    public async Task CreateAsync_AuthoritativeMissingStateReplacesStaleCacheEntry()
    {
        var cache = new GoogleDriveObjectIdCache();
        var scope = new GoogleDriveObjectCacheScope(ProfileId, RootId);
        Assert.True(cache.TryStoreUniqueValidated(
            scope,
            ParentId,
            FileName,
            GoogleDriveObjectKind.File,
            Metadata("stale-file-id", ParentId, FileName)));
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, name, _) => Task.FromResult(NotFound(name))
        };
        var api = new RecordingTextCreationApi();
        var service = Service(resolver, api, cache);

        await service.CreateAsync(
            ProfileId,
            "run/manifest.json",
            "{}",
            CancellationToken.None);

        Assert.True(cache.TryGet(
            scope,
            ParentId,
            FileName,
            GoogleDriveObjectKind.File,
            out GoogleDriveObjectIdCacheEntry? entry));
        Assert.Equal(FileId, entry!.ObjectId);
    }

    [Fact]
    public async Task CreateAsync_CancellationWhileWaitingForLockReleasesCoordination()
    {
        var coordinator = new GoogleDriveObjectCreationCoordinator();
        IDisposable blocker = await coordinator.AcquireAsync(
            ParentId,
            FileName,
            CancellationToken.None);
        var firstSearch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver
        {
            FindHandler = (_, _, name, _) =>
            {
                firstSearch.TrySetResult();
                return Task.FromResult(NotFound(name));
            }
        };
        var api = new RecordingTextCreationApi();
        var service = Service(
            resolver,
            api,
            creationCoordinator: coordinator);
        using var cancellation = new CancellationTokenSource();

        Task blocked = service.CreateAsync(
            ProfileId,
            "run/manifest.json",
            "{}",
            cancellation.Token);
        await firstSearch.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Equal(0, api.Calls);

        blocker.Dispose();
        await service.CreateAsync(
            ProfileId,
            "run/manifest.json",
            "{}",
            CancellationToken.None);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task CreateAsync_ExistingContentRemainsByteForByteUnchanged()
    {
        byte[] original = Encoding.UTF8.GetBytes("original immutable bytes\r\n");
        var api = new RecordingTextCreationApi();
        api.Files[(ParentId, FileName)] = original.ToArray();
        var resolver = new RecordingResolver
        {
            FindHandler = (_, parentId, name, _) =>
                Task.FromResult(FoundFile(parentId, name, "existing-file-id"))
        };
        var service = Service(resolver, api);

        await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
            () => service.CreateAsync(
                ProfileId,
                "run/manifest.json",
                "replacement",
                CancellationToken.None));

        Assert.Equal(0, api.Calls);
        Assert.Equal(original, api.Files[(ParentId, FileName)]);
    }

    [Fact]
    public async Task CreateAsync_RootPathIsRejectedBeforeAuthenticationOrMutation()
    {
        var resolver = new RecordingResolver();
        var api = new RecordingTextCreationApi();
        var contextFactory = new RecordingContextFactory(resolver);
        var service = Service(
            resolver,
            api,
            contextFactory: contextFactory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(
                ProfileId,
                string.Empty,
                "{}",
                CancellationToken.None));

        Assert.Equal(0, contextFactory.Calls);
        Assert.Empty(resolver.EnsuredPaths);
        Assert.Equal(0, resolver.FindCalls);
        Assert.Equal(0, api.Calls);
    }

    private static GoogleDriveCreateOnlyTextFileService Service(
        RecordingResolver resolver,
        RecordingTextCreationApi api,
        IGoogleDriveObjectIdCache? cache = null,
        GoogleDriveObjectCreationCoordinator? creationCoordinator = null,
        RecordingContextFactory? contextFactory = null) =>
        new(
            contextFactory ?? new RecordingContextFactory(resolver),
            api,
            creationCoordinator ?? new GoogleDriveObjectCreationCoordinator(),
            cache ?? new GoogleDriveObjectIdCache());

    private static GoogleDriveObjectResolutionResult FoundParent(
        string path,
        string id) =>
        new(
            GoogleDriveObjectResolutionStatus.Found,
            GoogleDriveRelativePath.Parse(path),
            GoogleDriveObjectKind.Folder,
            new GoogleDriveObjectMetadata(
                id,
                GoogleDriveRelativePath.Parse(path).Segments[^1],
                GoogleDriveApplicationRoot.FolderMimeType,
                trashed: false,
                parentIds: [RootId],
                driveId: null));

    private static GoogleDriveObjectResolutionResult NotFound(string name) =>
        new(
            GoogleDriveObjectResolutionStatus.NotFound,
            GoogleDriveRelativePath.Parse(name),
            GoogleDriveObjectKind.File,
            errorCode: GoogleDriveObjectResolutionErrorCodes.NotFound,
            message: "The requested Google Drive object was not found.");

    private static GoogleDriveObjectResolutionResult FoundFile(
        string parentId,
        string name,
        string id) =>
        new(
            GoogleDriveObjectResolutionStatus.Found,
            GoogleDriveRelativePath.Parse(name),
            GoogleDriveObjectKind.File,
            Metadata(id, parentId, name));

    private static GoogleDriveObjectResolutionResult TypeMismatchFolder(
        string parentId,
        string name) =>
        new(
            GoogleDriveObjectResolutionStatus.TypeMismatch,
            GoogleDriveRelativePath.Parse(name),
            GoogleDriveObjectKind.Folder,
            new GoogleDriveObjectMetadata(
                "same-name-folder-id",
                name,
                GoogleDriveApplicationRoot.FolderMimeType,
                trashed: false,
                parentIds: [parentId],
                driveId: null),
            errorCode: GoogleDriveObjectResolutionErrorCodes.TypeMismatch,
            message: "The Google Drive object has the wrong type.");

    private static GoogleDriveObjectResolutionResult Ambiguous(string name) =>
        new(
            GoogleDriveObjectResolutionStatus.Ambiguous,
            GoogleDriveRelativePath.Parse(name),
            GoogleDriveObjectKind.File,
            errorCode: GoogleDriveObjectResolutionErrorCodes.Ambiguous,
            message: "More than one Google Drive object has the requested name.");

    private static GoogleDriveObjectMetadata Metadata(
        string id,
        string parentId,
        string name) =>
        new(
            id,
            name,
            GoogleDriveTextCreationMediaTypes.Json,
            trashed: false,
            parentIds: [parentId],
            driveId: null);

    private static GoogleAuthorizedCredential Credential()
    {
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = "test-client-id",
                    ClientSecret = "test-client-secret"
                }
            });
        var user = new UserCredential(
            flow,
            "create-only-test-user",
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private sealed class RecordingContextFactory(RecordingResolver resolver)
        : IGoogleDriveRemoteOperationContextFactory
    {
        public int Calls { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                Credential(),
                resolver));
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        private int _findCalls;

        public GoogleDriveObjectResolutionResult EnsureResult { get; set; } =
            FoundParent("run", ParentId);

        public Func<
            int,
            string,
            string,
            CancellationToken,
            Task<GoogleDriveObjectResolutionResult>>? FindHandler { get; set; }

        public ConcurrentQueue<string> EnsuredPaths { get; } = new();

        public ConcurrentQueue<string> FindParentIds { get; } = new();

        public ConcurrentQueue<string> FindNames { get; } = new();

        public ConcurrentQueue<GoogleDriveObjectKind> FindKinds { get; } = new();

        public int FindCalls => Volatile.Read(ref _findCalls);

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _findCalls);
            FindParentIds.Enqueue(parentId);
            FindNames.Enqueue(exactName);
            FindKinds.Enqueue(expectedKind);
            return FindHandler is null
                ? Task.FromResult(NotFound(exactName))
                : FindHandler(call, parentId, exactName, cancellationToken);
        }

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Create-only text files must not use full-path read resolution.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsuredPaths.Enqueue(relativeFolderPath.Canonical);
            return Task.FromResult(EnsureResult);
        }
    }

    private sealed class RecordingTextCreationApi : IGoogleDriveTextCreationApi
    {
        private int _calls;
        private int _created;

        public Func<
            GoogleAuthorizedCredential,
            string,
            string,
            ReadOnlyMemory<byte>,
            CancellationToken,
            Task<GoogleDriveTextCreationResult>>? Handler { get; set; }

        public ConcurrentQueue<string> ParentIds { get; } = new();

        public ConcurrentQueue<string> FileNames { get; } = new();

        public ConcurrentQueue<string> MediaTypes { get; } = new();

        public ConcurrentQueue<byte[]> Contents { get; } = new();

        public ConcurrentDictionary<(string ParentId, string Name), byte[]> Files
            { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public bool Created => Volatile.Read(ref _created) != 0;

        public async Task<GoogleDriveTextCreationResult> CreateTextFileAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            string exactFileName,
            ReadOnlyMemory<byte> contentBytes,
            string mediaType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            ParentIds.Enqueue(parentFolderId);
            FileNames.Enqueue(exactFileName);
            MediaTypes.Enqueue(mediaType);
            Contents.Enqueue(contentBytes.ToArray());

            if (Handler is not null)
            {
                return await Handler(
                    credential,
                    parentFolderId,
                    exactFileName,
                    contentBytes,
                    cancellationToken);
            }

            Files.TryAdd(
                (parentFolderId, exactFileName),
                contentBytes.ToArray());
            Volatile.Write(ref _created, 1);
            return new GoogleDriveTextCreationResult(FileId);
        }
    }
}
