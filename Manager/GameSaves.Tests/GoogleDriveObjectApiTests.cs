using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace GameSaves.Tests;

public sealed class GoogleDriveObjectApiTests
{
    [Theory]
    [InlineData(
        "parent-id",
        "plain name",
        "'parent-id' in parents and name = 'plain name' and trashed = false")]
    [InlineData(
        "parent'id",
        "O'Brien",
        "'parent\\'id' in parents and name = 'O\\'Brien' and trashed = false")]
    [InlineData(
        @"parent\id",
        @"folder\name",
        "'parent\\\\id' in parents and name = 'folder\\\\name' and trashed = false")]
    [InlineData(
        @"par'ent\id",
        @"both'\name",
        "'par\\'ent\\\\id' in parents and name = 'both\\'\\\\name' and trashed = false")]
    [InlineData(
        "親フォルダー",
        "Pokémon 保存データ",
        "'親フォルダー' in parents and name = 'Pokémon 保存データ' and trashed = false")]
    public void QueryBuilder_EscapesExactParentAndNameLiterals(
        string parentId,
        string name,
        string expected)
    {
        string query = new GoogleDriveQueryBuilder()
            .BuildExactNameChildQuery(parentId, name);

        Assert.Equal(expected, query);
    }

    [Fact]
    public void QueryBuilder_EscapesInjectionTextInsideOneExactLiteral()
    {
        const string injection = "x' or trashed = true or name = 'y";

        string query = new GoogleDriveQueryBuilder()
            .BuildExactNameChildQuery("parent", injection);

        Assert.Equal(
            "'parent' in parents and " +
            "name = 'x\\' or trashed = true or name = \\'y' and trashed = false",
            query);
        Assert.StartsWith("'parent' in parents and name = '", query, StringComparison.Ordinal);
        Assert.EndsWith("' and trashed = false", query, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryBuilder_EscapesBackslashesBeforeApostrophesExactlyOnce()
    {
        Assert.Equal(
            "\\\\\\'",
            GoogleDriveQueryBuilder.EscapeLiteral(@"\'"));
    }

    [Theory]
    [InlineData(
        -1,
        "'parent\\'\\\\id' in parents and trashed = false")]
    [InlineData(
        (int)GoogleDriveObjectKind.Folder,
        "'parent\\'\\\\id' in parents and trashed = false and " +
        "mimeType = 'application/vnd.google-apps.folder'")]
    [InlineData(
        (int)GoogleDriveObjectKind.File,
        "'parent\\'\\\\id' in parents and trashed = false and " +
        "mimeType != 'application/vnd.google-apps.folder'")]
    public void QueryBuilder_BuildsEscapedDirectChildQuery(
        int expectedKindValue,
        string expected)
    {
        GoogleDriveObjectKind? expectedKind = expectedKindValue < 0
            ? null
            : (GoogleDriveObjectKind)expectedKindValue;
        string query = new GoogleDriveQueryBuilder()
            .BuildDirectChildrenQuery(@"parent'\id", expectedKind);

        Assert.Equal(expected, query);
    }

    [Fact]
    public void RequestFormatting_DoesNotExposeQueriesIdsOrPageTokens()
    {
        const string objectId = "private-object-id-marker";
        const string query = "'private-parent-id' in parents and name = 'Private name'";
        const string pageToken = "private-page-token-marker";
        var get = new GoogleDriveObjectGetRequest(objectId);
        var list = new GoogleDriveObjectListRequest(query, pageToken);
        var page = new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            pageToken,
            IncompleteSearch: false);

        Assert.DoesNotContain(objectId, get.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(query, list.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private name", list.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pageToken, list.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pageToken, page.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListChildren_FollowsAllPagesWithoutSelectingOrDeduplicating()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Object("same-id", "duplicate") },
            "page-2",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[]
            {
                Object("same-id", "duplicate"),
                Object("second-id", "duplicate")
            },
            null,
            IncompleteSearch: false));
        GoogleDriveObjectApi api = CreateApi(client);

        IReadOnlyList<GoogleDriveObjectMetadata> results =
            await api.ListChildrenByExactNameAsync(
                null!,
                "parent-id",
                "duplicate",
                CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "same-id", "same-id", "second-id" },
            results.Select(result => result.Id));
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Null(client.ListRequests[0].PageToken);
        Assert.Equal("page-2", client.ListRequests[1].PageToken);
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal(
                "'parent-id' in parents and name = 'duplicate' and trashed = false",
                request.Query);
            Assert.Equal("drive", request.Spaces);
            Assert.Equal("user", request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
        });
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task ListChildren_IncompleteSearchMapsToRetryableUnavailable()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "ignored-page",
            IncompleteSearch: true));
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(() =>
            api.ListChildrenByExactNameAsync(
                null!,
                "private-parent-id",
                "Private folder name",
                CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.Unavailable, exception.Failure);
        Assert.Equal(GoogleDriveApiOperation.ObjectChildList, exception.Details.Operation);
        Assert.True(exception.Details.Retryable);
        Assert.DoesNotContain("private-parent-id", exception.Details.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Private folder name", exception.Details.ToString(),
            StringComparison.Ordinal);
        Assert.Single(client.ListRequests);
    }

    [Fact]
    public async Task ListDirectChildren_FollowsEmptyAndPopulatedPagesWithoutDeduplication()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "page-2-private",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[]
            {
                Object("same-id", "Run-A"),
                Object("same-id", "Run-A"),
                Object("other-id", "run-a")
            },
            null,
            IncompleteSearch: false));
        GoogleDriveObjectApi api = CreateApi(client);

        IReadOnlyList<GoogleDriveObjectMetadata> results =
            await api.ListChildrenAsync(
                null!,
                "parent-id",
                expectedKind: null,
                CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "Run-A", "Run-A", "run-a" },
            results.Select(result => result.Name));
        Assert.Equal(new[] { "same-id", "same-id", "other-id" },
            results.Select(result => result.Id));
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Null(client.ListRequests[0].PageToken);
        Assert.Equal("page-2-private", client.ListRequests[1].PageToken);
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal("'parent-id' in parents and trashed = false", request.Query);
            Assert.Equal("drive", request.Spaces);
            Assert.Equal("user", request.Corpora);
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
    }

    [Fact]
    public async Task ListDirectChildren_FolderFilterUsesFolderMimeTypeAndPreservesEmptyResult()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            null,
            IncompleteSearch: false));
        GoogleDriveObjectApi api = CreateApi(client);

        IReadOnlyList<GoogleDriveObjectMetadata> results =
            await api.ListChildrenAsync(
                null!,
                "parent-id",
                GoogleDriveObjectKind.Folder,
                CancellationToken.None);

        Assert.Empty(results);
        GoogleDriveObjectListRequest request = Assert.Single(client.ListRequests);
        Assert.Equal(
            "'parent-id' in parents and trashed = false and " +
            "mimeType = 'application/vnd.google-apps.folder'",
            request.Query);
    }

    [Fact]
    public async Task ListDirectChildren_IncompleteSearchFailsSafely()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-next-token",
            IncompleteSearch: true));
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(() =>
            api.ListChildrenAsync(
                null!,
                "private-parent-id",
                expectedKind: null,
                CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.Unavailable, exception.Failure);
        Assert.True(exception.Details.Retryable);
        Assert.DoesNotContain("private-parent-id", exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-next-token", exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task ListDirectChildren_RejectsSharedDriveResultWithoutExposingPrivateData()
    {
        const string objectId = "private-object-id";
        const string objectName = "Private child name";
        const string driveId = "private-shared-drive-id";
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[]
            {
                Object(objectId, objectName, driveId: driveId)
            },
            null,
            IncompleteSearch: false));
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(() =>
            api.ListChildrenAsync(
                null!,
                "parent-id",
                GoogleDriveObjectKind.Folder,
                CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.AccessDenied, exception.Failure);
        Assert.Equal("GoogleDriveObjectUnsupportedLocation", exception.Details.SafeErrorCode);
        Assert.DoesNotContain(objectId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(objectName, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(driveId, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Theory]
    [InlineData(0, "GoogleDriveObjectTrashed")]
    [InlineData(1, "GoogleDriveObjectParentMismatch")]
    [InlineData(2, "GoogleDriveObjectTypeMismatch")]
    public async Task ListDirectChildren_RejectsResultsThatViolateTheQueryContract(
        int scenario,
        string expectedErrorCode)
    {
        GoogleDriveObjectMetadata invalidObject = scenario switch
        {
            0 => Object("private-object-id", "Private name", trashed: true),
            1 => Object(
                "private-object-id",
                "Private name",
                parentIds: new[] { "different-parent-id" }),
            2 => Object(
                "private-object-id",
                "Private name",
                mimeType: "application/json"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { invalidObject },
            null,
            IncompleteSearch: false));
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveApiException exception = await Assert.ThrowsAsync<GoogleDriveApiException>(() =>
            api.ListChildrenAsync(
                null!,
                "parent-id",
                GoogleDriveObjectKind.Folder,
                CancellationToken.None));

        Assert.Equal(GoogleDriveApiFailure.Failed, exception.Failure);
        Assert.Equal(expectedErrorCode, exception.Details.SafeErrorCode);
        Assert.DoesNotContain("private-object-id", exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Private name", exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task ListDirectChildren_ForwardsCancellationAndDisposesClient()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingObjectClient
        {
            ListException = new OperationCanceledException(cancellation.Token)
        };
        GoogleDriveObjectApi api = CreateApi(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.ListChildrenAsync(
                null!,
                "parent-id",
                expectedKind: null,
                cancellation.Token));

        Assert.Equal(cancellation.Token, client.LastListCancellationToken);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task GetById_UsesNarrowMetadataAndCanDetectSharedDriveMoves()
    {
        var client = new RecordingObjectClient
        {
            GetResult = Object("object-id", "Object")
        };
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveObjectMetadata result = await api.GetByIdAsync(
            null!,
            "object-id",
            CancellationToken.None);

        Assert.Equal("object-id", result.Id);
        GoogleDriveObjectGetRequest request = Assert.Single(client.GetRequests);
        Assert.Equal("object-id", request.ObjectId);
        Assert.Equal(
            "id,name,mimeType,trashed,parents,driveId",
            request.Fields);
        Assert.True(request.SupportsAllDrives);
    }

    [Fact]
    public async Task LateGetResultAfterCancellation_IsRejectedAndDisposesClient()
    {
        var completion = new TaskCompletionSource<GoogleDriveObjectMetadata>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingObjectClient
        {
            GetHandler = _ => completion.Task
        };
        GoogleDriveObjectApi api = CreateApi(client);
        using var cancellation = new CancellationTokenSource();

        Task<GoogleDriveObjectMetadata> operation = api.GetByIdAsync(
            null!,
            "object-id",
            cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(Object("object-id", "Object"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task LateListResultAfterCancellation_IsNotAccumulatedAndDisposesClient()
    {
        var completion = new TaskCompletionSource<GoogleDriveObjectListPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingObjectClient
        {
            ListHandler = _ => completion.Task
        };
        GoogleDriveObjectApi api = CreateApi(client);
        using var cancellation = new CancellationTokenSource();

        Task<IReadOnlyList<GoogleDriveObjectMetadata>> operation =
            api.ListChildrenByExactNameAsync(
                null!,
                "parent-id",
                "manifest.json",
                cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(new GoogleDriveObjectListPage(
            [Object("late-id", "manifest.json")],
            NextPageToken: null,
            IncompleteSearch: false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task LateFolderCreateResultAfterCancellation_IsRejectedAndDisposesClient()
    {
        var completion = new TaskCompletionSource<GoogleDriveObjectMetadata>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingObjectClient
        {
            CreateHandler = _ => completion.Task
        };
        GoogleDriveObjectApi api = CreateApi(client);
        using var cancellation = new CancellationTokenSource();

        Task<GoogleDriveObjectMetadata> operation = api.CreateFolderAsync(
            null!,
            "parent-id",
            "Folder",
            cancellation.Token);
        cancellation.Cancel();
        completion.SetResult(Object("late-id", "Folder"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CreateFolder_UsesExactNameOneParentAndNoContentUpload()
    {
        var client = new RecordingObjectClient
        {
            CreateResult = Object("created-id", "Exact Folder")
        };
        GoogleDriveObjectApi api = CreateApi(client);

        GoogleDriveObjectMetadata created = await api.CreateFolderAsync(
            null!,
            "parent-id",
            "Exact Folder",
            CancellationToken.None);

        Assert.Equal("created-id", created.Id);
        GoogleDriveFolderCreateRequest request = Assert.Single(client.CreateRequests);
        Assert.Equal("Exact Folder", request.Name);
        Assert.Equal("application/vnd.google-apps.folder", request.MimeType);
        Assert.Equal(new[] { "parent-id" }, request.ParentIds);
        Assert.Equal(GoogleDriveRequestContract.MetadataFields, request.Fields);
        Assert.False(request.SupportsAllDrives);
    }

    [Fact]
    public void SdkRequestConstruction_AppliesOnlyTheNarrowRequestContract()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var listContract = new GoogleDriveObjectListRequest(
            "'parent' in parents and name = 'name' and trashed = false",
            "next-page");
        Google.Apis.Drive.v3.FilesResource.ListRequest list =
            GoogleDriveObjectClient.CreateListRequest(drive, listContract);

        Assert.Equal(listContract.Query, list.Q);
        Assert.Equal("drive", list.Spaces);
        Assert.Equal("user", list.Corpora);
        Assert.False(list.IncludeItemsFromAllDrives);
        Assert.False(list.SupportsAllDrives);
        Assert.Equal("next-page", list.PageToken);
        Assert.Equal(
            "nextPageToken,incompleteSearch," +
            "files(id,name,mimeType,trashed,parents,driveId)",
            list.Fields);

        var getContract = new GoogleDriveObjectGetRequest("object-id");
        Google.Apis.Drive.v3.FilesResource.GetRequest get =
            GoogleDriveObjectClient.CreateGetRequest(drive, getContract);

        Assert.Equal(GoogleDriveRequestContract.MetadataFields, get.Fields);
        Assert.True(get.SupportsAllDrives);

        var createContract = new GoogleDriveFolderCreateRequest(
            "Exact Folder",
            "parent-id");
        Google.Apis.Drive.v3.Data.File metadata =
            GoogleDriveObjectClient.CreateFolderMetadata(createContract);
        Google.Apis.Drive.v3.FilesResource.CreateRequest create =
            GoogleDriveObjectClient.CreateFolderRequest(
                drive,
                createContract,
                metadata);

        Assert.Equal("Exact Folder", metadata.Name);
        Assert.Equal("application/vnd.google-apps.folder", metadata.MimeType);
        Assert.Equal(new[] { "parent-id" }, metadata.Parents);
        Assert.Equal(GoogleDriveRequestContract.MetadataFields, create.Fields);
        Assert.False(create.SupportsAllDrives);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalidQuery", (int)GoogleDriveApiFailure.InvalidQuery)]
    [InlineData(HttpStatusCode.Unauthorized, "authError", (int)GoogleDriveApiFailure.AuthorizationRevoked)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientFilePermissions", (int)GoogleDriveApiFailure.AccessDenied)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", (int)GoogleDriveApiFailure.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError", (int)GoogleDriveApiFailure.Unavailable)]
    public void ObjectAndRootApis_ReuseOneSanitizedErrorClassifier(
        HttpStatusCode status,
        string reason,
        int expected)
    {
        var providerError = new GoogleApiException(
            "Drive",
            "access_token=secret account=user@example.invalid object-id-marker")
        {
            HttpStatusCode = status,
            Error = new RequestError
            {
                Errors = new List<SingleError> { new() { Reason = reason } }
            }
        };

        GoogleDriveApiException objectFailure = GoogleDriveObjectApi.MapException(
            providerError,
            GoogleDriveApiOperation.ObjectChildList);
        GoogleDriveApiException rootFailure = GoogleDriveRootFolderApi.MapException(
            providerError,
            GoogleDriveApiOperation.RootFolderDiscovery);

        Assert.Equal((GoogleDriveApiFailure)expected, objectFailure.Failure);
        Assert.Equal((GoogleDriveApiFailure)expected, rootFailure.Failure);
        Assert.Equal(reason, objectFailure.Details.Reason);
        Assert.DoesNotContain("secret", objectFailure.Details.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", objectFailure.Details.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object-id-marker", objectFailure.Details.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownGoogleReason_IsExcludedFromSanitizedDiagnostics()
    {
        var providerError = new GoogleApiException("Drive", "raw-private-response")
        {
            HttpStatusCode = HttpStatusCode.BadRequest,
            Error = new RequestError
            {
                Errors = new List<SingleError>
                {
                    new() { Reason = "private-object-id-marker" }
                }
            }
        };

        GoogleDriveApiException mapped = GoogleDriveObjectApi.MapException(
            providerError,
            GoogleDriveApiOperation.ObjectMetadataGet);

        Assert.Equal(GoogleDriveApiFailure.InvalidRequest, mapped.Failure);
        Assert.Null(mapped.Details.Reason);
        Assert.DoesNotContain("private", mapped.Details.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfrastructureRegistration_ProvidesObjectApiWithoutCallingGoogle()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GoogleDriveObjectApi>(
            provider.GetRequiredService<IGoogleDriveObjectApi>());
        Assert.Same(
            provider.GetRequiredService<IGoogleDriveObjectApi>(),
            provider.GetRequiredService<IGoogleDriveObjectListingApi>());
        Assert.IsType<GoogleDriveObjectClientFactory>(
            provider.GetRequiredService<IGoogleDriveObjectClientFactory>());
        Assert.IsType<GoogleDriveQueryBuilder>(
            provider.GetRequiredService<GoogleDriveQueryBuilder>());
    }

    private static GoogleDriveObjectApi CreateApi(RecordingObjectClient client) =>
        new(new GoogleDriveQueryBuilder(), new RecordingObjectClientFactory(client));

    private static GoogleDriveObjectMetadata Object(
        string id,
        string name,
        string mimeType = "application/vnd.google-apps.folder",
        bool trashed = false,
        IReadOnlyList<string>? parentIds = null,
        string? driveId = null) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? new[] { "parent-id" },
            driveId);

    private sealed class RecordingObjectClientFactory : IGoogleDriveObjectClientFactory
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

        public GoogleDriveObjectMetadata GetResult { get; set; } =
            Object("default-id", "Default");

        public GoogleDriveObjectMetadata CreateResult { get; set; } =
            Object("created-id", "Created");

        public Exception? ListException { get; set; }

        public Func<CancellationToken, Task<GoogleDriveObjectMetadata>>?
            GetHandler { get; set; }

        public Func<CancellationToken, Task<GoogleDriveObjectListPage>>?
            ListHandler { get; set; }

        public Func<CancellationToken, Task<GoogleDriveObjectMetadata>>?
            CreateHandler { get; set; }

        public CancellationToken LastListCancellationToken { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRequests.Add(request);
            if (GetHandler is not null)
                return GetHandler(cancellationToken);
            return Task.FromResult(GetResult);
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            ListRequests.Add(request);
            LastListCancellationToken = cancellationToken;

            if (ListException is not null)
                return Task.FromException<GoogleDriveObjectListPage>(ListException);

            if (ListHandler is not null)
                return ListHandler(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Pages.Dequeue());
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateRequests.Add(request);
            if (CreateHandler is not null)
                return CreateHandler(cancellationToken);
            return Task.FromResult(CreateResult);
        }

        public void Dispose() => DisposeCalls++;
    }
}
