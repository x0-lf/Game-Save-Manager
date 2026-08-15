using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadServiceTests
{
    private static readonly Guid ProfileId = Guid.Parse(
        "407f8b6c-1f41-46cc-a65e-fb2ed43b9ee7");

    [Fact]
    public async Task UploadAsync_ComposesOneCompleteFileUpload()
    {
        using var temporary = new TemporaryFile([1, 2, 3]);
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("run-id", "run", "root-id")],
            [Folder("nested-id", "nested", "run-id")],
            [],
            []
        ]);
        var cache = new RecordingObjectIdCache();
        using GoogleDriveRemoteOperationContext context = Context();
        var contextFactory = new RecordingContextFactory(context);
        var mediaFactory = new RecordingMediaClientFactory(
            new GoogleDriveMediaUploadMetadata(
                "uploaded-id",
                "save.bin",
                GoogleDriveMediaUploadClient.OpaqueMediaType,
                trashed: false,
                parentIds: ["nested-id"],
                driveId: null,
                size: 3));
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            contextFactory,
            mediaFactory,
            cache);

        GoogleDriveBinaryUploadResult result = await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "run/nested/save.bin",
                3));

        Assert.Equal(GoogleDriveBinaryUploadStatus.Completed, result.Status);
        Assert.Equal(3, result.CompletedBytes);
        Assert.Equal(ProfileId, contextFactory.ProfileId);
        Assert.Equal(
            ["root-id", "run-id", "nested-id", "nested-id"],
            enumeration.ParentIds);
        Assert.Equal(2, cache.Stored.Count);
        Assert.Equal("nested-id", mediaFactory.Client.ParentId);
        Assert.Equal("save.bin", mediaFactory.Client.ExactName);
        Assert.Equal(3, mediaFactory.Client.ExpectedLength);
        Assert.Equal(
            GoogleDriveMediaUploadClient.OpaqueMediaType,
            mediaFactory.Client.MediaType);
        Assert.Equal(1, mediaFactory.Client.UploadCount);
        Assert.True(mediaFactory.Client.IsDisposed);
        Assert.False(mediaFactory.Client.Source!.CanRead);
        Assert.True(context.IsDisposed);
    }

    [Fact]
    public async Task UploadAsync_ReturnsOpenedLengthInsteadOfPlannedLength()
    {
        using var temporary = new TemporaryFile([1, 2, 3]);
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 3);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        GoogleDriveBinaryUploadResult result = await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "save.bin",
                expectedLength: 99));

        Assert.Equal(3, mediaFactory.Client.ExpectedLength);
        Assert.Equal(3, result.CompletedBytes);
    }

    [Fact]
    public async Task UploadAsync_PassesOpenedStreamDirectlyWithoutMaterializing()
    {
        using var temporary = new TemporaryFile([1, 2, 3]);
        var stream = new FileStream(
            temporary.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var source = new GoogleDriveLocalUploadSource(stream, stream.Length);
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 3);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory,
            openSourceAsync: (path, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(temporary.Path, path);
                return Task.FromResult(source);
            });

        await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "save.bin",
                3));

        Assert.Same(stream, mediaFactory.Client.Source);
        Assert.Equal(0, mediaFactory.Client.SourcePosition);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task UploadAsync_PreservesZeroByteStreamPositionAndLifetime()
    {
        using var temporary = new TemporaryFile([]);
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = new RecordingMediaClientFactory(
            ValidMetadata("root-id", "empty.bin", 0),
            readBufferSize: 16 * 1024);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "empty.bin",
                0));

        Assert.Equal(0, mediaFactory.Client.ExpectedLength);
        Assert.Equal(0, mediaFactory.Client.SourcePosition);
        Assert.Equal(0, mediaFactory.Client.SourcePositionAfterRead);
        Assert.Equal(0, mediaFactory.Client.BytesRead);
        Assert.False(mediaFactory.Client.Source!.CanRead);
    }

    [Fact]
    public async Task UploadAsync_LargeStreamUsesBoundedReadsWithoutEagerCopy()
    {
        const long length = (8 * 1024 * 1024) + 17;
        const int readBufferSize = 16 * 1024;
        using var temporary = new TemporaryFile(length);
        var stream = new BoundedReadFileStream(
            temporary.Path,
            maximumReadSize: 32 * 1024);
        var source = new GoogleDriveLocalUploadSource(stream, stream.Length);
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = new RecordingMediaClientFactory(
            ValidMetadata("root-id", "large.bin", length),
            readBufferSize);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory,
            openSourceAsync: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(source);
            });

        await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "large.bin",
                length));

        Assert.Same(stream, mediaFactory.Client.Source);
        Assert.Equal(0, mediaFactory.Client.SourcePosition);
        Assert.Equal(length, mediaFactory.Client.SourcePositionAfterRead);
        Assert.Equal(length, mediaFactory.Client.BytesRead);
        Assert.True(stream.ReadCount > 1);
        Assert.InRange(stream.MaximumRequestedRead, 1, readBufferSize);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task SourceFailure_PreservesSourceCategory()
    {
        string missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gamesaves-r8-missing-{Guid.NewGuid():N}.tmp");
        using GoogleDriveRemoteOperationContext context = Context();
        var contextFactory = new RecordingContextFactory(context);
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            new RecordingChildEnumerationService([]),
            contextFactory,
            mediaFactory);

        GoogleDriveLocalUploadSourceException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalUploadSourceException>(() =>
                service.UploadAsync(
                    missingPath,
                    GoogleDriveBinaryUploadRequest.Parse(
                        ProfileId,
                        "save.bin",
                        1)));

        Assert.Equal(
            GoogleDriveLocalUploadSourceErrorCodes.NotFound,
            exception.SafeErrorCode);
        Assert.Null(contextFactory.ProfileId);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
        Assert.DoesNotContain(missingPath, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextFailure_PreservesContextCategory()
    {
        using var temporary = new TemporaryFile([1]);
        var expected = new GoogleDriveRemoteOperationContextException(
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.NotConnected));
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            new RecordingChildEnumerationService([]),
            new FailingContextFactory(expected),
            mediaFactory);

        GoogleDriveRemoteOperationContextException actual =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(
                () => service.UploadAsync(
                    temporary.Path,
                    GoogleDriveBinaryUploadRequest.Parse(
                        ProfileId,
                        "save.bin",
                        1)));

        Assert.Same(expected, actual);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task ParentFailure_PreservesParentCategory()
    {
        using var temporary = new TemporaryFile([1]);
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("parent-id", "RUN", "root-id")]
        ]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("parent-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.UploadAsync(
                    temporary.Path,
                    GoogleDriveBinaryUploadRequest.Parse(
                        ProfileId,
                        "run/save.bin",
                        1)));

        Assert.Equal(
            GoogleDriveUploadParentPreparationErrorCodes.CaseCollision,
            exception.Result.ErrorCode);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task TargetFailure_PreservesTargetCategory()
    {
        using var temporary = new TemporaryFile([1]);
        var enumeration = new RecordingChildEnumerationService(
        [
            [Blob("existing-id", "save.bin", "root-id")]
        ]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                service.UploadAsync(
                    temporary.Path,
                    GoogleDriveBinaryUploadRequest.Parse(
                        ProfileId,
                        "save.bin",
                        1)));

        Assert.Equal(
            GoogleDriveCreateOnlyUploadTargetErrorCodes.AlreadyExists,
            exception.Result.ErrorCode);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task MediaFailure_PropagatesUnchanged()
    {
        using var temporary = new TemporaryFile([1]);
        var expected = new IOException(
            "The synthetic media stage did not complete.");
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = new RecordingMediaClientFactory(expected);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "save.bin",
                    1)));

        Assert.Same(expected, actual);
        Assert.True(mediaFactory.Client.IsDisposed);
    }

    [Fact]
    public async Task ResponseFailure_PreservesResponseCategory()
    {
        using var temporary = new TemporaryFile([1]);
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = new RecordingMediaClientFactory(
            new GoogleDriveMediaUploadMetadata(
                "uploaded-id",
                "different.bin",
                GoogleDriveMediaUploadClient.OpaqueMediaType,
                trashed: false,
                parentIds: ["root-id"],
                driveId: null,
                size: 1));
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        GoogleDriveUploadResponseException exception =
            await Assert.ThrowsAsync<GoogleDriveUploadResponseException>(() =>
                service.UploadAsync(
                    temporary.Path,
                    GoogleDriveBinaryUploadRequest.Parse(
                        ProfileId,
                        "save.bin",
                        1)));

        Assert.Equal(
            GoogleDriveUploadResponseErrorCodes.NameMismatch,
            exception.SafeErrorCode);
        Assert.True(mediaFactory.Client.IsDisposed);
    }

    [Fact]
    public async Task UploadAsync_ForwardsCallerTokenToEveryAsyncBoundary()
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        CancellationToken sourceToken = default;
        var sourceOpener = new GoogleDriveLocalUploadSourceOpener();
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("run-id", "run", "root-id")],
            [],
            []
        ]);
        using GoogleDriveRemoteOperationContext context = Context();
        var contextFactory = new RecordingContextFactory(context);
        var mediaFactory = ValidMediaFactory("run-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            contextFactory,
            mediaFactory,
            openSourceAsync: async (path, cancellationToken) =>
            {
                sourceToken = cancellationToken;
                return await sourceOpener.OpenAsync(path, cancellationToken);
            });

        await service.UploadAsync(
            temporary.Path,
            GoogleDriveBinaryUploadRequest.Parse(
                ProfileId,
                "run/save.bin",
                1),
            cancellation.Token);

        Assert.Equal(cancellation.Token, sourceToken);
        Assert.Equal(cancellation.Token, contextFactory.CancellationToken);
        Assert.All(enumeration.CancellationTokens,
            token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(cancellation.Token, mediaFactory.Client.CancellationToken);
    }

    [Fact]
    public async Task CancellationAfterSourceOpen_StopsBeforeContext()
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        GoogleDriveLocalUploadSource? openedSource = null;
        var sourceOpener = new GoogleDriveLocalUploadSourceOpener();
        var enumeration = new RecordingChildEnumerationService([]);
        using GoogleDriveRemoteOperationContext context = Context();
        var contextFactory = new RecordingContextFactory(context);
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            contextFactory,
            mediaFactory,
            openSourceAsync: async (path, cancellationToken) =>
            {
                openedSource = await sourceOpener.OpenAsync(
                    path,
                    cancellationToken);
                cancellation.Cancel();
                return openedSource;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "save.bin",
                    1),
                cancellation.Token));

        Assert.NotNull(openedSource);
        Assert.False(openedSource.Stream.CanRead);
        Assert.Null(contextFactory.ProfileId);
        Assert.Empty(enumeration.ParentIds);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task CancellationAfterContextCreation_StopsBeforeParent()
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService([]);
        using GoogleDriveRemoteOperationContext context = Context();
        var contextFactory = new RecordingContextFactory(
            context,
            cancellation.Cancel);
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            contextFactory,
            mediaFactory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "save.bin",
                    1),
                cancellation.Token));

        Assert.True(context.IsDisposed);
        Assert.Empty(enumeration.ParentIds);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task CancellationAfterParentEnumeration_StopsBeforeCache()
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService(
        [
            [Folder("run-id", "run", "root-id")]
        ], _ => cancellation.Cancel());
        var cache = new RecordingObjectIdCache();
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("run-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory,
            cache);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "run/save.bin",
                    1),
                cancellation.Token));

        Assert.Single(enumeration.ParentIds);
        Assert.Empty(cache.Stored);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAtEitherTargetGuard_StopsBeforeUpload(
        int cancellingCall)
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService(
        [
            [],
            []
        ], call =>
        {
            if (call == cancellingCall)
                cancellation.Cancel();
        });
        var cache = new RecordingObjectIdCache();
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory,
            cache);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "save.bin",
                    1),
                cancellation.Token));

        Assert.Equal(cancellingCall, enumeration.ParentIds.Count);
        Assert.Empty(cache.Stored);
        Assert.Equal(0, mediaFactory.Client.UploadCount);
    }

    [Fact]
    public async Task CancellationAfterSdkCompletion_ProducesNoResult()
    {
        using var temporary = new TemporaryFile([1]);
        using var cancellation = new CancellationTokenSource();
        var enumeration = new RecordingChildEnumerationService([[], []]);
        using GoogleDriveRemoteOperationContext context = Context();
        var mediaFactory = ValidMediaFactory("root-id", "save.bin", 1);
        mediaFactory.Client.BeforeReturn = cancellation.Cancel;
        GoogleDriveBinaryUploadService service = Service(
            enumeration,
            new RecordingContextFactory(context),
            mediaFactory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                temporary.Path,
                GoogleDriveBinaryUploadRequest.Parse(
                    ProfileId,
                    "save.bin",
                    1),
                cancellation.Token));

        Assert.Equal(1, mediaFactory.Client.UploadCount);
        Assert.True(mediaFactory.Client.IsDisposed);
    }

    [Fact]
    public void ServiceContract_IsInternalOneFileOnlyAndSdkFree()
    {
        Type contract = typeof(IGoogleDriveBinaryUploadService);
        Type implementation = typeof(GoogleDriveBinaryUploadService);

        Assert.False(contract.IsPublic || contract.IsNestedPublic);
        Assert.False(implementation.IsPublic || implementation.IsNestedPublic);
        Assert.True(implementation.IsSealed);
        Assert.True(contract.IsAssignableFrom(implementation));

        MethodInfo method = Assert.Single(contract.GetMethods());
        Assert.Equal("UploadAsync", method.Name);
        Assert.Equal(
            typeof(Task<GoogleDriveBinaryUploadResult>),
            method.ReturnType);
        Assert.DoesNotContain(
            method.GetParameters().Select(parameter => parameter.ParameterType),
            type => type.Namespace?.StartsWith(
                "Google.",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ServiceSource_HasNoRunProviderWiringOrCompletedFileCache()
    {
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveBinaryUploadService.cs"));
        string[] forbidden =
        [
            "IRemoteFileSystem",
            "GoogleDriveRemoteFileSystem",
            "SyncProviderFactory",
            "ListRunFolderNamesAsync",
            "DownloadFileAsync",
            "IServiceCollection",
            "IGoogleDriveObjectIdCache",
            "GameSaves.App",
            "Google.Apis",
            "ReadAllBytes",
            "ReadAllText",
            "ReadToEnd",
            "CopyTo",
            "MemoryStream",
            "StreamReader",
            "BinaryReader",
            "ToArray(",
            "foreach (",
            "for ("
        ];

        Assert.All(forbidden, value =>
            Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceSource_ChecksCancellationAroundValidationAndResult()
    {
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveBinaryUploadService.cs"));
        int validation = source.IndexOf(
            "GoogleDriveUploadResponseValidator.Validate",
            StringComparison.Ordinal);
        int result = source.IndexOf(
            "GoogleDriveBinaryUploadResult result = new",
            StringComparison.Ordinal);
        int returned = source.IndexOf("return result", StringComparison.Ordinal);

        Assert.True(validation >= 0 && result > validation && returned > result);
        Assert.Contains(
            "cancellationToken.ThrowIfCancellationRequested()",
            source[validation..result],
            StringComparison.Ordinal);
        Assert.Contains(
            "cancellationToken.ThrowIfCancellationRequested()",
            source[result..returned],
            StringComparison.Ordinal);
    }

    private static GoogleDriveBinaryUploadService Service(
        RecordingChildEnumerationService enumeration,
        IGoogleDriveRemoteOperationContextFactory contextFactory,
        IGoogleDriveMediaUploadClientFactory mediaFactory,
        RecordingObjectIdCache? cache = null,
        Func<string, CancellationToken,
            Task<GoogleDriveLocalUploadSource>>? openSourceAsync = null)
    {
        var guard = new GoogleDriveCreateOnlyUploadTargetGuard(
            enumeration,
            new GoogleDriveObjectCreationCoordinator());
        var parentPreparation = new GoogleDriveUploadParentPreparationService(
            enumeration,
            guard,
            new UnexpectedObjectApi(),
            cache ?? new RecordingObjectIdCache());
        var sourceOpener = new GoogleDriveLocalUploadSourceOpener();
        return new GoogleDriveBinaryUploadService(
            openSourceAsync ?? sourceOpener.OpenAsync,
            contextFactory,
            parentPreparation,
            guard,
            mediaFactory);
    }

    private static string FindManagerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(
                    directory.FullName,
                    "Manager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Manager.sln from the test output directory.");
    }

    private static RecordingMediaClientFactory ValidMediaFactory(
        string parentId,
        string exactName,
        long length) =>
        new(ValidMetadata(parentId, exactName, length));

    private static GoogleDriveMediaUploadMetadata ValidMetadata(
        string parentId,
        string exactName,
        long length) =>
        new(
            "uploaded-id",
            exactName,
            GoogleDriveMediaUploadClient.OpaqueMediaType,
            trashed: false,
            parentIds: [parentId],
            driveId: null,
            size: length);

    private static GoogleDriveFolderChildEntry Folder(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            GoogleDriveApplicationRoot.FolderMimeType,
            GoogleDriveRecursiveObjectKind.Folder,
            [parentId],
            trashed: false,
            driveId: null);

    private static GoogleDriveFolderChildEntry Blob(
        string id,
        string name,
        string parentId) =>
        new(
            id,
            name,
            GoogleDriveMediaUploadClient.OpaqueMediaType,
            GoogleDriveRecursiveObjectKind.BlobFile,
            [parentId],
            trashed: false,
            driveId: null);

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

    private sealed class RecordingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        private readonly GoogleDriveRemoteOperationContext _context;

        private readonly Action? _created;

        public RecordingContextFactory(
            GoogleDriveRemoteOperationContext context,
            Action? created = null)
        {
            _context = context;
            _created = created;
        }

        public Guid? ProfileId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileId = remoteProfileId;
            CancellationToken = cancellationToken;
            _created?.Invoke();
            return Task.FromResult(_context);
        }
    }

    private sealed class FailingContextFactory
        : IGoogleDriveRemoteOperationContextFactory
    {
        private readonly Exception _exception;

        public FailingContextFactory(Exception exception) =>
            _exception = exception;

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<GoogleDriveRemoteOperationContext>(_exception);
    }

    private sealed class RecordingMediaClientFactory
        : IGoogleDriveMediaUploadClientFactory
    {
        public RecordingMediaClientFactory(
            GoogleDriveMediaUploadMetadata response) =>
            Client = new RecordingMediaClient(response);

        public RecordingMediaClientFactory(
            GoogleDriveMediaUploadMetadata response,
            int readBufferSize) =>
            Client = new RecordingMediaClient(response, readBufferSize);

        public RecordingMediaClientFactory(Exception failure) =>
            Client = new RecordingMediaClient(failure);

        public RecordingMediaClient Client { get; }

        public IGoogleDriveMediaUploadClient Create(
            GoogleAuthorizedCredential credential) => Client;
    }

    private sealed class RecordingMediaClient : IGoogleDriveMediaUploadClient
    {
        private readonly GoogleDriveMediaUploadMetadata? _response;
        private readonly Exception? _failure;
        private readonly int? _readBufferSize;

        public RecordingMediaClient(GoogleDriveMediaUploadMetadata response) =>
            _response = response;

        public RecordingMediaClient(
            GoogleDriveMediaUploadMetadata response,
            int readBufferSize)
        {
            _response = response;
            _readBufferSize = readBufferSize;
        }

        public RecordingMediaClient(Exception failure) =>
            _failure = failure;

        public string? ParentId { get; private set; }

        public string? ExactName { get; private set; }

        public Stream? Source { get; private set; }

        public long SourcePosition { get; private set; }

        public long SourcePositionAfterRead { get; private set; }

        public long BytesRead { get; private set; }

        public long ExpectedLength { get; private set; }

        public string? MediaType { get; private set; }

        public int UploadCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Action? BeforeReturn { get; set; }

        public async Task<GoogleDriveMediaUploadMetadata> UploadAsync(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType,
            IProgress<GoogleDriveMediaUploadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken = cancellationToken;
            ParentId = parentFolderId;
            ExactName = exactFileName;
            Source = source;
            SourcePosition = source.Position;
            ExpectedLength = expectedLength;
            MediaType = mediaType;
            UploadCount++;

            if (_readBufferSize is int readBufferSize)
            {
                byte[] buffer = new byte[readBufferSize];
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(
                    buffer.AsMemory(), cancellationToken)) != 0)
                {
                    BytesRead += bytesRead;
                }
            }

            SourcePositionAfterRead = source.Position;
            if (_failure is not null)
                throw _failure;

            BeforeReturn?.Invoke();
            return _response!;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        private readonly Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>
            _results;

        private readonly Action<int>? _enumerated;

        public RecordingChildEnumerationService(
            IEnumerable<IReadOnlyList<GoogleDriveFolderChildEntry>> results,
            Action<int>? enumerated = null)
        {
            _results = new Queue<IReadOnlyList<GoogleDriveFolderChildEntry>>(results);
            _enumerated = enumerated;
        }

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
            _enumerated?.Invoke(ParentIds.Count);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingObjectIdCache : IGoogleDriveObjectIdCache
    {
        public List<GoogleDriveObjectMetadata> Stored { get; } = new();

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
            Stored.Add(metadata);
            return true;
        }

        public void Remove(
            GoogleDriveObjectCacheScope scope,
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind) =>
            throw new InvalidOperationException("Unexpected cache removal.");

        public void ClearScope(GoogleDriveObjectCacheScope scope) =>
            throw new InvalidOperationException("Unexpected cache clear.");

        public void InvalidateScope(
            GoogleDriveObjectCacheScope scope,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            throw new InvalidOperationException("Unexpected cache invalidation.");

        public void InvalidateProfile(
            Guid remoteProfileId,
            GoogleDriveObjectCacheInvalidationReason reason) =>
            throw new InvalidOperationException("Unexpected profile invalidation.");
    }

    private sealed class UnexpectedObjectApi : IGoogleDriveObjectApi
    {
        public Task<GoogleDriveObjectMetadata> GetByIdAsync(
            GoogleAuthorizedCredential credential,
            string objectId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object lookup.");

        public Task<IReadOnlyList<GoogleDriveObjectMetadata>>
            ListChildrenByExactNameAsync(
                GoogleAuthorizedCredential credential,
                string parentId,
                string name,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected exact-name lookup.");

        public Task<GoogleDriveObjectMetadata> CreateFolderAsync(
            GoogleAuthorizedCredential credential,
            string parentId,
            string name,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected folder creation.");
    }

    private sealed class UnusedResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected resolver call.");

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected resolver call.");

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected resolver call.");
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamesaves-r8-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(Path, content);
        }

        public TemporaryFile(long length)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamesaves-r9-{Guid.NewGuid():N}.tmp");
            using FileStream stream = File.Create(Path);
            stream.SetLength(length);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }

    private sealed class BoundedReadFileStream : FileStream
    {
        private readonly int _maximumReadSize;

        public BoundedReadFileStream(string path, int maximumReadSize)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        {
            _maximumReadSize = maximumReadSize;
        }

        public int ReadCount { get; private set; }

        public int MaximumRequestedRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateReadSize(count);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ValidateReadSize(buffer.Length);
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ValidateReadSize(count);
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ValidateReadSize(buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }

        private void ValidateReadSize(int requestedRead)
        {
            if (requestedRead > _maximumReadSize)
            {
                throw new InvalidOperationException(
                    "The synthetic stream rejects eager reads.");
            }

            ReadCount++;
            MaximumRequestedRead = Math.Max(
                MaximumRequestedRead,
                requestedRead);
        }
    }
}
