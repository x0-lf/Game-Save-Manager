using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveFolderExistenceServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("45b18fb0-591f-47dc-a5ac-4ce365394387");

    [Fact]
    public async Task EmptyPath_ResolvesTheAuthoritativeRootAsAFolder()
    {
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(_ => resolver);
        var service = new GoogleDriveFolderExistenceService(contexts);

        bool exists = await service.ExistsAsync(ProfileId, string.Empty);

        Assert.True(exists);
        ResolveCall call = Assert.Single(resolver.ResolveCalls);
        Assert.Equal(RecordingContextFactory.RootId, call.RootFolderId);
        Assert.True(call.Path.IsRoot);
        Assert.Equal(GoogleDriveObjectKind.Folder, call.ExpectedKind);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    [Theory]
    [InlineData("nested/run")]
    [InlineData("保存/ゲーム")]
    [InlineData("O'Brien/Run")]
    public async Task ExistingFolder_PreservesExactDrivePath(string relativeFolder)
    {
        var resolver = new RecordingResolver();
        var contexts = new RecordingContextFactory(_ => resolver);
        var service = new GoogleDriveFolderExistenceService(contexts);

        bool exists = await service.ExistsAsync(ProfileId, relativeFolder);

        Assert.True(exists);
        ResolveCall call = Assert.Single(resolver.ResolveCalls);
        Assert.Equal(relativeFolder, call.Path.Canonical);
        Assert.Equal(relativeFolder.Split('/'), call.Path.Segments);
        Assert.Equal(GoogleDriveObjectKind.Folder, call.ExpectedKind);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Fact]
    public async Task MissingFolder_ReturnsFalseWithoutCreation()
    {
        var resolver = new RecordingResolver
        {
            ResultFactory = path => Resolution(
                GoogleDriveObjectResolutionStatus.NotFound,
                path)
        };
        var service = new GoogleDriveFolderExistenceService(
            new RecordingContextFactory(_ => resolver));

        bool exists = await service.ExistsAsync(ProfileId, "missing/run");

        Assert.False(exists);
        Assert.Single(resolver.ResolveCalls);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Fact]
    public async Task DuplicateNames_FailClosedWithoutSelectingOrCreating()
    {
        var api = new RecordingObjectApi();
        api.SetChildren(
            "root-id",
            Folder("first-id", "duplicate", "root-id"),
            Folder("second-id", "duplicate", "root-id"));
        var service = new GoogleDriveFolderExistenceService(
            ResolverContexts(api, new GoogleDriveObjectIdCache()));

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ExistsAsync(ProfileId, "duplicate"));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.Failed,
            exception.Result.Status);
        Assert.Equal(1, api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
        Assert.DoesNotContain("duplicate", exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameNameFileCollision_FailsClosedAsWrongType()
    {
        var api = new RecordingObjectApi();
        api.SetChild("root-id", File("file-id", "run", "root-id"));
        var service = new GoogleDriveFolderExistenceService(
            ResolverContexts(api, new GoogleDriveObjectIdCache()));

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ExistsAsync(ProfileId, "run"));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootWrongType,
            exception.Result.Status);
        Assert.Equal(1, api.ListCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task InvalidFoundResult_FailsClosedInsteadOfReturningTrue()
    {
        var resolver = new RecordingResolver
        {
            ResultFactory = path => Resolution(
                GoogleDriveObjectResolutionStatus.Found,
                path,
                GoogleDriveObjectKind.File)
        };
        var service = new GoogleDriveFolderExistenceService(
            new RecordingContextFactory(_ => resolver));

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ExistsAsync(ProfileId, "run"));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.Failed,
            exception.Result.Status);
        Assert.Equal(0, resolver.EnsureCalls);
    }

    [Theory]
    [InlineData((int)GoogleDriveObjectResolutionStatus.Trashed,
        (int)GoogleDriveRemoteValidationStatus.RootTrashed)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.UnsupportedLocation,
        (int)GoogleDriveRemoteValidationStatus.RootUnsupportedLocation)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.AccessDenied,
        (int)GoogleDriveRemoteValidationStatus.RootInaccessible)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.ReauthenticationRequired,
        (int)GoogleDriveRemoteValidationStatus.ReauthenticationRequired)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.RateLimited,
        (int)GoogleDriveRemoteValidationStatus.RateLimited)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.QuotaExceeded,
        (int)GoogleDriveRemoteValidationStatus.QuotaExceeded)]
    [InlineData((int)GoogleDriveObjectResolutionStatus.Unavailable,
        (int)GoogleDriveRemoteValidationStatus.Unavailable)]
    public async Task UnsafeOrTemporaryState_FailsClosedWithSafeCategory(
        int resolutionStatusValue,
        int expectedStatusValue)
    {
        var resolutionStatus =
            (GoogleDriveObjectResolutionStatus)resolutionStatusValue;
        var expectedStatus =
            (GoogleDriveRemoteValidationStatus)expectedStatusValue;
        var resolver = new RecordingResolver
        {
            ResultFactory = path => Resolution(resolutionStatus, path)
        };
        var service = new GoogleDriveFolderExistenceService(
            new RecordingContextFactory(_ => resolver));

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.ExistsAsync(ProfileId, "run"));

        Assert.Equal(expectedStatus, exception.Result.Status);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.DoesNotContain("run", exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticationFailure_PropagatesTheSafeContextFailure()
    {
        var contexts = new RecordingContextFactory(_ => new RecordingResolver())
        {
            Failure = GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.AuthorizationRevoked)
        };
        var service = new GoogleDriveFolderExistenceService(contexts);

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(
                () => service.ExistsAsync(ProfileId, "run"));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.AuthorizationRevoked,
            exception.Result.Status);
        Assert.Null(contexts.LastCredential);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDisposesTheContext()
    {
        var resolver = new RecordingResolver { Cancel = true };
        var contexts = new RecordingContextFactory(_ => resolver);
        var service = new GoogleDriveFolderExistenceService(contexts);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExistsAsync(
                ProfileId,
                "run",
                cancellation.Token));

        Assert.Equal(cancellation.Token,
            Assert.Single(resolver.ResolveCalls).CancellationToken);
        Assert.Equal(0, resolver.EnsureCalls);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task ValidatedCachedIds_AreReusedWithoutAnotherNameLookup()
    {
        var api = new RecordingObjectApi();
        api.SetChild("root-id", Folder("parent-id", "Games", "root-id"));
        api.SetChild("parent-id", Folder("run-id", "Run", "parent-id"));
        var cache = new GoogleDriveObjectIdCache();
        var contexts = ResolverContexts(api, cache);
        var service = new GoogleDriveFolderExistenceService(contexts);

        Assert.True(await service.ExistsAsync(ProfileId, "Games/Run"));
        int listsAfterFirstCall = api.ListCalls;
        Assert.True(await service.ExistsAsync(ProfileId, "Games/Run"));

        Assert.Equal(listsAfterFirstCall, api.ListCalls);
        Assert.Equal(2, api.GetCalls);
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public async Task StaleCachedFolder_IsEvictedAndResolvedOnceByExactName()
    {
        var api = new RecordingObjectApi();
        api.SetChild("root-id", Folder("old-id", "Run", "root-id"));
        var cache = new GoogleDriveObjectIdCache();
        var contexts = ResolverContexts(api, cache);
        var service = new GoogleDriveFolderExistenceService(contexts);
        Assert.True(await service.ExistsAsync(ProfileId, "Run"));
        int listsAfterFirstCall = api.ListCalls;

        api.SetMetadata(Folder("old-id", "Renamed", "root-id"));
        api.SetChild("root-id", Folder("new-id", "Run", "root-id"));

        Assert.True(await service.ExistsAsync(ProfileId, "Run"));

        Assert.Equal(listsAfterFirstCall + 1, api.ListCalls);
        Assert.Equal(1, api.GetCalls);
        Assert.Equal(0, api.CreateCalls);
        Assert.True(cache.TryGet(
            new GoogleDriveObjectCacheScope(ProfileId, "root-id"),
            "root-id",
            "Run",
            GoogleDriveObjectKind.Folder,
            out GoogleDriveObjectIdCacheEntry? current));
        Assert.NotNull(current);
        Assert.Equal("new-id", current.ObjectId);
    }

    private static RecordingContextFactory ResolverContexts(
        IGoogleDriveObjectApi api,
        IGoogleDriveObjectIdCache cache) =>
        new(credential => new GoogleDriveObjectPathResolver(
            api,
            credential,
            new GoogleDriveObjectCreationCoordinator(),
            cache,
            ProfileId));

    private static GoogleDriveObjectResolutionResult Resolution(
        GoogleDriveObjectResolutionStatus status,
        GoogleDriveRelativePath path,
        GoogleDriveObjectKind objectKind = GoogleDriveObjectKind.Folder) =>
        new(
            status,
            path,
            objectKind,
            objectId: status == GoogleDriveObjectResolutionStatus.Found
                ? "resolved-folder-id"
                : null,
            errorCode: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : GoogleDriveObjectResolutionErrorCodes.Failed,
            message: status == GoogleDriveObjectResolutionStatus.Found
                ? null
                : "The folder could not be resolved safely.");

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed: false,
            new[] { parentId },
            driveId: null);

    private static GoogleDriveObjectMetadata File(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            "application/json",
            trashed: false,
            new[] { parentId },
            driveId: null);

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        public const string RootId = "root-id";

        private readonly Func<GoogleAuthorizedCredential,
            IGoogleDriveObjectPathResolver> _resolverFactory;

        public RecordingContextFactory(
            Func<GoogleAuthorizedCredential,
                IGoogleDriveObjectPathResolver> resolverFactory) =>
            _resolverFactory = resolverFactory;

        public GoogleDriveRemoteValidationResult? Failure { get; set; }

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw new GoogleDriveRemoteOperationContextException(Failure);

            LastCredential = Credential();
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                LastCredential,
                _resolverFactory(LastCredential)));
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public Func<GoogleDriveRelativePath,
            GoogleDriveObjectResolutionResult> ResultFactory { get; set; } =
            path => Resolution(GoogleDriveObjectResolutionStatus.Found, path);

        public bool Cancel { get; set; }

        public List<ResolveCall> ResolveCalls { get; } = new();

        public int EnsureCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Folder existence must resolve the complete relative path.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls.Add(new ResolveCall(
                rootFolderId,
                relativePath,
                expectedFinalKind,
                cancellationToken));
            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ResultFactory(relativePath));
        }

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            throw new InvalidOperationException(
                "Folder existence must never create a Drive folder.");
        }
    }

    private sealed class RecordingObjectApi : IGoogleDriveObjectApi
    {
        private readonly Dictionary<(string ParentId, string Name),
            IReadOnlyList<GoogleDriveObjectMetadata>> _children = new();
        private readonly Dictionary<string, GoogleDriveObjectMetadata> _metadata =
            new(StringComparer.Ordinal);

        public int GetCalls { get; private set; }

        public int ListCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public void SetChild(string parentId, GoogleDriveObjectMetadata child)
            => SetChildren(parentId, child);

        public void SetChildren(
            string parentId,
            params GoogleDriveObjectMetadata[] children)
        {
            if (children.Length == 0)
                throw new ArgumentException("At least one child is required.", nameof(children));

            string name = children[0].Name;
            if (children.Any(child =>
                !string.Equals(child.Name, name, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Recorded children must share one exact name.",
                    nameof(children));
            }

            _children[(parentId, name)] = children;
            foreach (GoogleDriveObjectMetadata child in children)
                _metadata[child.Id] = child;
        }

        public void SetMetadata(GoogleDriveObjectMetadata metadata) =>
            _metadata[metadata.Id] = metadata;

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult(_metadata[objectId]);
        }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            return Task.FromResult(
                _children.TryGetValue((parentId, name), out var children)
                    ? children
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
            throw new InvalidOperationException(
                "Folder existence must never create a Drive folder.");
        }
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

    private sealed record ResolveCall(
        string RootFolderId,
        GoogleDriveRelativePath Path,
        GoogleDriveObjectKind? ExpectedKind,
        CancellationToken CancellationToken);
}
