using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRunFolderDiscoveryServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("d99be4bb-1d6c-48f2-b41a-16e774705a11");

    [Fact]
    public async Task EmptyRoot_ReturnsAnEmptySafeResultWithoutResolverWork()
    {
        var listing = new RecordingListingApi();
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderDiscoveryService(contexts, listing);

        GoogleDriveRunFolderDiscoveryResult result =
            await service.DiscoverAsync(ProfileId);

        Assert.Empty(result.Candidates);
        Assert.False(result.HasExactNameCollisions);
        Assert.False(result.HasCaseInsensitiveNameCollisions);
        Assert.Equal(RecordingContextFactory.RootId, listing.ParentFolderId);
        Assert.Equal(GoogleDriveObjectKind.Folder, listing.ExpectedKind);
        Assert.Equal(0, contexts.Resolver.OperationCalls);
        Assert.True(contexts.LastCredential!.IsDisposed);
    }

    [Fact]
    public async Task PaginatedListing_UsesOnlyTheRequiredMyDriveFolderRequest()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-page-2",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Folder("first-private-id", "Run One") },
            "private-page-3",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Folder("second-private-id", "Run Two") },
            null,
            IncompleteSearch: false));
        var api = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new RecordingObjectClientFactory(client));
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderDiscoveryService(contexts, api);

        GoogleDriveRunFolderDiscoveryResult result =
            await service.DiscoverAsync(ProfileId);

        Assert.Equal(new[] { "Run One", "Run Two" },
            result.Candidates.Select(candidate => candidate.ExactName));
        Assert.Equal(new[] { "first-private-id", "second-private-id" },
            result.Candidates.Select(candidate => candidate.FolderId));
        Assert.Equal(3, client.ListRequests.Count);
        Assert.Equal(
            new[] { null, "private-page-2", "private-page-3" },
            client.ListRequests.Select(request => request.PageToken));
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal(
                "'authoritative-root-id' in parents and trashed = false and " +
                "mimeType = 'application/vnd.google-apps.folder'",
                request.Query);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            Assert.Equal(
                "nextPageToken,incompleteSearch," +
                "files(id,name,mimeType,trashed,parents,driveId)",
                request.Fields);
        });
        Assert.Empty(client.GetRequests);
        Assert.Empty(client.CreateRequests);
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, contexts.Resolver.OperationCalls);
    }

    [Fact]
    public async Task UnicodeNamesAndParentRelationships_ArePreservedExactly()
    {
        const string exactName = "保存/Pokémon O'Brien";
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Folder(
                    "private-folder-id",
                    exactName,
                    new[] { RecordingContextFactory.RootId, "second-parent-id" })
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRunFolderCandidate candidate = Assert.Single(
            (await service.DiscoverAsync(ProfileId)).Candidates);

        Assert.Equal(exactName, candidate.ExactName);
        Assert.Equal("private-folder-id", candidate.FolderId);
        Assert.Equal(GoogleDriveApplicationRoot.FolderMimeType, candidate.MimeType);
        Assert.Equal(
            new[] { RecordingContextFactory.RootId, "second-parent-id" },
            candidate.ParentIds);
    }

    [Fact]
    public async Task ExactDuplicates_ArePreservedAndMarkedWithoutSelection()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Folder("first-private-id", "Same Run"),
                Folder("second-private-id", "Same Run")
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRunFolderDiscoveryResult result =
            await service.DiscoverAsync(ProfileId);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(new[] { "first-private-id", "second-private-id" },
            result.Candidates.Select(candidate => candidate.FolderId));
        Assert.True(result.HasExactNameCollisions);
        Assert.False(result.HasCaseInsensitiveNameCollisions);
        Assert.All(result.Candidates,
            candidate => Assert.True(candidate.HasExactNameCollision));
        Assert.All(result.Candidates,
            candidate => Assert.False(candidate.HasCaseInsensitiveNameCollision));
    }

    [Fact]
    public async Task CaseOnlyCollisions_AreMarkedSeparatelyFromExactDuplicates()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Folder("upper-private-id", "Run"),
                Folder("lower-private-id", "run"),
                Folder("other-private-id", "Other")
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRunFolderDiscoveryResult result =
            await service.DiscoverAsync(ProfileId);

        Assert.False(result.HasExactNameCollisions);
        Assert.True(result.HasCaseInsensitiveNameCollisions);
        Assert.All(
            result.Candidates.Where(candidate =>
                candidate.ExactName.Equals("run", StringComparison.OrdinalIgnoreCase)),
            candidate => Assert.True(candidate.HasCaseInsensitiveNameCollision));
        Assert.False(result.Candidates.Single(candidate =>
            candidate.ExactName == "Other").HasCaseInsensitiveNameCollision);
    }

    [Fact]
    public async Task WrongTypeResult_IsRejectedWithoutReturningACandidate()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                new GoogleDriveObjectMetadata(
                    "private-file-id",
                    "Private file name",
                    "application/json",
                    trashed: false,
                    new[] { RecordingContextFactory.RootId },
                    driveId: null)
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.DiscoverAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootWrongType,
            exception.Result.Status);
        AssertSafe(exception, "private-file-id", "Private file name");
    }

    [Fact]
    public async Task SharedDriveResult_IsRejectedWithoutExposingItsIdentity()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Folder(
                    "private-folder-id",
                    "Private folder name",
                    driveId: "private-shared-drive-id")
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.DiscoverAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootUnsupportedLocation,
            exception.Result.Status);
        AssertSafe(
            exception,
            "private-folder-id",
            "Private folder name",
            "private-shared-drive-id");
    }

    [Fact]
    public async Task TrashedResult_IsRejectedDefensively()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Folder("private-trashed-id", "Trashed folder", trashed: true)
            }
        };
        var service = new GoogleDriveRunFolderDiscoveryService(
            new RecordingContextFactory(),
            listing);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => service.DiscoverAsync(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.RootTrashed,
            exception.Result.Status);
        AssertSafe(exception, "private-trashed-id", "Trashed folder");
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDisposesTheOperationContext()
    {
        using var cancellation = new CancellationTokenSource();
        var listing = new RecordingListingApi
        {
            Handler = token => throw new OperationCanceledException(token)
        };
        var contexts = new RecordingContextFactory();
        var service = new GoogleDriveRunFolderDiscoveryService(contexts, listing);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiscoverAsync(ProfileId, cancellation.Token));

        Assert.Equal(cancellation.Token, listing.CancellationToken);
        Assert.True(contexts.LastCredential!.IsDisposed);
        Assert.Equal(0, contexts.Resolver.OperationCalls);
    }

    [Fact]
    public void CandidateAndResultDiagnostics_OmitNamesAndObjectIds()
    {
        var parents = new List<string> { "private-parent-id" };
        var candidate = new GoogleDriveRunFolderCandidate(
            "private-folder-id",
            "Private folder name",
            GoogleDriveApplicationRoot.FolderMimeType,
            parents,
            hasExactNameCollision: true,
            hasCaseInsensitiveNameCollision: true);
        parents.Add("late-private-parent-id");
        var result = new GoogleDriveRunFolderDiscoveryResult(new[] { candidate });

        Assert.Single(candidate.ParentIds);
        AssertSafe(
            candidate,
            "private-parent-id",
            "late-private-parent-id",
            "private-folder-id",
            "Private folder name");
        AssertSafe(
            result,
            "private-parent-id",
            "private-folder-id",
            "Private folder name");
        Assert.True(result.HasExactNameCollisions);
        Assert.True(result.HasCaseInsensitiveNameCollisions);
    }

    [Fact]
    public void DependencyInjection_RegistersDiscoveryWithoutRemoteWork()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        IGoogleDriveRunFolderDiscoveryService service =
            provider.GetRequiredService<IGoogleDriveRunFolderDiscoveryService>();

        Assert.IsType<GoogleDriveRunFolderDiscoveryService>(service);
    }

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        IReadOnlyList<string>? parentIds = null,
        bool trashed = false,
        string? driveId = null) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed,
            parentIds ?? new[] { RecordingContextFactory.RootId },
            driveId);

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

        public RecordingResolver Resolver { get; } = new();

        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCredential = Credential();
            return Task.FromResult(new GoogleDriveRemoteOperationContext(
                remoteProfileId,
                RootId,
                LastCredential,
                Resolver));
        }
    }

    private sealed class RecordingListingApi : IGoogleDriveObjectListingApi
    {
        public IReadOnlyList<GoogleDriveObjectMetadata> Result { get; set; } =
            Array.Empty<GoogleDriveObjectMetadata>();

        public Func<CancellationToken,
            IReadOnlyList<GoogleDriveObjectMetadata>>? Handler { get; set; }

        public string? ParentFolderId { get; private set; }

        public GoogleDriveObjectKind? ExpectedKind { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken)
        {
            ParentFolderId = parentFolderId;
            ExpectedKind = expectedKind;
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Handler?.Invoke(cancellationToken) ?? Result);
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public int OperationCalls { get; private set; }

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

        private Task<GoogleDriveObjectResolutionResult> Unexpected()
        {
            OperationCalls++;
            throw new InvalidOperationException(
                "Run-folder discovery must not invoke path resolution or creation.");
        }
    }

    private sealed class RecordingObjectClientFactory
        : IGoogleDriveObjectClientFactory
    {
        private readonly RecordingObjectClient _client;

        public RecordingObjectClientFactory(RecordingObjectClient client) =>
            _client = client;

        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential) =>
            _client;
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
                "Run-folder discovery must not request individual metadata.");
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
                "Run-folder discovery must never create a folder.");
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
