using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveFolderChildEnumerationServiceTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("ea77d649-7e19-4bcb-aa86-9f704dad9d50");

    private const string ParentId = "authoritative-parent-id";

    public static TheoryData<string> InvalidChildNames => new()
    {
        string.Empty,
        ".",
        "..",
        "nested/name",
        "name/",
        "control\u0001name"
    };

    [Fact]
    public async Task FilesAndFolders_AreReturnedWithExactValidatedMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("folder-id", "Files", GoogleDriveApplicationRoot.FolderMimeType),
                Object("file-id", "Pokémon O'Brien\\save.dat", "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Collection(children,
            folder =>
            {
                Assert.Equal("folder-id", folder.ObjectId);
                Assert.Equal("Files", folder.ExactName);
                Assert.Equal(GoogleDriveRecursiveObjectKind.Folder, folder.Kind);
                Assert.Equal(GoogleDriveApplicationRoot.FolderMimeType, folder.MimeType);
            },
            file =>
            {
                Assert.Equal("file-id", file.ObjectId);
                Assert.Equal("Pokémon O'Brien\\save.dat", file.ExactName);
                Assert.Equal(GoogleDriveRecursiveObjectKind.BlobFile, file.Kind);
                Assert.Equal("application/octet-stream", file.MimeType);
            });
        Assert.All(children, child =>
        {
            Assert.Equal(new[] { ParentId }, child.ParentIds);
            Assert.False(child.Trashed);
            Assert.Null(child.DriveId);
        });
        Assert.Equal(ParentId, listing.ParentFolderId);
        Assert.Null(listing.ExpectedKind);
        Assert.Same(context.Credential, listing.Credential);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public async Task MixedDriveNativeChildren_AreClassifiedWithoutFollowingOrExporting()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("shortcut-id", "shortcut", "application/vnd.google-apps.shortcut"),
                Object("document-id", "document", "application/vnd.google-apps.document")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Equal(
            new[]
            {
                GoogleDriveRecursiveObjectKind.Shortcut,
                GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument
            },
            children.Select(child => child.Kind));
        Assert.Equal(1, listing.CallCount);
    }

    [Fact]
    public async Task EmptyFolder_ReturnsAnImmutableEmptyCollection()
    {
        var service = new GoogleDriveFolderChildEnumerationService(
            new RecordingListingApi());
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Empty(children);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<GoogleDriveFolderChildEntry>)children).Add(
                Entry("late-id", "late.dat", "application/octet-stream")));
    }

    [Theory]
    [MemberData(nameof(InvalidChildNames))]
    public async Task InvalidChildName_FailsWithoutReturningPartialChildren(
        string invalidName)
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("valid-id", "valid.dat", "application/octet-stream"),
                Object("invalid-id", invalidName, "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "valid-id",
            "valid.dat",
            "invalid-id",
            invalidName);
    }

    [Fact]
    public async Task MalformedUnicodeName_FailsWithoutReturningPartialChildren()
    {
        string invalidName = "malformed" + new string('\uD800', 1);
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object("invalid-id", invalidName, "application/octet-stream")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "invalid-id");
    }

    [Fact]
    public async Task InvalidMimeType_FailsAsInvalidMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[] { Object("private-id", "save.dat", "not-a-mime") }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "private-id",
            "save.dat",
            "not-a-mime");
    }

    [Fact]
    public async Task WrongParent_FailsAsInvalidMetadata()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    new[] { "different-private-parent" })
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.InvalidMetadata,
            "private-id",
            "save.dat",
            "different-private-parent");
    }

    [Fact]
    public async Task TrashedChild_FailsClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    trashed: true)
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.TrashedObject,
            "private-id",
            "save.dat");
    }

    [Fact]
    public async Task SharedDriveChild_FailsClosed()
    {
        var listing = new RecordingListingApi
        {
            Result = new[]
            {
                Object(
                    "private-id",
                    "save.dat",
                    "application/octet-stream",
                    driveId: "private-drive-id")
            }
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        AssertFailure(
            exception,
            GoogleDriveRecursiveFileListingStatus.UnsupportedLocation,
            "private-id",
            "save.dat",
            "private-drive-id");
    }

    [Theory]
    [InlineData(
        GoogleDriveApiFailure.AuthorizationRevoked,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)]
    [InlineData(
        GoogleDriveApiFailure.InsufficientScope,
        GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired)]
    [InlineData(
        GoogleDriveApiFailure.AccessDenied,
        GoogleDriveRecursiveFileListingStatus.AccessDenied)]
    [InlineData(
        GoogleDriveApiFailure.NotFound,
        GoogleDriveRecursiveFileListingStatus.FolderNotFound)]
    [InlineData(
        GoogleDriveApiFailure.RateLimited,
        GoogleDriveRecursiveFileListingStatus.RateLimited)]
    [InlineData(
        GoogleDriveApiFailure.QuotaExceeded,
        GoogleDriveRecursiveFileListingStatus.QuotaExceeded)]
    [InlineData(
        GoogleDriveApiFailure.Unavailable,
        GoogleDriveRecursiveFileListingStatus.Unavailable)]
    public async Task ApiFailures_MapToStableRecursiveListingFailures(
        object failureValue,
        object expectedStatusValue)
    {
        var failure = (GoogleDriveApiFailure)failureValue;
        var expectedStatus =
            (GoogleDriveRecursiveFileListingStatus)expectedStatusValue;
        var listing = new RecordingListingApi
        {
            Exception = GoogleDriveApiFailureMapper.Create(
                GoogleDriveApiOperation.ObjectChildList,
                failure,
                $"safe-{failure}")
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                service.EnumerateAsync(context, ParentId));

        Assert.Equal(expectedStatus, exception.Result.Status);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(expectedStatus),
            exception.Result.SafeErrorCode);
        Assert.Equal(
            failure is GoogleDriveApiFailure.RateLimited or
                GoogleDriveApiFailure.Unavailable,
            exception.Result.Retryable);
        AssertFailure(exception, expectedStatus, ParentId);
    }

    [Fact]
    public async Task Cancellation_IsForwardedAndDoesNotDisposeTheCallerOwnedContext()
    {
        using var cancellation = new CancellationTokenSource();
        var listing = new RecordingListingApi
        {
            Exception = new OperationCanceledException(cancellation.Token)
        };
        var service = new GoogleDriveFolderChildEnumerationService(listing);
        using GoogleDriveRemoteOperationContext context = Context();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnumerateAsync(context, ParentId, cancellation.Token));

        Assert.Equal(1, listing.CallCount);
        Assert.Equal(cancellation.Token, listing.CancellationToken);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public async Task ExistingObjectApi_PaginatesWithRequiredFieldsAndDisposesItsClient()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            "private-page-token",
            IncompleteSearch: false));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            new[] { Object("private-file-id", "save.dat", "application/octet-stream") },
            null,
            IncompleteSearch: false));
        var api = new GoogleDriveObjectApi(
            new GoogleDriveQueryBuilder(),
            new RecordingObjectClientFactory(client));
        var service = new GoogleDriveFolderChildEnumerationService(api);
        using GoogleDriveRemoteOperationContext context = Context();

        IReadOnlyList<GoogleDriveFolderChildEntry> children =
            await service.EnumerateAsync(context, ParentId);

        Assert.Single(children);
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Equal(new[] { null, "private-page-token" },
            client.ListRequests.Select(request => request.PageToken));
        Assert.All(client.ListRequests, request =>
        {
            Assert.Equal(
                "'authoritative-parent-id' in parents and trashed = false",
                request.Query);
            Assert.Equal(GoogleDriveRequestContract.ListFields, request.Fields);
            Assert.Equal(GoogleDriveRequestContract.DriveSpace, request.Spaces);
            Assert.Equal(GoogleDriveRequestContract.UserCorpus, request.Corpora);
            Assert.False(request.IncludeItemsFromAllDrives);
            Assert.False(request.SupportsAllDrives);
        });
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.Equal(0, client.CreateCalls);
        Assert.False(context.IsDisposed);
    }

    [Fact]
    public void ChildEntry_IsImmutableAndSafeToFormat()
    {
        var parents = new List<string> { ParentId };
        GoogleDriveFolderChildEntry entry = new(
            "private-file-id",
            "Private Save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            parents,
            trashed: false,
            driveId: null);
        parents.Add("late-private-parent-id");

        Assert.Equal(new[] { ParentId }, entry.ParentIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)entry.ParentIds).Add("mutating-parent-id"));
        AssertSafe(
            entry,
            "private-file-id",
            "Private Save.dat",
            ParentId,
            "late-private-parent-id");
    }

    [Fact]
    public void ChildEntry_RejectsMissingAuthoritativeIdentity()
    {
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            string.Empty,
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            new[] { ParentId },
            trashed: false,
            driveId: null));
        Assert.Throws<ArgumentException>(() => new GoogleDriveFolderChildEntry(
            "file-id",
            "save.dat",
            "application/octet-stream",
            GoogleDriveRecursiveObjectKind.BlobFile,
            new[] { string.Empty },
            trashed: false,
            driveId: null));
    }

    [Fact]
    public void DependencyInjection_RegistersEnumerationWithoutRemoteWork()
    {
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();

        using ServiceProvider provider = services.BuildServiceProvider();
        IGoogleDriveFolderChildEnumerationService service =
            provider.GetRequiredService<IGoogleDriveFolderChildEnumerationService>();

        Assert.IsType<GoogleDriveFolderChildEnumerationService>(service);
    }

    private static GoogleDriveObjectMetadata Object(
        string id,
        string name,
        string mimeType,
        IReadOnlyList<string>? parentIds = null,
        bool trashed = false,
        string? driveId = null) =>
        new(
            id,
            name,
            mimeType,
            trashed,
            parentIds ?? new[] { ParentId },
            driveId);

    private static GoogleDriveFolderChildEntry Entry(
        string id,
        string name,
        string mimeType) =>
        new(
            id,
            name,
            mimeType,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType),
            new[] { ParentId },
            trashed: false,
            driveId: null);

    private static void AssertFailure(
        GoogleDriveRecursiveFileListingException exception,
        GoogleDriveRecursiveFileListingStatus status,
        params string[] privateValues)
    {
        Assert.Equal(status, exception.Result.Status);
        Assert.Empty(exception.Result.Entries);
        Assert.Equal(
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status),
            exception.Result.SafeErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Result.SafeUserMessage));
        AssertSafe(exception, privateValues);
        AssertSafe(exception.Result, privateValues);
    }

    private static void AssertSafe(object value, params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues.Where(value => value.Length > 0))
            Assert.DoesNotContain(privateValue, text, StringComparison.Ordinal);
    }

    private static GoogleDriveRemoteOperationContext Context() =>
        new(ProfileId, "authoritative-root-id", Credential(), new UnusedResolver());

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

    private sealed class RecordingListingApi : IGoogleDriveObjectListingApi
    {
        public IReadOnlyList<GoogleDriveObjectMetadata> Result { get; set; } =
            Array.Empty<GoogleDriveObjectMetadata>();

        public Exception? Exception { get; set; }

        public int CallCount { get; private set; }

        public GoogleAuthorizedCredential? Credential { get; private set; }

        public string? ParentFolderId { get; private set; }

        public GoogleDriveObjectKind? ExpectedKind { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>> ListChildrenAsync(
            GoogleAuthorizedCredential credential,
            string parentFolderId,
            GoogleDriveObjectKind? expectedKind,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Credential = credential;
            ParentFolderId = parentFolderId;
            ExpectedKind = expectedKind;
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<IReadOnlyList<GoogleDriveObjectMetadata>>(Exception);
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

        public List<GoogleDriveObjectListRequest> ListRequests { get; } = new();

        public int GetCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken)
        {
            GetCalls++;
            throw new InvalidOperationException("Enumeration must not get by ID.");
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
            CreateCalls++;
            throw new InvalidOperationException("Enumeration must not create folders.");
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Enumeration must not create paths.");
    }
}
