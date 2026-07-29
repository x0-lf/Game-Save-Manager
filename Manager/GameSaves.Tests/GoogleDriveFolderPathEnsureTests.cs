using System.Collections.Concurrent;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveFolderPathEnsureTests
{
    [Fact]
    public async Task AllFoldersExist_ReusesEveryAuthoritativeId()
    {
        var api = new FakeObjectApi();
        api.Add(Folder("games-id", "Games", "root-id"));
        api.Add(Folder("run-id", "Run", "games-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games/Run"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("run-id", result.ObjectId);
        Assert.Equal(
            new[] { ("root-id", "Games"), ("games-id", "Run") },
            api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task OneMissingFolder_CreatesOnlyThatFolder()
    {
        var api = new FakeObjectApi();
        api.Add(Folder("games-id", "Games", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games/Run"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Created, result.Status);
        Assert.Equal("created-1", result.ObjectId);
        Assert.Equal(new[] { ("games-id", "Run") }, api.CreateCallsSnapshot);
        Assert.Equal(3, api.ListCalls.Count);
    }

    [Fact]
    public async Task MultipleMissingFolders_CreateOneFolderPerMissingSegment()
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games/Run/Data"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Created, result.Status);
        Assert.Equal("created-3", result.ObjectId);
        Assert.Equal(
            new[]
            {
                ("root-id", "Games"),
                ("created-1", "Run"),
                ("created-2", "Data")
            },
            api.CreateCallsSnapshot);
    }

    [Fact]
    public async Task SameNameFileCollision_ReturnsTypeMismatchWithoutCreating()
    {
        var api = new FakeObjectApi();
        api.Add(FileObject("file-id", "Games", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.TypeMismatch, result.Status);
        Assert.Equal("file-id", result.ObjectId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task DuplicateFolderNames_ReturnAmbiguousWithoutCreating()
    {
        var api = new FakeObjectApi();
        api.Add(Folder("first-id", "Games", "root-id"));
        api.Add(Folder("second-id", "Games", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.ObjectId);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task CandidateAppearingBeforeCreate_IsFoundBySecondSearchAndReused()
    {
        var api = new FakeObjectApi();
        api.ListHandler = (instance, parentId, name, call, _) =>
        {
            if (call == 2)
                instance.Add(Folder("appeared-id", name, parentId));

            return Task.FromResult(instance.Snapshot(parentId, name));
        };
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("appeared-id", result.ObjectId);
        Assert.Equal(2, api.ListCalls.Count);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task ConcurrentCallers_CreateAtMostOneFolderWithinProcess()
    {
        var api = new FakeObjectApi();
        var createEntered = NewSignal();
        var releaseCreate = NewSignal();
        var thirdSearchObserved = NewSignal();

        api.ListHandler = (instance, parentId, name, call, _) =>
        {
            if (call == 3)
                thirdSearchObserved.TrySetResult();

            return Task.FromResult(instance.Snapshot(parentId, name));
        };
        api.CreateHandler = async (instance, parentId, name, _, cancellationToken) =>
        {
            createEntered.TrySetResult();
            await releaseCreate.Task.WaitAsync(cancellationToken);
            return instance.AddCreated(parentId, name);
        };

        using GoogleAuthorizedCredential firstCredential = Credential();
        using GoogleAuthorizedCredential secondCredential = Credential();
        var coordinator = new GoogleDriveObjectCreationCoordinator();
        var firstResolver = new GoogleDriveObjectPathResolver(
            api,
            firstCredential,
            coordinator);
        var secondResolver = new GoogleDriveObjectPathResolver(
            api,
            secondCredential,
            coordinator);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Games");

        Task<GoogleDriveObjectResolutionResult> first =
            firstResolver.EnsureFolderPathAsync("root-id", path);
        await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<GoogleDriveObjectResolutionResult> second =
            secondResolver.EnsureFolderPathAsync("root-id", path);
        await thirdSearchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, api.CreateCalls);
        releaseCreate.TrySetResult();

        GoogleDriveObjectResolutionResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, api.CreateCalls);
        Assert.All(results, result => Assert.True(
            result.Status is GoogleDriveObjectResolutionStatus.Found or
                GoogleDriveObjectResolutionStatus.Created));
        Assert.Single(results, result =>
            result.Status == GoogleDriveObjectResolutionStatus.Created);
        Assert.Equal(results[0].ObjectId, results[1].ObjectId);
    }

    [Theory]
    [InlineData("wrong-name", (int)GoogleDriveObjectResolutionStatus.Failed)]
    [InlineData("wrong-parent", (int)GoogleDriveObjectResolutionStatus.Failed)]
    [InlineData("wrong-type", (int)GoogleDriveObjectResolutionStatus.TypeMismatch)]
    [InlineData("trashed", (int)GoogleDriveObjectResolutionStatus.Trashed)]
    public async Task InvalidCreateResponse_IsRejected(
        string responseKind,
        int expectedStatus)
    {
        var api = new FakeObjectApi();
        api.CreateHandler = (_, parentId, name, _, _) => Task.FromResult(
            responseKind switch
            {
                "wrong-name" => Folder("created-id", "Different", parentId),
                "wrong-parent" => Folder("created-id", name, "other-parent"),
                "wrong-type" => FileObject("created-id", name, parentId),
                "trashed" => Folder("created-id", name, parentId, trashed: true),
                _ => throw new InvalidOperationException("Unknown test response.")
            });
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"));

        Assert.Equal((GoogleDriveObjectResolutionStatus)expectedStatus, result.Status);
        Assert.NotEqual(GoogleDriveObjectResolutionStatus.Created, result.Status);
        if (result.Status == GoogleDriveObjectResolutionStatus.Failed)
        {
            Assert.Equal(
                GoogleDriveObjectResolutionErrorCodes.InvalidCreateResponse,
                result.ErrorCode);
        }
        Assert.DoesNotContain("created-id", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreationCancellation_ReleasesCoordinationAndDoesNotClaimSuccess()
    {
        var api = new FakeObjectApi();
        var createEntered = NewSignal();
        api.CreateHandler = async (_, _, _, _, cancellationToken) =>
        {
            createEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled create should not complete.");
        };
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);
        using var cancellation = new CancellationTokenSource();

        Task<GoogleDriveObjectResolutionResult> operation =
            resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"),
                cancellation.Token);
        await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, api.CreateCalls);
        Assert.Empty(api.Snapshot("root-id", "Games"));

        api.CreateHandler = null;
        GoogleDriveObjectResolutionResult retried = await resolver
            .EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(GoogleDriveObjectResolutionStatus.Created, retried.Status);
        Assert.Equal(2, api.CreateCalls);
    }

    [Fact]
    public async Task RetryAfterPartialCreate_RediscoversRemoteFolderWithoutCreatingAgain()
    {
        var api = new FakeObjectApi();
        api.CreateHandler = (instance, parentId, name, call, _) =>
        {
            GoogleDriveObjectMetadata created = instance.AddCreated(parentId, name);
            if (call == 1)
                throw new InvalidOperationException("Simulated local processing failure.");

            return Task.FromResult(created);
        };
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Games");

        GoogleDriveObjectResolutionResult first =
            await resolver.EnsureFolderPathAsync("root-id", path);
        GoogleDriveObjectResolutionResult retried =
            await resolver.EnsureFolderPathAsync("root-id", path);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Failed, first.Status);
        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, retried.Status);
        Assert.Equal("created-1", retried.ObjectId);
        Assert.Equal(1, api.CreateCalls);
    }

    [Fact]
    public async Task SharedDriveCreateResponse_IsRejectedWithoutDeletion()
    {
        var api = new FakeObjectApi();
        api.CreateHandler = (_, parentId, name, _, _) => Task.FromResult(
            Folder(
                "shared-id",
                name,
                parentId,
                driveId: "shared-drive-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Parse("Games"));

        Assert.Equal(
            GoogleDriveObjectResolutionStatus.UnsupportedLocation,
            result.Status);
        Assert.Equal(1, api.CreateCalls);
        AssertNoDestructiveApiSurface();
    }

    [Fact]
    public async Task RootPath_ReusesConfiguredRootWithoutApiCalls()
    {
        var api = new FakeObjectApi();
        using GoogleAuthorizedCredential credential = Credential();
        var resolver = Resolver(api, credential);

        GoogleDriveObjectResolutionResult result =
            await resolver.EnsureFolderPathAsync(
                "root-id",
                GoogleDriveRelativePath.Root);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("root-id", result.ObjectId);
        Assert.Empty(api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public void ObjectApi_ProvidesNoDeleteMoveRenameOverwriteOrTransferOperation()
    {
        AssertNoDestructiveApiSurface();
    }

    private static void AssertNoDestructiveApiSurface()
    {
        string[] methods = typeof(IGoogleDriveObjectApi)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(methods, name =>
            name.Contains("Delete", StringComparison.Ordinal) ||
            name.Contains("Move", StringComparison.Ordinal) ||
            name.Contains("Rename", StringComparison.Ordinal) ||
            name.Contains("Trash", StringComparison.Ordinal) ||
            name.Contains("Overwrite", StringComparison.Ordinal) ||
            name.Contains("Upload", StringComparison.Ordinal) ||
            name.Contains("Download", StringComparison.Ordinal));
    }

    private static GoogleDriveObjectPathResolver Resolver(
        IGoogleDriveObjectApi api,
        GoogleAuthorizedCredential credential) =>
        new(api, credential, new GoogleDriveObjectCreationCoordinator());

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        private readonly object _gate = new();
        private readonly Dictionary<(string ParentId, string Name),
            List<GoogleDriveObjectMetadata>> _objects = new();
        private readonly ConcurrentQueue<(string ParentId, string Name)> _listCalls =
            new();
        private readonly ConcurrentQueue<(string ParentId, string Name)> _createCalls =
            new();
        private int _listSequence;
        private int _createSequence;

        public Func<FakeObjectApi, string, string, int, CancellationToken,
            Task<IReadOnlyList<GoogleDriveObjectMetadata>>>? ListHandler { get; set; }

        public Func<FakeObjectApi, string, string, int, CancellationToken,
            Task<GoogleDriveObjectMetadata>>? CreateHandler { get; set; }

        public IReadOnlyList<(string ParentId, string Name)> ListCalls =>
            _listCalls.ToArray();

        public IReadOnlyList<(string ParentId, string Name)> CreateCallsSnapshot =>
            _createCalls.ToArray();

        public int CreateCalls => Volatile.Read(ref _createSequence);

        public void Add(GoogleDriveObjectMetadata metadata)
        {
            string parentId = Assert.Single(metadata.ParentIds);
            lock (_gate)
            {
                var key = (parentId, metadata.Name);
                if (!_objects.TryGetValue(key, out var objects))
                {
                    objects = new List<GoogleDriveObjectMetadata>();
                    _objects.Add(key, objects);
                }

                objects.Add(metadata);
            }
        }

        public GoogleDriveObjectMetadata AddCreated(string parentId, string name)
        {
            var metadata = Folder(
                $"created-{Volatile.Read(ref _createSequence)}",
                name,
                parentId);
            Add(metadata);
            return metadata;
        }

        public IReadOnlyList<GoogleDriveObjectMetadata> Snapshot(
            string parentId,
            string name)
        {
            lock (_gate)
            {
                return _objects.TryGetValue((parentId, name), out var objects)
                    ? objects.ToArray()
                    : Array.Empty<GoogleDriveObjectMetadata>();
            }
        }

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Folder ensuring must not fetch by ID.");

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listCalls.Enqueue((parentId, name));
            int call = Interlocked.Increment(ref _listSequence);

            return ListHandler is null
                ? Task.FromResult(Snapshot(parentId, name))
                : ListHandler(this, parentId, name, call, cancellationToken);
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _createCalls.Enqueue((parentId, name));
            int call = Interlocked.Increment(ref _createSequence);

            return CreateHandler is null
                ? Task.FromResult(AddCreated(parentId, name))
                : CreateHandler(this, parentId, name, call, cancellationToken);
        }
    }
}
