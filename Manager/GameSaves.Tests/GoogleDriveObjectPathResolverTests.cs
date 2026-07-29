using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveObjectPathResolverTests
{
    [Fact]
    public async Task EmptyPath_ResolvesToAuthoritativeApplicationRootId()
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Root,
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("root-id", result.ObjectId);
        Assert.Equal(GoogleDriveObjectKind.Folder, result.ObjectKind);
        Assert.Null(result.Metadata);
        Assert.DoesNotContain("root-id", result.ToString(), StringComparison.Ordinal);
        Assert.Empty(api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task OneFolder_ResolvesByExactNameUnderRootId()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Saves", Folder("saves-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Saves"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("saves-id", result.ObjectId);
        Assert.Equal(new[] { ("root-id", "Saves") }, api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task NestedFolders_UseResolvedIdsForEachFollowingSegment()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Game", Folder("game-id", "Game", "root-id"));
        api.Set("game-id", "Run", Folder("run-id", "Run", "game-id"));
        api.Set("run-id", "Data", Folder("data-id", "Data", "run-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Game/Run/Data"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("data-id", result.ObjectId);
        Assert.Equal(
            new[]
            {
                ("root-id", "Game"),
                ("game-id", "Run"),
                ("run-id", "Data")
            },
            api.ListCalls);
    }

    [Fact]
    public async Task FinalFile_ResolvesAfterFolderSegments()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Run", Folder("run-id", "Run", "root-id"));
        api.Set("run-id", "manifest.json",
            FileObject("manifest-id", "manifest.json", "run-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Run/manifest.json"),
            GoogleDriveObjectKind.File);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal(GoogleDriveObjectKind.File, result.ObjectKind);
        Assert.Equal("manifest-id", result.ObjectId);
    }

    [Fact]
    public async Task MissingIntermediateFolder_StopsBeforeLookingUpLaterSegments()
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Missing/Later"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.NotFound, result.Status);
        Assert.Equal(new[] { ("root-id", "Missing") }, api.ListCalls);
        Assert.Equal("Missing/Later", result.Path!.Canonical);
    }

    [Fact]
    public async Task MissingFinalObject_ReturnsNotFoundAfterResolvingParent()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Run", Folder("run-id", "Run", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Run/missing.sav"),
            GoogleDriveObjectKind.File);

        Assert.Equal(GoogleDriveObjectResolutionStatus.NotFound, result.Status);
        Assert.Equal(
            new[] { ("root-id", "Run"), ("run-id", "missing.sav") },
            api.ListCalls);
    }

    [Fact]
    public async Task FileInIntermediatePosition_ReturnsTypeMismatchAndStops()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Run", FileObject("file-id", "Run", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Run/child"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.TypeMismatch, result.Status);
        Assert.Equal(GoogleDriveObjectKind.File, result.ObjectKind);
        Assert.Single(api.ListCalls);
    }

    [Fact]
    public async Task FolderWhenFinalFileExpected_ReturnsTypeMismatch()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "manifest.json",
            Folder("folder-id", "manifest.json", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("manifest.json"),
            GoogleDriveObjectKind.File);

        Assert.Equal(GoogleDriveObjectResolutionStatus.TypeMismatch, result.Status);
        Assert.Equal(GoogleDriveObjectKind.Folder, result.ObjectKind);
    }

    [Fact]
    public async Task DuplicateFolders_ReturnAmbiguousWithoutSelectingEither()
    {
        var api = new FakeObjectApi();
        api.Set(
            "root-id",
            "Saves",
            Folder("first-id", "Saves", "root-id"),
            Folder("second-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.ObjectId);
        Assert.Null(result.Metadata);
        Assert.Equal(0, api.GetCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task SameNameFileAndFolder_AreAmbiguousBeforeExpectedKindFiltering()
    {
        var api = new FakeObjectApi();
        api.Set(
            "root-id",
            "Saves",
            FileObject("file-id", "Saves", "root-id"),
            Folder("folder-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.ObjectId);
    }

    [Theory]
    [InlineData("保存データ")]
    [InlineData("O'Brien")]
    [InlineData(@"folder\name")]
    [InlineData(@"Léa's\保存")]
    public async Task ExactUnicodeApostropheAndBackslashNames_ArePreserved(string name)
    {
        var api = new FakeObjectApi();
        api.Set("root-id", name, Folder("folder-id", name, "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse(name),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal(("root-id", name), Assert.Single(api.ListCalls));
    }

    [Fact]
    public async Task SharedDriveObject_IsRejectedWithoutModification()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Saves",
            Folder("shared-id", "Saves", "root-id", driveId: "shared-drive-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.UnsupportedLocation, result.Status);
        Assert.Equal("shared-id", result.ObjectId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task TrashedObject_IsRejectedDefensively()
    {
        var api = new FakeObjectApi();
        api.Set("root-id", "Saves",
            Folder("trashed-id", "Saves", "root-id", trashed: true));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Trashed, result.Status);
        Assert.Equal("trashed-id", result.ObjectId);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("parent-id", "")]
    [InlineData("parent-id", "folder/child")]
    [InlineData("parent-id", ".")]
    [InlineData("parent-id", "..")]
    public async Task InvalidParentOrChildName_ReturnsInvalidPathWithoutApiCall(
        string parentId,
        string name)
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            parentId,
            name,
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.InvalidPath, result.Status);
        Assert.Empty(api.ListCalls);
    }

    [Fact]
    public async Task Cancellation_StopsBeforeApiLookup()
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Saves"),
                GoogleDriveObjectKind.Folder,
                cancellation.Token));

        Assert.Empty(api.ListCalls);
    }

    [Fact]
    public async Task PaginationFromObjectApi_IsInheritedAndAllDuplicatesRemainAmbiguous()
    {
        var client = new PagedObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Folder("first-id", "Saves", "root-id") },
            "page-2",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Folder("second-id", "Saves", "root-id") },
            null,
            IncompleteSearch: false));
        var objectApi = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new PagedObjectClientFactory(client));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(objectApi, credential);

        GoogleDriveObjectResolutionResult result = await resolver.FindChildAsync(
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Null(client.ListRequests[0].PageToken);
        Assert.Equal("page-2", client.ListRequests[1].PageToken);
        Assert.Equal(0, client.CreateCalls);
    }

    [Fact]
    public async Task AuthenticationFailure_MapsSafelyAndStopsTraversal()
    {
        var api = new FakeObjectApi
        {
            Failure = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectChildList,
                GoogleDriveApiFailure.AuthorizationRevoked,
                "GoogleDriveObjectAuthorizationRevoked")
        };
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = new GoogleDriveObjectPathResolver(api, credential);

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Saves/Later"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(
            GoogleDriveObjectResolutionStatus.ReauthenticationRequired,
            result.Status);
        Assert.Single(api.ListCalls);
        Assert.DoesNotContain("root-id", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverContract_ContainsOnlyLookupResolutionAndFolderEnsureOperations()
    {
        string[] methods = typeof(IGoogleDriveObjectPathResolver)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "EnsureFolderPathAsync",
                "FindChildAsync",
                "ResolveAsync"
            },
            methods.Order());
        Assert.DoesNotContain(methods, name =>
            name.Contains("Upload", StringComparison.Ordinal) ||
            name.Contains("Download", StringComparison.Ordinal) ||
            name.Contains("Delete", StringComparison.Ordinal));
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
            "test-profile",
            new TokenResponse
            {
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token"
            });
        return new GoogleAuthorizedCredential(user);
    }

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        string parentId,
        string? driveId = null,
        bool trashed = false) =>
        new(
            id,
            name,
            "application/vnd.google-apps.folder",
            trashed,
            new[] { parentId },
            driveId);

    private static GoogleDriveObjectMetadata FileObject(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            "application/octet-stream",
            false,
            new[] { parentId },
            null);

    private sealed class FakeObjectApi : IGoogleDriveObjectApi
    {
        private readonly Dictionary<(string ParentId, string Name),
            IReadOnlyList<GoogleDriveObjectMetadata>> _results = new();

        public List<(string ParentId, string Name)> ListCalls { get; } = new();
        public int GetCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public GoogleDriveApiException? Failure { get; set; }

        public void Set(
            string parentId,
            string name,
            params GoogleDriveObjectMetadata[] results) =>
            _results[(parentId, name)] = results;

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            throw new InvalidOperationException("The read-only resolver must not get by ID.");
        }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls.Add((parentId, name));

            if (Failure is not null)
                throw Failure;

            return Task.FromResult(
                _results.TryGetValue((parentId, name), out var results)
                    ? results
                    : (IReadOnlyList<GoogleDriveObjectMetadata>)
                        Array.Empty<GoogleDriveObjectMetadata>());
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("The read-only resolver must not create folders.");
        }
    }

    private sealed class PagedObjectClientFactory : IGoogleDriveObjectClientFactory
    {
        private readonly PagedObjectClient _client;

        public PagedObjectClientFactory(PagedObjectClient client) => _client = client;

        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential) =>
            _client;
    }

    private sealed class PagedObjectClient : IGoogleDriveObjectClient
    {
        public Queue<GoogleDriveObjectListPage> Pages { get; } = new();
        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();
        public int CreateCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object metadata request.");

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
            CreateCalls++;
            throw new InvalidOperationException("The read-only resolver must not create folders.");
        }

        public void Dispose()
        {
        }
    }
}
