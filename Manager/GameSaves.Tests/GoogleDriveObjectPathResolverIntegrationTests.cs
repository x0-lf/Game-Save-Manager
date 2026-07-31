using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveObjectPathResolverIntegrationTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("77777777-8888-9999-aaaa-bbbbbbbbbbbb");

    [Fact]
    public async Task RealObjectApiAndResolver_ResolveEscapedPaginatedPathThenValidateCache()
    {
        const string firstName = @"O'Brien\保存";
        var state = new DriveScenarioState();
        state.SetChildren(
            "root-id",
            firstName,
            leadingEmptyPage: true,
            Folder("parent-id", firstName, "root-id"));
        state.SetChildren(
            "parent-id",
            "Run",
            leadingEmptyPage: false,
            Folder("run-id", "Run", "parent-id"));
        var cache = new GoogleDriveObjectIdCache();
        using GoogleAuthorizedCredential credential = Credential();
        IGoogleDriveObjectPathResolver resolver = CreateResolver(state, cache, credential);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse($"{firstName}/Run");

        GoogleDriveObjectResolutionResult first = await resolver.ResolveAsync(
            "root-id",
            path,
            GoogleDriveObjectKind.Folder);
        int listCountAfterFirstResolution = state.ListRequests.Count;
        GoogleDriveObjectResolutionResult second = await resolver.ResolveAsync(
            "root-id",
            path,
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, first.Status);
        Assert.Equal("run-id", first.ObjectId);
        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, second.Status);
        Assert.Equal(listCountAfterFirstResolution, state.ListRequests.Count);
        Assert.Equal(new[] { "parent-id", "run-id" }, state.GetRequests.Select(r => r.ObjectId));
        Assert.Equal(3, listCountAfterFirstResolution);

        string expectedQuery = new GoogleDriveQueryBuilder()
            .BuildExactNameChildQuery("root-id", firstName);
        Assert.Equal(expectedQuery, state.ListRequests[0].Query);
        Assert.Contains("O\\'Brien\\\\保存", expectedQuery, StringComparison.Ordinal);
        Assert.All(state.ListRequests, request =>
        {
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
        });
        Assert.DoesNotContain("run-id", second.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealObjectApiAndResolver_EnsureOnlyMissingParentsAndReuseCreatedIds()
    {
        var state = new DriveScenarioState();
        state.SetChildren("root-id", "Games", leadingEmptyPage: false);
        var cache = new GoogleDriveObjectIdCache();
        using GoogleAuthorizedCredential credential = Credential();
        IGoogleDriveObjectPathResolver resolver = CreateResolver(state, cache, credential);
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Games/2026");

        GoogleDriveObjectResolutionResult first = await resolver.EnsureFolderPathAsync(
            "root-id",
            path);
        int listsAfterCreation = state.ListRequests.Count;
        GoogleDriveObjectResolutionResult second = await resolver.EnsureFolderPathAsync(
            "root-id",
            path);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Created, first.Status);
        Assert.Equal(GoogleDriveObjectResolutionStatus.Found, second.Status);
        Assert.Equal(2, state.CreateRequests.Count);
        Assert.Equal(listsAfterCreation, state.ListRequests.Count);
        Assert.Equal(new[] { "Games", "2026" }, state.CreateRequests.Select(r => r.Name));
        Assert.All(state.CreateRequests, request =>
        {
            Assert.Equal(GoogleDriveApplicationRoot.FolderMimeType, request.MimeType);
            Assert.Single(request.ParentIds);
            Assert.False(request.SupportsAllDrives);
            Assert.Equal(GoogleDriveRequestContract.MetadataFields, request.Fields);
        });
        Assert.Equal(2, state.GetRequests.Count);
    }

    [Fact]
    public async Task ResolverRejectsPaginatedDuplicatesAndTrashedObjectsWithoutCreating()
    {
        var state = new DriveScenarioState();
        state.SetChildren(
            "root-id",
            "Duplicate",
            leadingEmptyPage: false,
            Folder("first-id", "Duplicate", "root-id"),
            Folder("second-id", "Duplicate", "root-id"));
        state.SetChildren(
            "root-id",
            "Trashed",
            leadingEmptyPage: false,
            Folder("trashed-id", "Trashed", "root-id", trashed: true));
        using GoogleAuthorizedCredential credential = Credential();
        IGoogleDriveObjectPathResolver resolver = CreateResolver(
            state,
            new GoogleDriveObjectIdCache(),
            credential);

        GoogleDriveObjectResolutionResult duplicate = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Duplicate"),
            GoogleDriveObjectKind.Folder);
        GoogleDriveObjectResolutionResult trashed = await resolver.ResolveAsync(
            "root-id",
            GoogleDriveRelativePath.Parse("Trashed"),
            GoogleDriveObjectKind.Folder);

        Assert.Equal(GoogleDriveObjectResolutionStatus.Ambiguous, duplicate.Status);
        Assert.Null(duplicate.ObjectId);
        Assert.Equal(GoogleDriveObjectResolutionStatus.Trashed, trashed.Status);
        Assert.Empty(state.CreateRequests);
        Assert.Equal(3, state.ListRequests.Count);
    }

    [Fact]
    public void DependencyInjection_RegistersCredentialScopedResolverWithoutDoingDriveWork()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();
        using GoogleAuthorizedCredential credential = Credential();

        IGoogleDriveObjectPathResolverFactory factory =
            provider.GetRequiredService<IGoogleDriveObjectPathResolverFactory>();
        IGoogleDriveObjectPathResolver resolver = factory.Create(ProfileId, credential);

        Assert.IsType<GoogleDriveObjectPathResolverFactory>(factory);
        Assert.IsType<GoogleDriveObjectPathResolver>(resolver);
        Assert.IsType<GoogleDriveObjectApi>(
            provider.GetRequiredService<IGoogleDriveObjectApi>());
        Assert.Same(
            provider.GetRequiredService<IGoogleDriveObjectIdCache>(),
            provider.GetRequiredService<IGoogleDriveObjectIdCache>());
        Assert.Same(
            provider.GetRequiredService<GoogleDriveObjectCreationCoordinator>(),
            provider.GetRequiredService<GoogleDriveObjectCreationCoordinator>());
    }

    [Fact]
    public void GoogleDriveArchitecture_RemainsInfrastructureOnlyAndDoesNotActivateSync()
    {
        Type[] infrastructureTypes = typeof(GoogleDriveObjectPathResolver).Assembly.GetTypes();
        Type[] googleDriveTypes = infrastructureTypes
            .Where(type => string.Equals(
                type.Namespace,
                "GameSaves.Infrastructure.GoogleDrive",
                StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(googleDriveTypes, type =>
            type.Name == "GoogleDriveSyncProvider");
        Assert.Collection(
            googleDriveTypes.Where(type =>
                !type.IsInterface &&
                typeof(IRemoteFileSystem).IsAssignableFrom(type)),
            type => Assert.Equal("GoogleDriveRemoteFileSystem", type.Name));
        Assert.DoesNotContain(
            typeof(SyncProviderFactory).GetMethods(),
            method => method.Name.Contains("Google", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(GoogleDriveObjectPathResolver).GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(GoogleDriveQueryBuilder));
        Assert.Equal(
            "https://www.googleapis.com/auth/drive.file",
            GoogleDriveAuthorizationScopes.DriveFile);

        string[] coreReferences = typeof(SyncProviderKind).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        string[] appReferences = typeof(GameSaves.App.ViewModels.SyncViewModel).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(
            coreReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            appReferences,
            name => name.StartsWith("Google.", StringComparison.Ordinal));
    }

    private static IGoogleDriveObjectPathResolver CreateResolver(
        DriveScenarioState state,
        IGoogleDriveObjectIdCache cache,
        GoogleAuthorizedCredential credential)
    {
        var api = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new ScenarioObjectClientFactory(state));
        var factory = new GoogleDriveObjectPathResolverFactory(
            api,
            cache,
            new GoogleDriveObjectCreationCoordinator());
        return factory.Create(ProfileId, credential);
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

    private static GoogleDriveObjectMetadata Folder(
        string id,
        string name,
        string parentId,
        bool trashed = false) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            trashed,
            new[] { parentId },
            driveId: null);

    private sealed class ScenarioObjectClientFactory : IGoogleDriveObjectClientFactory
    {
        private readonly DriveScenarioState _state;

        public ScenarioObjectClientFactory(DriveScenarioState state) => _state = state;

        public IGoogleDriveObjectClient Create(GoogleAuthorizedCredential credential) =>
            new ScenarioObjectClient(_state);
    }

    private sealed class ScenarioObjectClient : IGoogleDriveObjectClient
    {
        private readonly DriveScenarioState _state;

        public ScenarioObjectClient(DriveScenarioState state) => _state = state;

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken) =>
            _state.GetAsync(request, cancellationToken);

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken) =>
            _state.ListAsync(request, cancellationToken);

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken) =>
            _state.CreateFolderAsync(request, cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class DriveScenarioState
    {
        private readonly object _gate = new();
        private readonly GoogleDriveQueryBuilder _queryBuilder = new();
        private readonly Dictionary<string, ListScenario> _lists =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, GoogleDriveObjectMetadata> _objects =
            new(StringComparer.Ordinal);
        private int _createdSequence;

        public List<GoogleDriveObjectGetRequest> GetRequests { get; } = new();
        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();
        public List<GoogleDriveFolderCreateRequest> CreateRequests { get; } = new();

        public void SetChildren(
            string parentId,
            string name,
            bool leadingEmptyPage,
            params GoogleDriveObjectMetadata[] objects)
        {
            lock (_gate)
            {
                string query = _queryBuilder.BuildExactNameChildQuery(parentId, name);
                _lists[query] = new ListScenario(
                    leadingEmptyPage,
                    objects.ToList());
                foreach (GoogleDriveObjectMetadata metadata in objects)
                    _objects[metadata.Id] = metadata;
            }
        }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                GetRequests.Add(request);
                if (_objects.TryGetValue(request.ObjectId, out var metadata))
                    return Task.FromResult(metadata);
            }

            throw GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectMetadataGet,
                GoogleDriveApiFailure.NotFound,
                "GoogleDriveObjectNotFound");
        }

        public Task<GoogleDriveObjectListPage> ListAsync(
            GoogleDriveObjectListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ListRequests.Add(request);
                if (!_lists.TryGetValue(request.Query, out ListScenario? scenario))
                {
                    return Task.FromResult(new GoogleDriveObjectListPage(
                        Array.Empty<GoogleDriveObjectMetadata>(),
                        null,
                        IncompleteSearch: false));
                }

                if (scenario.LeadingEmptyPage && request.PageToken is null)
                {
                    return Task.FromResult(new GoogleDriveObjectListPage(
                        Array.Empty<GoogleDriveObjectMetadata>(),
                        "0",
                        IncompleteSearch: false));
                }

                int offset = request.PageToken is null
                    ? 0
                    : int.Parse(request.PageToken, System.Globalization.CultureInfo.InvariantCulture);
                GoogleDriveObjectMetadata[] page = scenario.Objects
                    .Skip(offset)
                    .Take(1)
                    .ToArray();
                string? next = offset + page.Length < scenario.Objects.Count
                    ? (offset + page.Length).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                return Task.FromResult(new GoogleDriveObjectListPage(
                    page,
                    next,
                    IncompleteSearch: false));
            }
        }

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleDriveFolderCreateRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CreateRequests.Add(request);
                var created = Folder(
                    $"created-{++_createdSequence}",
                    request.Name,
                    request.ParentId);
                _objects[created.Id] = created;
                string query = _queryBuilder.BuildExactNameChildQuery(
                    request.ParentId,
                    request.Name);
                if (!_lists.TryGetValue(query, out ListScenario? scenario))
                {
                    scenario = new ListScenario(
                        LeadingEmptyPage: false,
                        new List<GoogleDriveObjectMetadata>());
                    _lists.Add(query, scenario);
                }
                scenario.Objects.Add(created);
                return Task.FromResult(created);
            }
        }

        private sealed record ListScenario(
            bool LeadingEmptyPage,
            List<GoogleDriveObjectMetadata> Objects);
    }
}
