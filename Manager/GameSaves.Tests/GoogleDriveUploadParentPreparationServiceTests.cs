using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadParentPreparationServiceTests
{
    private static readonly Guid ProfileId = Guid.Parse(
        "9b423317-0915-4bef-b381-a6514dd58af2");

    [Fact]
    public async Task ExistingSegments_ReuseAuthoritativeIdsAndRemoteSeparators()
    {
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("first-id", "Parent\\One", "root-id")],
            [Folder("second-id", "Child", "first-id")]
        ]);
        GoogleDriveUploadParentPreparationService service = Service(enumeration);
        using GoogleDriveRemoteOperationContext context = Context();

        string parentId = await service.PrepareAsync(
            context,
            GoogleDriveRelativePath.Parse("Parent\\One/Child"));

        Assert.Equal("second-id", parentId);
        Assert.Equal(new[] { "root-id", "first-id" }, enumeration.ParentIds);
    }

    [Fact]
    public async Task RootPath_ReusesConfiguredRootWithoutListing()
    {
        var enumeration = new RecordingChildEnumerationService([]);
        GoogleDriveUploadParentPreparationService service = Service(enumeration);
        using GoogleDriveRemoteOperationContext context = Context();

        string parentId = await service.PrepareAsync(
            context,
            GoogleDriveRelativePath.Root);

        Assert.Equal("root-id", parentId);
        Assert.Empty(enumeration.ParentIds);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("file")]
    public async Task ConflictingCandidate_StopsBeforeNextSegment(string scenario)
    {
        IReadOnlyList<GoogleDriveFolderChildEntry> children = scenario switch
        {
            "duplicate" =>
            [
                Folder("first-id", "Parent", "root-id"),
                Folder("second-id", "Parent", "root-id")
            ],
            "case" => [Folder("first-id", "PARENT", "root-id")],
            "file" => [File("first-id", "Parent", "root-id")],
            _ => throw new InvalidOperationException("Unknown scenario.")
        };
        string expectedCode = scenario switch
        {
            "duplicate" => GoogleDriveUploadParentPreparationErrorCodes.Ambiguous,
            "case" => GoogleDriveUploadParentPreparationErrorCodes.CaseCollision,
            "file" => GoogleDriveUploadParentPreparationErrorCodes.TypeCollision,
            _ => throw new InvalidOperationException("Unknown scenario.")
        };
        var enumeration = new RecordingChildEnumerationService([children]);
        GoogleDriveUploadParentPreparationService service = Service(enumeration);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.PrepareAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Parent/Child")));

        Assert.Equal(expectedCode, exception.Result.ErrorCode);
        Assert.Single(enumeration.ParentIds);
        AssertSafe(exception, "Parent", "PARENT", "first-id", "second-id");
    }

    [Theory]
    [InlineData("workspace", GoogleDriveUploadParentPreparationErrorCodes.UnsupportedObject)]
    [InlineData("shortcut", GoogleDriveUploadParentPreparationErrorCodes.UnsupportedObject)]
    [InlineData("trashed", GoogleDriveUploadParentPreparationErrorCodes.InvalidMetadata)]
    [InlineData("shared", GoogleDriveUploadParentPreparationErrorCodes.UnsupportedLocation)]
    [InlineData("parent", GoogleDriveUploadParentPreparationErrorCodes.InvalidMetadata)]
    public async Task InvalidChildSet_FailsClosed(
        string scenario,
        string expectedCode)
    {
        GoogleDriveFolderChildEntry child = scenario switch
        {
            "workspace" => Entry(
                "child-id",
                "unrelated",
                "application/vnd.google-apps.document",
                GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument,
                "root-id"),
            "shortcut" => Entry(
                "child-id",
                "unrelated",
                "application/vnd.google-apps.shortcut",
                GoogleDriveRecursiveObjectKind.Shortcut,
                "root-id"),
            "trashed" => Entry(
                "child-id",
                "unrelated",
                "application/octet-stream",
                GoogleDriveRecursiveObjectKind.BlobFile,
                "root-id",
                trashed: true),
            "shared" => Entry(
                "child-id",
                "unrelated",
                "application/octet-stream",
                GoogleDriveRecursiveObjectKind.BlobFile,
                "root-id",
                driveId: "shared-id"),
            "parent" => File("child-id", "unrelated", "other-parent"),
            _ => throw new InvalidOperationException("Unknown scenario.")
        };
        GoogleDriveUploadParentPreparationService service = Service(
            new RecordingChildEnumerationService([[child]]));
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.PrepareAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Parent")));

        Assert.Equal(expectedCode, exception.Result.ErrorCode);
        AssertSafe(exception, "Parent", "child-id", "shared-id", "other-parent");
    }

    [Fact]
    public async Task MissingSegments_CreateOneFolderEachUnderCheckedLeases()
    {
        var enumeration = new RecordingChildEnumerationService(
        [
            [], [], [],
            [], [], []
        ]);
        var api = new RecordingObjectApi();
        GoogleDriveUploadParentPreparationService service = Service(enumeration, api);
        using GoogleDriveRemoteOperationContext context = Context();

        string parentId = await service.PrepareAsync(
            context,
            GoogleDriveRelativePath.Parse("Parent/Child"));

        Assert.Equal("created-2", parentId);
        Assert.Equal(
            new[] { ("root-id", "Parent"), ("created-1", "Child") },
            api.CreateCalls);
        Assert.Equal(
            new[]
            {
                "root-id", "root-id", "root-id",
                "created-1", "created-1", "created-1"
            },
            enumeration.ParentIds);
    }

    [Fact]
    public async Task CollisionAppearingInsideLease_PreventsFolderCreate()
    {
        var enumeration = new RecordingChildEnumerationService(
        [
            [],
            [],
            [Folder("racing-id", "PARENT", "root-id")]
        ]);
        var api = new RecordingObjectApi();
        GoogleDriveUploadParentPreparationService service = Service(enumeration, api);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.PrepareAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Parent")));

        Assert.Equal(
            GoogleDriveCreateOnlyUploadTargetErrorCodes.CaseCollision,
            exception.Result.ErrorCode);
        Assert.Empty(api.CreateCalls);
        Assert.Equal(3, enumeration.ParentIds.Count);
        AssertSafe(exception, "Parent", "PARENT", "racing-id");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("name")]
    [InlineData("mime")]
    [InlineData("trashed")]
    [InlineData("shared")]
    [InlineData("missing-parent")]
    [InlineData("wrong-parent")]
    [InlineData("multiple-parents")]
    public async Task InvalidCreateResponse_StopsBeforeCacheOrNextSegment(
        string scenario)
    {
        var enumeration = new RecordingChildEnumerationService([[], [], []]);
        var api = new RecordingObjectApi
        {
            CreateHandler = (parentId, name) => scenario switch
            {
                "null" => null!,
                "name" => Metadata("created-id", "Different", FolderMime(), [parentId]),
                "mime" => Metadata("created-id", name, "application/octet-stream", [parentId]),
                "trashed" => Metadata(
                    "created-id", name, FolderMime(), [parentId], trashed: true),
                "shared" => Metadata(
                    "created-id", name, FolderMime(), [parentId], driveId: "shared-id"),
                "missing-parent" => Metadata(
                    "created-id", name, FolderMime(), []),
                "wrong-parent" => Metadata(
                    "created-id", name, FolderMime(), ["other-parent"]),
                "multiple-parents" => Metadata(
                    "created-id", name, FolderMime(), [parentId, "other-parent"]),
                _ => throw new InvalidOperationException("Unknown scenario.")
            }
        };
        var cache = new RecordingObjectIdCache();
        GoogleDriveUploadParentPreparationService service =
            Service(enumeration, api, cache);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.PrepareAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Parent/Child")));

        Assert.Equal(
            GoogleDriveUploadParentPreparationErrorCodes.InvalidCreateResponse,
            exception.Result.ErrorCode);
        Assert.Empty(cache.Stored);
        Assert.Single(api.CreateCalls);
        Assert.Equal(3, enumeration.ParentIds.Count);
        AssertSafe(
            exception,
            "Parent",
            "Child",
            "created-id",
            "shared-id",
            "other-parent");
    }

    [Fact]
    public async Task ValidExistingAndCreatedFolders_AreCachedBeforeContinuing()
    {
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("existing-id", "Existing", "root-id")],
            [], [], []
        ]);
        var api = new RecordingObjectApi();
        var cache = new RecordingObjectIdCache();
        GoogleDriveUploadParentPreparationService service =
            Service(enumeration, api, cache);
        using GoogleDriveRemoteOperationContext context = Context();

        string parentId = await service.PrepareAsync(
            context,
            GoogleDriveRelativePath.Parse("Existing/Created"));

        Assert.Equal("created-1", parentId);
        Assert.Equal(2, cache.Stored.Count);
        Assert.Equal(
            new[] { "existing-id", "created-1" },
            cache.Stored.Select(entry => entry.Metadata.Id));
        Assert.All(cache.Stored, entry =>
            Assert.Equal(GoogleDriveObjectKind.Folder, entry.Kind));
    }

    [Fact]
    public async Task CacheRejection_StopsBeforeNextSegment()
    {
        var enumeration = new RecordingChildEnumerationService([[], [], []]);
        var api = new RecordingObjectApi();
        var cache = new RecordingObjectIdCache { StoreResult = false };
        GoogleDriveUploadParentPreparationService service =
            Service(enumeration, api, cache);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.PrepareAsync(
                    context,
                    GoogleDriveRelativePath.Parse("Parent/Child")));

        Assert.Equal(
            GoogleDriveUploadParentPreparationErrorCodes.CacheRejected,
            exception.Result.ErrorCode);
        Assert.Single(api.CreateCalls);
        Assert.Single(cache.Stored);
        Assert.Equal(3, enumeration.ParentIds.Count);
        AssertSafe(exception, "Parent", "Child", "created-1");
    }

    [Fact]
    public async Task MissingParentCreation_ForwardsCallerToken()
    {
        var enumeration = new RecordingChildEnumerationService([[], [], []]);
        var api = new RecordingObjectApi();
        GoogleDriveUploadParentPreparationService service = Service(
            enumeration,
            api);
        using GoogleDriveRemoteOperationContext context = Context();
        using var cancellation = new CancellationTokenSource();

        await service.PrepareAsync(
            context,
            GoogleDriveRelativePath.Parse("Parent"),
            cancellation.Token);

        Assert.All(enumeration.CancellationTokens,
            token => Assert.Equal(cancellation.Token, token));
        Assert.Equal([cancellation.Token], api.CancellationTokens);
    }

    [Fact]
    public async Task CancellationAfterFolderCreate_StopsBeforeCacheOrNextSegment()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService([[], [], []]);
        var api = new RecordingObjectApi
        {
            CreateHandler = (parentId, name) =>
            {
                cancellation.Cancel();
                return Metadata(
                    "created-id",
                    name,
                    FolderMime(),
                    [parentId]);
            }
        };
        var cache = new RecordingObjectIdCache();
        GoogleDriveUploadParentPreparationService service = Service(
            enumeration,
            api,
            cache);
        using GoogleDriveRemoteOperationContext context = Context();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareAsync(
                context,
                GoogleDriveRelativePath.Parse("Parent/Child"),
                cancellation.Token));

        Assert.Single(api.CreateCalls);
        Assert.Empty(cache.Stored);
        Assert.Equal(3, enumeration.ParentIds.Count);
    }

    private static GoogleDriveUploadParentPreparationService Service(
        RecordingChildEnumerationService enumeration,
        RecordingObjectApi? objectApi = null,
        RecordingObjectIdCache? objectIdCache = null)
    {
        var coordinator = new GoogleDriveObjectCreationCoordinator();
        return new GoogleDriveUploadParentPreparationService(
            enumeration,
            new GoogleDriveCreateOnlyUploadTargetGuard(
                enumeration,
                coordinator),
            objectApi ?? new RecordingObjectApi(),
            objectIdCache ?? new RecordingObjectIdCache());
    }

    private static GoogleDriveFolderChildEntry Folder(
        string id,
        string name,
        string parentId) =>
        Entry(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            GoogleDriveRecursiveObjectKind.Folder,
            parentId);

    private static GoogleDriveFolderChildEntry File(
        string id,
        string name,
        string parentId) =>
        Entry(
            id,
            name,
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            parentId);

    private static GoogleDriveFolderChildEntry Entry(
        string id,
        string name,
        string mimeType,
        GoogleDriveRecursiveObjectKind kind,
        string parentId,
        bool trashed = false,
        string? driveId = null) =>
        new(
            id,
            name,
            mimeType,
            kind,
            [parentId],
            trashed,
            driveId);

    private static string FolderMime() =>
        GoogleDriveApplicationRoot.FolderMimeType;

    private static GoogleDriveObjectMetadata Metadata(
        string id,
        string name,
        string mimeType,
        IEnumerable<string> parentIds,
        bool trashed = false,
        string? driveId = null) =>
        new(id, name, mimeType, trashed, parentIds, driveId);

    private static void AssertSafe(object value, params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues)
            Assert.DoesNotContain(privateValue, text, StringComparison.Ordinal);
    }

    private static GoogleDriveRemoteOperationContext Context() =>
        new(ProfileId, "root-id", Credential(), new UnusedResolver());

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

    private sealed class RecordingChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        private readonly Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>
            _results;

        public RecordingChildEnumerationService(
            IEnumerable<IReadOnlyList<GoogleDriveFolderChildEntry>> results) =>
            _results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(results);

        public List<string> ParentIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParentIds.Add(parentFolderId);
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingObjectApi : IGoogleDriveObjectApi
    {
        private int _createSequence;

        public List<(string ParentId, string Name)> CreateCalls { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Func<string, string, GoogleDriveObjectMetadata>? CreateHandler
        {
            get;
            set;
        }

        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Parent preparation must not get by ID.");

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Parent preparation must use complete child sets.");

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls.Add((parentId, name));
            CancellationTokens.Add(cancellationToken);
            int sequence = Interlocked.Increment(ref _createSequence);
            return Task.FromResult(CreateHandler is null
                ? new GoogleDriveObjectMetadata(
                    $"created-{sequence}",
                    name,
                    GoogleDriveApplicationRoot.FolderMimeType,
                    trashed: false,
                    parentIds: [parentId],
                    driveId: null)
                : CreateHandler(parentId, name));
        }
    }

    private sealed class RecordingObjectIdCache : IGoogleDriveObjectIdCache
    {
        public bool StoreResult { get; set; } = true;

        public List<(GoogleDriveObjectCacheScope Scope, string ParentId,
            string Name, GoogleDriveObjectKind Kind,
            GoogleDriveObjectMetadata Metadata)> Stored { get; } = new();

        public bool TryGet(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            out GoogleDriveObjectIdCacheEntry? entry)
        {
            entry = null;
            return false;
        }

        public bool TryStoreUniqueValidated(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            GoogleDriveObjectMetadata metadata)
        {
            Stored.Add((scope, parentId, exactName, expectedKind, metadata));
            return StoreResult;
        }

        public void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind) =>
            throw new InvalidOperationException("Parent preparation must not remove cache state.");

        public void ClearScope(GoogleDriveObjectCacheScope scope) =>
            throw new InvalidOperationException("Parent preparation must not clear cache state.");

        public void InvalidateScope(
            GoogleDriveObjectCacheScope scope,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            throw new InvalidOperationException("Parent preparation must not invalidate cache state.");

        public void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            throw new InvalidOperationException("Parent preparation must not invalidate profiles.");
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Parent preparation must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Parent preparation must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Parent preparation must not use the legacy ensure path.");
    }
}
