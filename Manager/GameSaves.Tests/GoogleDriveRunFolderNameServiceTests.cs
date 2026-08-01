using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveRunFolderNameServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("665251e6-fc72-4fd4-a2aa-4add5df05350");

    [Fact]
    public async Task FoldersWithoutManifest_AreIgnoredAndIncludedNamesAreOrdered()
    {
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(
                Candidate("zeta-id", "zeta"),
                Candidate("missing-id", "No Manifest"),
                Candidate("alpha-id", "Alpha"),
                Candidate("unicode-id", "保存 Pokémon"))
        };
        var objects = new RecordingObjectApi();
        objects.Results["zeta-id"] = new[] { Manifest("manifest-zeta", "zeta-id") };
        objects.Results["missing-id"] = new[]
        {
            Folder("folder-named-manifest", "manifest.json", "missing-id")
        };
        objects.Results["alpha-id"] = new[] { Manifest("manifest-alpha", "alpha-id") };
        objects.Results["unicode-id"] = new[] { Manifest("manifest-unicode", "unicode-id") };
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderNameService(
            contexts,
            discovery,
            objects);

        IReadOnlyList<string> names = await service.ListAsync(ProfileId);

        Assert.Equal(new[] { "Alpha", "zeta", "保存 Pokémon" }, names);
        Assert.Equal(
            new[] { "zeta-id", "missing-id", "alpha-id", "unicode-id" },
            objects.ExactNameRequests.Select(request => request.ParentId));
        Assert.All(objects.ExactNameRequests,
            request => Assert.Equal("manifest.json", request.Name));
        Assert.Equal(1, discovery.ContextCalls);
        Assert.Equal(0, discovery.ProfileIdCalls);
        Assert.Same(contexts.LastContext, discovery.Context);
        Assert.True(contexts.LastCredential!.IsDisposed);
        Assert.Equal(0, objects.GetCalls);
        Assert.Equal(0, objects.CreateCalls);
    }

    [Fact]
    public async Task ExactDuplicateRunNames_FailClosedBeforeManifestLookup()
    {
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(
                Candidate("first-private-id", "Same Run", exactCollision: true),
                Candidate("second-private-id", "Same Run", exactCollision: true))
        };
        var objects = new RecordingObjectApi();
        objects.Results["first-private-id"] = new[]
        {
            Manifest("first-private-manifest-id", "first-private-id")
        };
        objects.Results["second-private-id"] = new[]
        {
            Manifest("second-private-manifest-id", "second-private-id")
        };
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderNameService(
            contexts,
            discovery,
            objects);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ListAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRunFolderListingErrorCodes.AmbiguousRunName,
            exception.Result.ErrorCode);
        Assert.Equal(2, objects.ExactNameRequests.Count);
        Assert.True(contexts.LastCredential!.IsDisposed);
        AssertSafe(
            exception,
            "first-private-id",
            "second-private-id",
            "Same Run");
    }

    [Fact]
    public async Task CaseInsensitiveRunNameCollision_FailsClosedWithStableError()
    {
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(
                Candidate("upper-private-id", "Run", caseCollision: true),
                Candidate("lower-private-id", "run", caseCollision: true))
        };
        var objects = new RecordingObjectApi();
        objects.Results["upper-private-id"] = new[]
        {
            Manifest("upper-private-manifest-id", "upper-private-id")
        };
        objects.Results["lower-private-id"] = new[]
        {
            Manifest("lower-private-manifest-id", "lower-private-id")
        };
        var service = new GoogleDriveRunFolderNameService(
            new RecordingContextFactory(),
            discovery,
            objects);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ListAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRunFolderListingErrorCodes.AmbiguousRunName,
            exception.Result.ErrorCode);
        Assert.Equal(2, objects.ExactNameRequests.Count);
        AssertSafe(
            exception,
            "upper-private-id",
            "lower-private-id");
    }

    [Fact]
    public async Task DuplicateManifestFiles_IncludeRunOnceWithoutSelectingAnObject()
    {
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(Candidate("run-folder-id", "Run One"))
        };
        var objects = new RecordingObjectApi();
        objects.Results["run-folder-id"] = new[]
        {
            Manifest("first-private-manifest-id", "run-folder-id"),
            Manifest("second-private-manifest-id", "run-folder-id")
        };
        var service = new GoogleDriveRunFolderNameService(
            new RecordingContextFactory(),
            discovery,
            objects);

        IReadOnlyList<string> names = await service.ListAsync(ProfileId);

        Assert.Equal(new[] { "Run One" }, names);
        Assert.Single(objects.ExactNameRequests);
        Assert.Equal(0, objects.GetCalls);
        Assert.Equal(0, objects.CreateCalls);
    }

    [Fact]
    public async Task ManifestLookup_FollowsAllPagesAndUsesOnlyListMetadataRequests()
    {
        var discoveryClient = new RecordingObjectClient();
        discoveryClient.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Folder("run-folder-id", "Run One") },
            NextPageToken: null,
            IncompleteSearch: false));

        var manifestClient = new RecordingObjectClient();
        manifestClient.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-next-page-token",
            IncompleteSearch: false));
        manifestClient.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[]
            {
                Manifest("first-private-manifest-id", "run-folder-id"),
                Manifest("second-private-manifest-id", "run-folder-id")
            },
            NextPageToken: null,
            IncompleteSearch: false));

        var clientFactory = new RecordingObjectClientFactory(
            discoveryClient,
            manifestClient);
        var objectApi = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            clientFactory);
        var contexts = new RecordingContextFactory();
        var discovery = new GoogleDriveRunFolderDiscoveryService(
            contexts,
            objectApi);
        var service = new GoogleDriveRunFolderNameService(
            contexts,
            discovery,
            objectApi);

        IReadOnlyList<string> names = await service.ListAsync(ProfileId);

        Assert.Equal(new[] { "Run One" }, names);
        Assert.Single(discoveryClient.ListRequests);
        Assert.Equal(2, manifestClient.ListRequests.Count);
        Assert.Equal(
            new[] { null, "private-next-page-token" },
            manifestClient.ListRequests.Select(request => request.PageToken));
        Assert.All(manifestClient.ListRequests, request =>
        {
            Assert.Equal(
                "'run-folder-id' in parents and name = 'manifest.json' and trashed = false",
                request.Query);
            Assert.Equal(
                "nextPageToken,incompleteSearch," +
                "files(id,name,mimeType,trashed,parents,driveId)",
                request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
        });
        Assert.Empty(discoveryClient.GetRequests);
        Assert.Empty(discoveryClient.CreateRequests);
        Assert.Empty(manifestClient.GetRequests);
        Assert.Empty(manifestClient.CreateRequests);
        Assert.Equal(1, discoveryClient.DisposeCalls);
        Assert.Equal(1, manifestClient.DisposeCalls);
        Assert.Equal(1, contexts.CreateCalls);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDisposesTheSharedContext()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(Candidate("run-folder-id", "Run One"))
        };
        var objects = new RecordingObjectApi
        {
            Handler = (_, _, token) => throw new OperationCanceledException(token)
        };
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderNameService(
            contexts,
            discovery,
            objects);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAsync(ProfileId, cancellation.Token));

        Assert.Equal(cancellation.Token, objects.ExactNameRequests.Single().Token);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task SharedDriveManifestMetadata_IsRejectedWithoutIdentityDisclosure()
    {
        var discovery = new RecordingDiscoveryService
        {
            Result = Result(Candidate("run-folder-id", "Private Run Name"))
        };
        var objects = new RecordingObjectApi();
        objects.Results["run-folder-id"] = new[]
        {
            new GoogleDriveObjectMetadata(
                "private-manifest-id",
                "manifest.json",
                "application/json",
                trashed: false,
                new[] { "run-folder-id" },
                "private-shared-drive-id")
        };
        var service = new GoogleDriveRunFolderNameService(
            new RecordingContextFactory(),
            discovery,
            objects);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ListAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
            exception.Result.Status);
        AssertSafe(
            exception,
            "private-manifest-id",
            "private-shared-drive-id",
            "Private Run Name");
    }

    private static GoogleDriveRunFolderDiscoveryResult Result(
        params GoogleDriveRunFolderCandidate[] candidates) => new(candidates);

    private static GoogleDriveRunFolderCandidate Candidate(
        string id,
        string name,
        bool exactCollision = false,
        bool caseCollision = false) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            new[] { RecordingContextFactory.RootId },
            exactCollision,
            caseCollision);

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        string parentId = RecordingContextFactory.RootId) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed: false,
            new[] { parentId },
            driveId: null);

    private static GoogleDriveObjectMetadata Manifest(
        string id,
        string parentId) =>
        new(
            id,
            "manifest.json",
            "application/json",
            trashed: false,
            new[] { parentId },
            driveId: null);

    private static void AssertSafe(object value, params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues)
            Assert.DoesNotContain(privateValue, text, StringComparison.Ordinal);
    }

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public const string RootId = "authoritative-root-id";

        public int CreateCalls { get; private set; }

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public GoogleDriveRemoteOperationContext? LastContext { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCredential = Credential();
            LastContext = new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                LastCredential,
                new RejectingResolver());
            return Task.FromResult(LastContext);
        }
    }

    private sealed class RecordingDiscoveryService
        : IGoogleDriveRunFolderDiscoveryService
    {
        public GoogleDriveRunFolderDiscoveryResult Result { get; set; } =
            new(Array.Empty<GoogleDriveRunFolderCandidate>());

        public int ProfileIdCalls { get; private set; }

        public int ContextCalls { get; private set; }

        public GoogleDriveRemoteOperationContext? Context { get; private set; }

        public Task<GoogleDriveRunFolderDiscoveryResult> DiscoverAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfileIdCalls++;
            throw new InvalidOperationException(
                "Run-folder name listing must share its operation context.");
        }

        public Task<GoogleDriveRunFolderDiscoveryResult> DiscoverAsync(
            GoogleDriveRemoteOperationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContextCalls++;
            Context = context;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingObjectApi : IGoogleDriveObjectApi
    {
        public Dictionary<string, IReadOnlyList<GoogleDriveObjectMetadata>> Results
            { get; } = new(StringComparer.Ordinal);

        public Func<string, string, CancellationToken,
            IReadOnlyList<GoogleDriveObjectMetadata>>? Handler { get; set; }

        public List<(string ParentId, string Name, CancellationToken Token)>
            ExactNameRequests { get; } = new();

        public int GetCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            throw new InvalidOperationException(
                "Run-folder name listing must not fetch individual objects.");
        }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            ExactNameRequests.Add((parentId, name, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (Handler is not null)
                return Task.FromResult(Handler(parentId, name, cancellationToken));

            return Task.FromResult(
                Results.TryGetValue(parentId, out IReadOnlyList<GoogleDriveObjectMetadata>? result)
                    ? result
                    : (IReadOnlyList<GoogleDriveObjectMetadata>)Array.Empty<GoogleDriveObjectMetadata>());
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException(
                "Run-folder name listing must never create a folder.");
        }
    }

    private sealed class RejectingResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) => Unexpected();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) => Unexpected();

        private static Task<GoogleDriveObjectResolutionResult> Unexpected() =>
            Task.FromException<GoogleDriveObjectResolutionResult>(
                new InvalidOperationException(
                    "Run-folder name listing must not use path resolution or creation."));
    }

    private sealed class RecordingObjectClientFactory
        : IGoogleDriveObjectClientFactory
    {
        private readonly Queue<IGoogleDriveObjectClient> _clients;

        public RecordingObjectClientFactory(
            params IGoogleDriveObjectClient[] clients) =>
            _clients = new Queue<IGoogleDriveObjectClient>(clients);

        public IGoogleDriveObjectClient Create(
            GoogleAuthorizedCredential credential) => _clients.Dequeue();
    }

    private sealed class RecordingObjectClient : IGoogleDriveObjectClient
    {
        public Queue<GoogleDriveObjectListPage> Pages { get; } = new();

        public List<GoogleDriveObjectGetRequest> GetRequests { get; } = new();

        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();

        public List<GoogleDriveFolderCreateRequest> CreateRequests { get; } = new();

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            GetRequests.Add(request);
            throw new InvalidOperationException(
                "Run-folder name listing must not request object content or metadata by ID.");
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListRequests.Add(request);
            return Task.FromResult(Pages.Dequeue());
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreateRequests.Add(request);
            throw new InvalidOperationException(
                "Run-folder name listing must not create a folder.");
        }

        public void Dispose() => DisposeCalls++;
    }

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
            ProfileId.ToString("D"),
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }
}
