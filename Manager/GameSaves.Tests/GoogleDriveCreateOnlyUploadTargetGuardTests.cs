using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadTargetGuardTests
{
    private static readonly Guid ProfileId = Guid.Parse(
        "7e4d9f25-c8d9-4aa0-9e44-6434f6eab728");

    [Theory]
    [InlineData((int)GoogleDriveObjectKind.File, (int)GoogleDriveRecursiveObjectKind.BlobFile,
        "target.bin", GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists)]
    [InlineData((int)GoogleDriveObjectKind.Folder, (int)GoogleDriveRecursiveObjectKind.Folder,
        "target.bin", GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists)]
    [InlineData((int)GoogleDriveObjectKind.File, (int)GoogleDriveRecursiveObjectKind.BlobFile,
        "TARGET.BIN", GoogleDriveCreateOnlyUploadTargetErrorCodes.CaseCollision)]
    [InlineData((int)GoogleDriveObjectKind.File, (int)GoogleDriveRecursiveObjectKind.Folder,
        "target.bin", GoogleDriveCreateOnlyUploadTargetErrorCodes.TypeCollision)]
    [InlineData((int)GoogleDriveObjectKind.Folder, (int)GoogleDriveRecursiveObjectKind.BlobFile,
        "TARGET.BIN", GoogleDriveCreateOnlyUploadTargetErrorCodes.TypeCollision)]
    public async Task MatchingChild_RefusesCreateOnlyTarget(
        int targetKindValue,
        int childKindValue,
        string childName,
        string expectedCode)
    {
        var targetKind = (GoogleDriveObjectKind)targetKindValue;
        var childKind = (GoogleDriveRecursiveObjectKind)childKindValue;
        var enumeration = new RecordingChildEnumerationService
        {
            Results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(
            [
                [
                    Child("unrelated-id", "unrelated.bin", GoogleDriveRecursiveObjectKind.BlobFile),
                    Child("matching-id", childName, childKind)
                ]
            ])
        };
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            enumeration,
            new GoogleDriveObjectCreationCoordinator());
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                guard.AcquireAsync(
                    context,
                    "authoritative-parent-id",
                    "target.bin",
                    targetKind).AsTask());

        Assert.Equal(expectedCode, exception.Result.ErrorCode);
        Assert.Equal(1, enumeration.CallCount);
        AssertSafe(exception, "target.bin", childName, "matching-id");
    }

    [Fact]
    public async Task UnrelatedChildren_LeaveTargetAvailable()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(
            [
                [
                    Child("first-id", "first.bin", GoogleDriveRecursiveObjectKind.BlobFile),
                    Child("folder-id", "folder", GoogleDriveRecursiveObjectKind.Folder)
                ],
                [
                    Child("first-id", "first.bin", GoogleDriveRecursiveObjectKind.BlobFile),
                    Child("folder-id", "folder", GoogleDriveRecursiveObjectKind.Folder)
                ]
            ])
        };
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            enumeration,
            new GoogleDriveObjectCreationCoordinator());
        using GoogleDriveRemoteOperationContext context = Context();

        using IDisposable lease = await guard.AcquireAsync(
            context,
            "authoritative-parent-id",
            "target.bin",
            GoogleDriveObjectKind.File);

        Assert.Equal(2, enumeration.CallCount);
    }

    [Fact]
    public async Task MatchOnLaterPage_RefusesAfterConsumingEveryPage()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(Page(
            [Object("first-id", "target.bin", "application/octet-stream")],
            "page-2"));
        client.Pages.Enqueue(Page(
            [Object("matching-id", "TARGET.BIN", "application/octet-stream")],
            nextPageToken: null));
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            new GoogleDriveFolderChildEnumerationService(
                new GoogleDriveObjectApi(
                    new GoogleDriveQueryBuilder(),
                    new RecordingObjectClientFactory(client))),
            new GoogleDriveObjectCreationCoordinator());
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                guard.AcquireAsync(
                    context,
                    "authoritative-parent-id",
                    "target.bin",
                    GoogleDriveObjectKind.File).AsTask());

        Assert.Equal(
            GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists,
            exception.Result.ErrorCode);
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Equal(new[] { null, "page-2" },
            client.ListRequests.Select(request => request.PageToken));
        Assert.Equal(1, client.DisposeCalls);
        AssertSafe(exception, "target.bin", "TARGET.BIN", "matching-id", "page-2");
    }

    [Fact]
    public async Task IncompleteLaterPage_FailsWithoutAvailability()
    {
        var client = new RecordingObjectClient();
        client.Pages.Enqueue(Page(
            [Object("first-id", "unrelated.bin", "application/octet-stream")],
            "page-2"));
        client.Pages.Enqueue(new GoogleDriveObjectListPage(
            Array.Empty<GoogleDriveObjectMetadata>(),
            null,
            IncompleteSearch: true));
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            new GoogleDriveFolderChildEnumerationService(
                new GoogleDriveObjectApi(
                    new GoogleDriveQueryBuilder(),
                    new RecordingObjectClientFactory(client))),
            new GoogleDriveObjectCreationCoordinator());
        using GoogleDriveRemoteOperationContext context = Context();

        GoogleDriveRecursiveFileListingException exception =
            await Assert.ThrowsAsync<GoogleDriveRecursiveFileListingException>(() =>
                guard.AcquireAsync(
                    context,
                    "authoritative-parent-id",
                    "target.bin",
                    GoogleDriveObjectKind.File).AsTask());

        Assert.Equal(
            GoogleDriveRecursiveFileListingStatus.Unavailable,
            exception.Result.Status);
        Assert.True(exception.Result.Retryable);
        Assert.Equal(2, client.ListRequests.Count);
        Assert.Equal(1, client.DisposeCalls);
        AssertSafe(exception, "target.bin", "page-2", "first-id");
    }

    [Fact]
    public async Task LocalRace_RechecksAfterEnteringCreationLease()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(
            [
                Array.Empty<GoogleDriveFolderChildEntry>(),
                [Child("racing-id", "target.bin", GoogleDriveRecursiveObjectKind.BlobFile)]
            ])
        };
        var coordinator = new GoogleDriveObjectCreationCoordinator();
        IDisposable blocker = await coordinator.AcquireAsync(
            "authoritative-parent-id",
            "target.bin",
            CancellationToken.None);
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            enumeration,
            coordinator);
        using GoogleDriveRemoteOperationContext context = Context();

        Task<IDisposable> acquisition = guard.AcquireAsync(
            context,
            "authoritative-parent-id",
            "target.bin",
            GoogleDriveObjectKind.File).AsTask();
        await enumeration.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(acquisition.IsCompleted);

        blocker.Dispose();
        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => acquisition);

        Assert.Equal(
            GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists,
            exception.Result.ErrorCode);
        Assert.Equal(2, enumeration.CallCount);
        AssertSafe(exception, "target.bin", "racing-id");

        using IDisposable releasedLease = await coordinator.AcquireAsync(
            "authoritative-parent-id",
            "target.bin",
            CancellationToken.None);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLease_StopsBeforeSecondGuard()
    {
        var enumeration = new RecordingChildEnumerationService
        {
            Results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(
            [
                Array.Empty<GoogleDriveFolderChildEntry>()
            ])
        };
        var coordinator = new GoogleDriveObjectCreationCoordinator();
        IDisposable blocker = await coordinator.AcquireAsync(
            "authoritative-parent-id",
            "target.bin",
            CancellationToken.None);
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            enumeration,
            coordinator);
        using GoogleDriveRemoteOperationContext context = Context();
        using var cancellation = new CancellationTokenSource();

        Task<IDisposable> acquisition = guard.AcquireAsync(
            context,
            "authoritative-parent-id",
            "target.bin",
            GoogleDriveObjectKind.File,
            cancellation.Token).AsTask();
        await enumeration.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(1, enumeration.CallCount);

        blocker.Dispose();
        using IDisposable releasedLease = await coordinator.AcquireAsync(
            "authoritative-parent-id",
            "target.bin",
            CancellationToken.None);
    }

    private static GoogleDriveFolderChildEntry Child(
        string id,
        string name,
        GoogleDriveRecursiveObjectKind kind)
    {
        string mimeType = kind == GoogleDriveRecursiveObjectKind.Folder
            ? GoogleDriveApplicationRoot.FolderMimeType
            : "application/octet-stream";
        return new GoogleDriveFolderChildEntry(
            id,
            name,
            mimeType,
            kind,
            ["authoritative-parent-id"],
            trashed: false,
            driveId: null);
    }

    private static GoogleDriveObjectMetadata Object(
        string id,
        string name,
        string mimeType) =>
        new(
            id,
            name,
            mimeType,
            trashed: false,
            parentIds: ["authoritative-parent-id"],
            driveId: null);

    private static GoogleDriveObjectListPage Page(
        IReadOnlyList<GoogleDriveObjectMetadata> objects,
        string? nextPageToken) =>
        new(objects, nextPageToken, IncompleteSearch: false);

    private static void AssertSafe(object value, params string[] privateValues)
    {
        string text = value.ToString()!;
        foreach (string privateValue in privateValues)
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

    private sealed class RecordingChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        public Queue<IReadOnlyList<GoogleDriveFolderChildEntry>> Results { get; set; } =
            new();

        public int CallCount { get; private set; }

        public TaskCompletionSource<bool> FirstCallObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            FirstCallObserved.TrySetResult(true);
            return Task.FromResult(Results.Dequeue());
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

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveObjectMetadata> GetAsync(
            GoogleDriveObjectGetRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Target guard must not get by ID.");

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
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Target guard must not create folders.");

        public void Dispose() => DisposeCalls++;
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Target guard must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Target guard must not resolve paths.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Target guard must not create paths.");
    }
}
