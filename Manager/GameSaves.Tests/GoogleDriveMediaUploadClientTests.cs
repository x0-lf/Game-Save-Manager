using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveMediaUploadClientTests
{
    [Fact]
    public void Metadata_IsAnImmutableProjectOwnedSnapshot()
    {
        const string id = "private-id-marker";
        const string name = "private-name-marker";
        string[] parents = ["private-parent-marker"];
        var metadata = new GoogleDriveMediaUploadMetadata(
            id,
            name,
            "application/octet-stream",
            trashed: false,
            parents,
            driveId: null,
            size: 42);

        parents[0] = "changed";

        Assert.Equal(id, metadata.Id);
        Assert.Equal(name, metadata.Name);
        Assert.Equal("application/octet-stream", metadata.MimeType);
        Assert.False(metadata.Trashed);
        Assert.Equal(["private-parent-marker"], metadata.ParentIds);
        Assert.Null(metadata.DriveId);
        Assert.Equal(42, metadata.Size);
        Assert.DoesNotContain(id, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(name, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private-parent-marker",
            metadata.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_ValidatesAndFormatsOnlyStateAndCounts()
    {
        var progress = new GoogleDriveMediaUploadProgress(
            GoogleDriveMediaUploadProgressStatus.Uploading,
            42);

        Assert.Equal(GoogleDriveMediaUploadProgressStatus.Uploading, progress.Status);
        Assert.Equal(42, progress.BytesSent);
        Assert.Equal(
            "Google Drive media upload progress: status=Uploading; bytesSent=42",
            progress.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveMediaUploadProgress(
                (GoogleDriveMediaUploadProgressStatus)int.MaxValue,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveMediaUploadProgress(
                GoogleDriveMediaUploadProgressStatus.Uploading,
                -1));
    }

    [Fact]
    public void Boundary_IsInfrastructureInternalFakeableAndSdkFree()
    {
        Type[] boundaryTypes =
        [
            typeof(IGoogleDriveMediaUploadClient),
            typeof(IGoogleDriveMediaUploadClientFactory),
            typeof(GoogleDriveMediaUploadMetadata),
            typeof(GoogleDriveMediaUploadProgress),
            typeof(GoogleDriveMediaUploadProgressStatus)
        ];

        Assert.All(boundaryTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
        });
        Assert.True(typeof(IGoogleDriveMediaUploadClient).IsAssignableFrom(
            typeof(BoundaryFakeClient)));
        Assert.All(boundaryTypes, AssertNoGoogleSdkType);

        Type[] coreTypes = typeof(ISyncProvider).Assembly.GetTypes();
        Type[] appTypes = typeof(SyncViewModel).Assembly.GetTypes();
        Assert.All(boundaryTypes, boundaryType =>
        {
            Assert.DoesNotContain(coreTypes, type => type.Name == boundaryType.Name);
            Assert.DoesNotContain(appTypes, type => type.Name == boundaryType.Name);
        });
    }

    [Fact]
    public void Factory_CreatesDistinctShortLivedSdkClients()
    {
        var factory = new GoogleDriveMediaUploadClientFactory();
        using GoogleAuthorizedCredential credential = Credential();
        IGoogleDriveMediaUploadClient first = factory.Create(credential);
        IGoogleDriveMediaUploadClient second = factory.Create(credential);

        Assert.IsType<GoogleDriveMediaUploadClient>(first);
        Assert.IsType<GoogleDriveMediaUploadClient>(second);
        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();

        Assert.True(((GoogleDriveMediaUploadClient)first).IsDisposed);
        Assert.True(((GoogleDriveMediaUploadClient)second).IsDisposed);
    }

    [Fact]
    public void SdkClient_DisposesOwnedDriveServiceExactlyOnce()
    {
        var drive = new DisposalTrackingDriveService();
        var client = new GoogleDriveMediaUploadClient(drive);

        client.Dispose();
        client.Dispose();

        Assert.True(client.IsDisposed);
        Assert.Equal(1, drive.DisposeCalls);
    }

    [Fact]
    public void SdkAdapter_BuildsRestrictedCreateRequestAndMapsOnlyProjectState()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        using var source = new MemoryStream([1, 2, 3], writable: false);

        FilesResource.CreateMediaUpload upload =
            GoogleDriveMediaUploadClient.CreateSdkUpload(
                drive,
                "private-parent-marker",
                "private-name-marker",
                source,
                "application/octet-stream");
        Google.Apis.Drive.v3.Data.File requestMetadata =
            GoogleDriveMediaUploadClient.CreateMetadata(
                "private-parent-marker",
                "private-name-marker");
        GoogleDriveMediaUploadMetadata metadata =
            GoogleDriveMediaUploadClient.Map(
                new Google.Apis.Drive.v3.Data.File
                {
                    Id = "private-id-marker",
                    Name = "private-name-marker",
                    MimeType = "application/octet-stream",
                    Trashed = false,
                    Parents = ["private-parent-marker"],
                    Size = 3
                });

        Assert.IsAssignableFrom<ResumableUpload>(upload);
        Assert.Equal("private-name-marker", requestMetadata.Name);
        Assert.Equal(
            ["private-parent-marker"],
            requestMetadata.Parents);
        Assert.Equal("application/octet-stream", requestMetadata.MimeType);
        Assert.Equal(
            "id,name,mimeType,trashed,parents,driveId,size",
            upload.Fields);
        Assert.False(upload.SupportsAllDrives);
        Assert.Null(upload.UseContentAsIndexableText);
        Assert.Null(upload.OcrLanguage);
        Assert.Null(upload.IncludePermissionsForView);
        Assert.Null(upload.KeepRevisionForever);
        Assert.IsType<GoogleDriveMediaUploadMetadata>(metadata);
        Assert.Equal(3, metadata.Size);
    }

    [Fact]
    public void SdkAdapter_RejectsAnyNonOpaqueMediaType()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        using var source = new MemoryStream([1], writable: false);

        Assert.Throws<ArgumentException>(() =>
            GoogleDriveMediaUploadClient.CreateSdkUpload(
                drive,
                "private-parent-marker",
                "private-name-marker",
                source,
                "text/plain"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData((5 * 1024 * 1024) + 1)]
    public void SdkAdapter_UsesResumableCreateWithDefaultChunkingForEverySize(
        int size)
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        using var source = new MemoryStream(new byte[size], writable: false);

        FilesResource.CreateMediaUpload upload =
            GoogleDriveMediaUploadClient.CreateSdkUpload(
                drive,
                "private-parent-marker",
                "private-name-marker",
                source,
                "application/octet-stream");

        Assert.IsAssignableFrom<ResumableUpload>(upload);
        Assert.Equal(ResumableUpload.DefaultChunkSize, upload.ChunkSize);
    }

    [Theory]
    [InlineData(UploadStatus.NotStarted,
        (int)GoogleDriveMediaUploadProgressStatus.NotStarted)]
    [InlineData(UploadStatus.Starting,
        (int)GoogleDriveMediaUploadProgressStatus.Starting)]
    [InlineData(UploadStatus.Uploading,
        (int)GoogleDriveMediaUploadProgressStatus.Uploading)]
    [InlineData(UploadStatus.Completed,
        (int)GoogleDriveMediaUploadProgressStatus.Completed)]
    [InlineData(UploadStatus.Failed,
        (int)GoogleDriveMediaUploadProgressStatus.Failed)]
    public void SdkProgress_IsMappedWithoutProviderExceptions(
        UploadStatus sdkStatus,
        int expectedStatus)
    {
        var sdkProgress = new StubUploadProgress(
            sdkStatus,
            42,
            new IOException("private-provider-marker"));

        GoogleDriveMediaUploadProgress progress =
            GoogleDriveMediaUploadClient.MapProgress(sdkProgress);

        Assert.Equal(
            (GoogleDriveMediaUploadProgressStatus)expectedStatus,
            progress.Status);
        Assert.Equal(42, progress.BytesSent);
        Assert.DoesNotContain(
            "private-provider-marker",
            progress.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(8, 8, true)]
    [InlineData(7, 8, false)]
    public void FailedSdkCompletion_UsesAcceptedByteEvidence(
        long bytesSent,
        long expectedLength,
        bool expected)
    {
        var sdkProgress = new StubUploadProgress(
            UploadStatus.Failed,
            bytesSent,
            new IOException("private-provider-marker"));

        Assert.Equal(
            expected,
            GoogleDriveMediaUploadClient.CompletionMayBeIndeterminate(
                sdkProgress,
                expectedLength));
    }

    [Fact]
    public void IndeterminateCompletionFailure_IsFixedAndSafe()
    {
        var exception =
            new GoogleDriveUploadCompletionIndeterminateException();

        Assert.Equal(
            GoogleDriveBinaryUploadErrorCodes.CompletionIndeterminate,
            exception.SafeErrorCode);
        Assert.DoesNotContain(
            "private-provider-marker",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposedSdkClient_RejectsUploadWithoutNetworkWork()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var client = new GoogleDriveMediaUploadClient(drive);
        client.Dispose();
        using var source = new MemoryStream([1], writable: false);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                1,
                "application/octet-stream",
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task SdkClient_PreCanceledTokenStopsBeforeInitiation()
    {
        var handler = new BlockingInitiationHandler();
        using var client = new GoogleDriveMediaUploadClient(
            DriveWithHandler(handler));
        using var source = new MemoryStream([1], writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                1,
                "application/octet-stream",
                progress: null,
                cancellation.Token));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SdkClient_ForwardsCancellationDuringInitiation()
    {
        var handler = new BlockingInitiationHandler();
        using var client = new GoogleDriveMediaUploadClient(
            DriveWithHandler(handler));
        using var source = new MemoryStream([1], writable: false);
        using var cancellation = new CancellationTokenSource();

        Task<GoogleDriveMediaUploadMetadata> upload = client.UploadAsync(
            "private-parent-marker",
            "private-name-marker",
            source,
            1,
            "application/octet-stream",
            progress: null,
            cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.CancellationToken.CanBeCanceled);
        Assert.True(handler.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DeterministicFake_SupportsSuccessAndChunkProgress()
    {
        var response = new GoogleDriveMediaUploadMetadata(
            "private-id-marker",
            "private-name-marker",
            "application/octet-stream",
            trashed: false,
            ["private-parent-marker"],
            driveId: null,
            size: 8);
        var fake = new FakeGoogleDriveMediaUploadClient
        {
            Response = response,
            ChunkBytes = [4, 8]
        };
        var progress = new RecordingProgress<GoogleDriveMediaUploadProgress>();
        using var source = new MemoryStream(new byte[8], writable: false);

        GoogleDriveMediaUploadMetadata result = await fake.UploadAsync(
            "private-parent-marker",
            "private-name-marker",
            source,
            8,
            "application/octet-stream",
            progress,
            CancellationToken.None);

        FakeGoogleDriveMediaUploadCall call = Assert.Single(fake.Calls);
        Assert.Same(response, result);
        Assert.Same(source, call.Source);
        Assert.Equal(0, source.Position);
        Assert.Equal(8, call.ExpectedLength);
        Assert.Equal(
            [
                GoogleDriveMediaUploadProgressStatus.Starting,
                GoogleDriveMediaUploadProgressStatus.Uploading,
                GoogleDriveMediaUploadProgressStatus.Uploading,
                GoogleDriveMediaUploadProgressStatus.Completed
            ],
            progress.Values.Select(value => value.Status));
        Assert.Equal([0, 4, 8, 8],
            progress.Values.Select(value => value.BytesSent));
    }

    [Fact]
    public async Task DeterministicFake_SupportsInjectedFailure()
    {
        var failure = new IOException("private-provider-marker");
        var fake = new FakeGoogleDriveMediaUploadClient
        {
            Failure = failure,
            ChunkBytes = [4]
        };
        var progress = new RecordingProgress<GoogleDriveMediaUploadProgress>();
        using var source = new MemoryStream(new byte[8], writable: false);

        IOException thrown = await Assert.ThrowsAsync<IOException>(() =>
            fake.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                8,
                "application/octet-stream",
                progress,
                CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(
            GoogleDriveMediaUploadProgressStatus.Failed,
            progress.Values[^1].Status);
        Assert.DoesNotContain(
            progress.Values,
            value => value.Status ==
                GoogleDriveMediaUploadProgressStatus.Completed);
    }

    [Fact]
    public async Task DeterministicFake_SupportsPreCanceledOperation()
    {
        var fake = new FakeGoogleDriveMediaUploadClient();
        var progress = new RecordingProgress<GoogleDriveMediaUploadProgress>();
        using var source = new MemoryStream([1], writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                1,
                "application/octet-stream",
                progress,
                cancellation.Token));

        Assert.Empty(fake.Calls);
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task DeterministicFake_SupportsMidChunkCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeGoogleDriveMediaUploadClient
        {
            ChunkBytes = [4, 8],
            ChunkReported = _ => cancellation.Cancel()
        };
        var progress = new RecordingProgress<GoogleDriveMediaUploadProgress>();
        using var source = new MemoryStream(new byte[8], writable: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                8,
                "application/octet-stream",
                progress,
                cancellation.Token));

        Assert.Single(fake.Calls);
        Assert.Equal(cancellation.Token, fake.Calls[0].CancellationToken);
        Assert.DoesNotContain(
            progress.Values,
            value => value.Status ==
                GoogleDriveMediaUploadProgressStatus.Completed);
    }

    [Fact]
    public async Task DeterministicFake_SupportsLateProviderResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var response = new GoogleDriveMediaUploadMetadata(
            "private-id-marker",
            "private-name-marker",
            "application/octet-stream",
            trashed: false,
            ["private-parent-marker"],
            driveId: null,
            size: 1);
        var fake = new FakeGoogleDriveMediaUploadClient
        {
            Response = response,
            ChunkBytes = [1],
            BeforeReturn = cancellation.Cancel
        };
        using var source = new MemoryStream([1], writable: false);

        GoogleDriveMediaUploadMetadata result = await fake.UploadAsync(
            "private-parent-marker",
            "private-name-marker",
            source,
            1,
            "application/octet-stream",
            progress: null,
            cancellation.Token);

        Assert.Same(response, result);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task DeterministicFake_SupportsLostCompletionResponse()
    {
        var fake = new FakeGoogleDriveMediaUploadClient
        {
            ChunkBytes = [1],
            Failure = new GoogleDriveUploadCompletionIndeterminateException()
        };
        using var source = new MemoryStream([1], writable: false);

        await Assert.ThrowsAsync<
            GoogleDriveUploadCompletionIndeterminateException>(() =>
            fake.UploadAsync(
                "private-parent-marker",
                "private-name-marker",
                source,
                1,
                "application/octet-stream",
                progress: null,
                CancellationToken.None));

        Assert.Single(fake.Calls);
    }

    private static void AssertNoGoogleSdkType(Type type)
    {
        const BindingFlags members =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        IEnumerable<Type> exposedTypes = type.GetConstructors(members)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(type.GetProperties(members).Select(property => property.PropertyType))
            .Concat(type.GetMethods(members).SelectMany(method =>
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)));

        Assert.DoesNotContain(exposedTypes, ContainsGoogleSdkType);
    }

    private static bool ContainsGoogleSdkType(Type type)
    {
        if (type.Namespace?.StartsWith("Google.", StringComparison.Ordinal) == true)
            return true;

        return type.IsGenericType &&
            type.GetGenericArguments().Any(ContainsGoogleSdkType);
    }

    private sealed class BoundaryFakeClient : IGoogleDriveMediaUploadClient
    {
        public Task<GoogleDriveMediaUploadMetadata> UploadAsync(
            string parentFolderId,
            string exactFileName,
            Stream source,
            long expectedLength,
            string mediaType,
            IProgress<GoogleDriveMediaUploadProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
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
        return new GoogleAuthorizedCredential(
            new UserCredential(
                flow,
                "test-user",
                new TokenResponse { RefreshToken = "test-refresh-token" }));
    }

    private static DriveService DriveWithHandler(HttpMessageHandler handler)
    {
        var factory = new HttpClientFromMessageHandlerFactory(_ =>
            new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(
                handler,
                false,
                false));
        return new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests",
            HttpClientFactory = factory
        });
    }

    private sealed class DisposalTrackingDriveService : DriveService
    {
        public DisposalTrackingDriveService()
            : base(new BaseClientService.Initializer
            {
                ApplicationName = "Game Save Manager Tests"
            })
        {
        }

        public int DisposeCalls { get; private set; }

        public override void Dispose()
        {
            DisposeCalls++;
            base.Dispose();
        }
    }

    private sealed class BlockingInitiationHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            CancellationToken = cancellationToken;
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class StubUploadProgress(
        UploadStatus status,
        long bytesSent,
        Exception? exception) : IUploadProgress
    {
        public UploadStatus Status { get; } = status;

        public long BytesSent { get; } = bytesSent;

        public Exception? Exception { get; } = exception;
    }
}

internal sealed class FakeGoogleDriveMediaUploadCall
{
    public FakeGoogleDriveMediaUploadCall(
        string parentFolderId,
        string exactFileName,
        Stream source,
        long expectedLength,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ParentFolderId = parentFolderId;
        ExactFileName = exactFileName;
        Source = source;
        ExpectedLength = expectedLength;
        MediaType = mediaType;
        CancellationToken = cancellationToken;
    }

    public string ParentFolderId { get; }

    public string ExactFileName { get; }

    public Stream Source { get; }

    public long ExpectedLength { get; }

    public string MediaType { get; }

    public CancellationToken CancellationToken { get; }
}

internal sealed class FakeGoogleDriveMediaUploadClient
    : IGoogleDriveMediaUploadClient
{
    private bool _disposed;

    public GoogleDriveMediaUploadMetadata Response { get; set; } =
        new(null, null, null, null, null, null, null);

    public Exception? Failure { get; set; }

    public IReadOnlyList<long> ChunkBytes { get; set; } = [];

    public Action<long>? ChunkReported { get; set; }

    public Action? BeforeReturn { get; set; }

    public List<FakeGoogleDriveMediaUploadCall> Calls { get; } = [];

    public int DisposeCalls { get; private set; }

    public async Task<GoogleDriveMediaUploadMetadata> UploadAsync(
        string parentFolderId,
        string exactFileName,
        Stream source,
        long expectedLength,
        string mediaType,
        IProgress<GoogleDriveMediaUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeGoogleDriveMediaUploadCall(
            parentFolderId,
            exactFileName,
            source,
            expectedLength,
            mediaType,
            cancellationToken));

        progress?.Report(new GoogleDriveMediaUploadProgress(
            GoogleDriveMediaUploadProgressStatus.Starting,
            0));
        foreach (long bytesSent in ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new GoogleDriveMediaUploadProgress(
                GoogleDriveMediaUploadProgressStatus.Uploading,
                bytesSent));
            ChunkReported?.Invoke(bytesSent);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Failure is not null)
        {
            progress?.Report(new GoogleDriveMediaUploadProgress(
                GoogleDriveMediaUploadProgressStatus.Failed,
                ChunkBytes.LastOrDefault()));
            throw Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new GoogleDriveMediaUploadProgress(
            GoogleDriveMediaUploadProgressStatus.Completed,
            expectedLength));
        BeforeReturn?.Invoke();
        await Task.CompletedTask;
        return Response;
    }

    public void Dispose()
    {
        DisposeCalls++;
        _disposed = true;
    }
}

internal sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];

    public void Report(T value) => Values.Add(value);
}
