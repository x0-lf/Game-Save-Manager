using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveObjectIdCacheTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task UniqueEntry_IsValidatedByIdBeforeCrossCallReuse()
    {
        var api = new CacheObjectApi();
        api.SetChildren("root-id", "Saves", Folder("saves-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);

        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Saves");
        GoogleDriveObjectResolutionResult first = await resolver.ResolveAsync(
            "root-id", path, GoogleDriveObjectKind.Folder);
        GoogleDriveObjectResolutionResult second = await resolver.ResolveAsync(
            "root-id", path, GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, first.Status);
        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, second.Status);
        Assert.Single(api.ListCalls);
        Assert.Equal(new[] { "saves-id" }, api.GetCalls);
    }

    [Fact]
    public async Task MissingCachedObject_IsEvictedAndExactNameResolutionRetriesOnce()
    {
        var api = new CacheObjectApi();
        api.SetChildren("root-id", "Saves", Folder("old-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Saves");
        await resolver.ResolveAsync("root-id", path, GoogleDriveObjectKind.Folder);

        api.RemoveById("old-id");
        api.SetChildren("root-id", "Saves", Folder("new-id", "Saves", "root-id"));

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id", path, GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, result.Status);
        Assert.Equal("new-id", result.ObjectId);
        Assert.Equal(2, api.ListCalls.Count);
        Assert.Equal(new[] { "old-id" }, api.GetCalls);
    }

    [Theory]
    [InlineData(
        StaleChange.Trashed,
        (int)GoogleDriveObjectResolutionStatus.NotFound)]
    [InlineData(
        StaleChange.Renamed,
        (int)GoogleDriveObjectResolutionStatus.NotFound)]
    [InlineData(
        StaleChange.Moved,
        (int)GoogleDriveObjectResolutionStatus.NotFound)]
    [InlineData(
        StaleChange.WrongType,
        (int)GoogleDriveObjectResolutionStatus.TypeMismatch)]
    [InlineData(
        StaleChange.SharedDrive,
        (int)GoogleDriveObjectResolutionStatus.UnsupportedLocation)]
    public async Task StaleCachedMetadata_IsEvictedAndNormalResolutionRunsOnce(
        StaleChange change,
        int expectedStatusValue)
    {
        var api = new CacheObjectApi();
        api.SetChildren("root-id", "Saves", Folder("cached-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Saves");
        await resolver.ResolveAsync("root-id", path, GoogleDriveObjectKind.Folder);

        GoogleDriveObjectMetadata stale = change switch
        {
            StaleChange.Trashed => Folder("cached-id", "Saves", "root-id", trashed: true),
            StaleChange.Renamed => Folder("cached-id", "Renamed", "root-id"),
            StaleChange.Moved => Folder("cached-id", "Saves", "different-parent"),
            StaleChange.WrongType => FileObject("cached-id", "Saves", "root-id"),
            StaleChange.SharedDrive => Folder(
                "cached-id", "Saves", "root-id", driveId: "shared-drive-id"),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        api.SetById(stale);
        api.SetChildren(
            "root-id",
            "Saves",
            change switch
            {
                StaleChange.WrongType => new[] { stale },
                StaleChange.SharedDrive => new[] { stale },
                _ => Array.Empty<GoogleDriveObjectMetadata>()
            });

        GoogleDriveObjectResolutionResult result = await resolver.ResolveAsync(
            "root-id", path, GoogleDriveObjectKind.Folder);

        Assert.Equal((GoogleDriveObjectResolutionStatus)expectedStatusValue, result.Status);
        Assert.Equal(2, api.ListCalls.Count);
        Assert.Equal(new[] { "cached-id" }, api.GetCalls);
        Assert.False(cache.TryGet(
            Scope("root-id"),
            "root-id",
            "Saves",
            GoogleDriveObjectKind.Folder,
            out _));
    }

    [Fact]
    public async Task StaleFolder_ClearsDescendantEntriesInTheSameRootScope()
    {
        var api = new CacheObjectApi();
        api.SetChildren("root-id", "Parent", Folder("parent-id", "Parent", "root-id"));
        api.SetChildren("parent-id", "Child", Folder("child-id", "Child", "parent-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);
        await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Parent/Child"),
            GoogleDriveObjectKind.Folder);

        api.SetById(Folder("parent-id", "Renamed", "root-id"));
        api.SetChildren("root-id", "Parent");

        await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Parent"),
            GoogleDriveObjectKind.Folder);

        Assert.False(cache.TryGet(
            Scope("root-id"),
            "parent-id",
            "Child",
            GoogleDriveObjectKind.Folder,
            out _));
    }

    [Fact]
    public void CacheScopes_SeparateDifferentRootsAndProfiles()
    {
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectCacheScope firstRoot = Scope("first-root");
        GoogleDriveObjectCacheScope secondRoot = Scope("second-root");
        var otherProfile = new GoogleDriveObjectCacheScope(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "first-root");

        Assert.True(cache.TryStoreUniqueValidated(
            firstRoot, "parent", "Name", GoogleDriveObjectKind.Folder,
            Folder("first-id", "Name", "parent")));

        Assert.True(cache.TryGet(
            firstRoot, "parent", "Name", GoogleDriveObjectKind.Folder, out _));
        Assert.False(cache.TryGet(
            secondRoot, "parent", "Name", GoogleDriveObjectKind.Folder, out _));
        Assert.False(cache.TryGet(
            otherProfile, "parent", "Name", GoogleDriveObjectKind.Folder, out _));
    }

    [Fact]
    public async Task AmbiguousAndFailedResults_AreNeverCached()
    {
        var api = new CacheObjectApi();
        api.SetChildren(
            "root-id",
            "Saves",
            Folder("first-id", "Saves", "root-id"),
            Folder("second-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);

        GoogleDriveObjectResolutionResult ambiguous = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Saves"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.False(cache.TryGet(
            Scope("root-id"), "root-id", "Saves", GoogleDriveObjectKind.Folder, out _));

        api.ListFailure = GoogleDriveApiFailureMapper.Create(
            GoogleDriveApiOperation.ObjectChildList,
            GoogleDriveApiFailure.Unavailable,
            "GoogleDriveObjectUnavailable",
            retryable: true);
        GoogleDriveObjectResolutionResult failed = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Other"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Unavailable, failed.Status);
        Assert.False(cache.TryGet(
            Scope("root-id"), "root-id", "Other", GoogleDriveObjectKind.Folder, out _));
    }

    [Fact]
    public void Cache_RejectsObjectsThatAreNotSafeUniqueMyDriveMatches()
    {
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectCacheScope scope = Scope("root-id");

        Assert.False(cache.TryStoreUniqueValidated(
            scope, "root-id", "Saves", GoogleDriveObjectKind.Folder,
            Folder("trashed", "Saves", "root-id", trashed: true)));
        Assert.False(cache.TryStoreUniqueValidated(
            scope, "root-id", "Saves", GoogleDriveObjectKind.Folder,
            Folder("shared", "Saves", "root-id", driveId: "drive-id")));
        Assert.False(cache.TryStoreUniqueValidated(
            scope, "root-id", "Saves", GoogleDriveObjectKind.Folder,
            Folder("renamed", "Other", "root-id")));
        Assert.False(cache.TryStoreUniqueValidated(
            scope, "root-id", "Saves", GoogleDriveObjectKind.Folder,
            Folder("moved", "Saves", "other-parent")));
        Assert.False(cache.TryStoreUniqueValidated(
            scope, "root-id", "Saves", GoogleDriveObjectKind.Folder,
            FileObject("file", "Saves", "root-id")));
    }

    [Theory]
    [InlineData((int)GoogleDriveObjectCacheInvalidationReason.AccountReconnect)]
    [InlineData((int)GoogleDriveObjectCacheInvalidationReason.AccountDisconnect)]
    [InlineData((int)GoogleDriveObjectCacheInvalidationReason.ApplicationRootReplacement)]
    [InlineData((int)GoogleDriveObjectCacheInvalidationReason.ProfileDeletion)]
    [InlineData((int)GoogleDriveObjectCacheInvalidationReason.AuthorizationRevocation)]
    public void LifecycleInvalidation_ClearsEveryRootForTheProfile(
        int reasonValue)
    {
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectCacheScope first = Scope("first-root");
        GoogleDriveObjectCacheScope second = Scope("second-root");
        cache.TryStoreUniqueValidated(
            first, "parent", "One", GoogleDriveObjectKind.Folder,
            Folder("one-id", "One", "parent"));
        cache.TryStoreUniqueValidated(
            second, "parent", "Two", GoogleDriveObjectKind.Folder,
            Folder("two-id", "Two", "parent"));

        cache.InvalidateProfile(
            ProfileId,
            (GoogleDriveObjectCacheInvalidationReason)reasonValue);

        Assert.False(cache.TryGet(
            first, "parent", "One", GoogleDriveObjectKind.Folder, out _));
        Assert.False(cache.TryGet(
            second, "parent", "Two", GoogleDriveObjectKind.Folder, out _));
    }

    [Fact]
    public async Task Cancellation_PreventsCachedMetadataValidation()
    {
        var api = new CacheObjectApi();
        api.SetChildren("root-id", "Saves", Folder("saves-id", "Saves", "root-id"));
        using GoogleAuthorizedCredential credential = Credential();
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectPathResolver resolver = Resolver(api, credential, cache);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Saves");
        await resolver.ResolveAsync("root-id", path, GoogleDriveObjectKind.Folder);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(
                "root-id", path, GoogleDriveObjectKind.Folder, cancellation.Token));

        Assert.Empty(api.GetCalls);
    }

    [Fact]
    public async Task Cache_IsThreadSafeUnderConcurrentStoreReadAndInvalidation()
    {
        var cache = new GoogleDriveObjectIdCache();
        GoogleDriveObjectCacheScope scope = Scope("root-id");

        Task[] workers = Enumerable.Range(0, 8)
            .Select(worker => Task.Run(() =>
            {
                for (int index = 0; index < 250; index++)
                {
                    string name = $"Folder-{worker}-{index}";
                    cache.TryStoreUniqueValidated(
                        scope,
                        "parent-id",
                        name,
                        GoogleDriveObjectKind.Folder,
                        Folder($"id-{worker}-{index}", name, "parent-id"));
                    cache.TryGet(
                        scope,
                        "parent-id",
                        name,
                        GoogleDriveObjectKind.Folder,
                        out _);

                    if (index % 25 == 0)
                        cache.ClearScope(scope);
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);
        cache.InvalidateProfile(
            ProfileId,
            GoogleDriveObjectCacheInvalidationReason.ProfileDeletion);
    }

    [Fact]
    public void Cache_IsInMemoryOnlyAndRegisteredAsOneInfrastructureService()
    {
        Assert.Empty(typeof(GoogleDriveObjectIdCache).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters()));
        Assert.DoesNotContain(
            typeof(GoogleDriveObjectIdCache).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            field =>
                field.FieldType.FullName?.Contains("Sqlite", StringComparison.Ordinal) == true ||
                field.FieldType.FullName?.Contains("Repository", StringComparison.Ordinal) == true ||
                field.FieldType.FullName?.Contains("File", StringComparison.Ordinal) == true);

        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveObjectIdCache>(
            provider.GetRequiredService<IGoogleDriveObjectIdCache>());
        Assert.Same(
            provider.GetRequiredService<IGoogleDriveObjectIdCache>(),
            provider.GetRequiredService<IGoogleDriveObjectIdCache>());
    }

    private static GoogleDriveObjectPathResolver Resolver(
        IGoogleDriveObjectApi api,
        GoogleAuthorizedCredential credential,
        IGoogleDriveObjectIdCache cache) =>
        new(
            api,
            credential,
            new GoogleDriveObjectCreationCoordinator(),
            cache,
            ProfileId);

    private static GoogleDriveObjectCacheScope Scope(string rootFolderId) =>
        new(ProfileId, rootFolderId);

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

    public enum StaleChange
    {
        Trashed,
        Renamed,
        Moved,
        WrongType,
        SharedDrive
    }

    private sealed class CacheObjectApi : IGoogleDriveObjectApi
    {
        private readonly Dictionary<(string ParentId, string Name),
            IReadOnlyList<GoogleDriveObjectMetadata>> _children = new();
        private readonly Dictionary<string, GoogleDriveObjectMetadata> _byId =
            new(StringComparer.Ordinal);

        public List<(string ParentId, string Name)> ListCalls { get; } = new();
        public List<string> GetCalls { get; } = new();
        public GoogleDriveApiException? ListFailure { get; set; }

        public void SetChildren(
            string parentId,
            string name,
            params GoogleDriveObjectMetadata[] children)
        {
            _children[(parentId, name)] = children;
            foreach (GoogleDriveObjectMetadata child in children)
                _byId[child.Id] = child;
        }

        public void SetById(GoogleDriveObjectMetadata metadata) =>
            _byId[metadata.Id] = metadata;

        public void RemoveById(string objectId) => _byId.Remove(objectId);

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls.Add(objectId);
            if (_byId.TryGetValue(objectId, out GoogleDriveObjectMetadata? metadata))
                return Task.FromResult(metadata);

            throw GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectMetadataGet,
                GoogleDriveApiFailure.NotFound,
                "GoogleDriveObjectNotFound");
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
            if (ListFailure is not null)
                throw ListFailure;

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
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This cache test does not create folders.");
    }
}
