using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveMediaDownloadClientTests
{
    [Fact]
    public void Progress_ValidatesAndFormatsOnlyStateAndCounts()
    {
        var progress = new GoogleDriveMediaDownloadProgress(
            GoogleDriveMediaDownloadProgressStatus.Downloading,
            42);

        Assert.Equal(
            GoogleDriveMediaDownloadProgressStatus.Downloading,
            progress.Status);
        Assert.Equal(42, progress.BytesDownloaded);
        Assert.Equal(
            "Google Drive media download progress: status=Downloading; bytesDownloaded=42",
            progress.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveMediaDownloadProgress(
                (GoogleDriveMediaDownloadProgressStatus)int.MaxValue,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveMediaDownloadProgress(
                GoogleDriveMediaDownloadProgressStatus.Downloading,
                -1));
    }

    [Fact]
    public void Boundary_IsInfrastructureInternalFakeableAndSdkFree()
    {
        Type[] boundaryTypes =
        [
            typeof(IGoogleDriveMediaDownloadClient),
            typeof(IGoogleDriveMediaDownloadClientFactory),
            typeof(GoogleDriveMediaDownloadProgress),
            typeof(GoogleDriveMediaDownloadProgressStatus)
        ];

        Assert.All(boundaryTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            AssertNoGoogleSdkType(type);
        });
        Assert.True(typeof(IGoogleDriveMediaDownloadClient).IsAssignableFrom(
            typeof(BoundaryFakeClient)));

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
        var factory = new GoogleDriveMediaDownloadClientFactory();
        using GoogleAuthorizedCredential credential = Credential();

        IGoogleDriveMediaDownloadClient first = factory.Create(credential);
        IGoogleDriveMediaDownloadClient second = factory.Create(credential);

        Assert.IsType<GoogleDriveMediaDownloadClient>(first);
        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();

        Assert.True(((GoogleDriveMediaDownloadClient)first).IsDisposed);
        Assert.True(((GoogleDriveMediaDownloadClient)second).IsDisposed);
        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }

    [Fact]
    public void SdkClient_DisposesOwnedDriveServiceExactlyOnce()
    {
        var drive = new DisposalTrackingDriveService();
        var client = new GoogleDriveMediaDownloadClient(drive);

        client.Dispose();
        client.Dispose();

        Assert.True(client.IsDisposed);
        Assert.Equal(1, drive.DisposeCalls);
    }

    [Fact]
    public void SdkAdapter_BuildsAReadOnlyMediaRequest()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });

        FilesResource.GetRequest request =
            GoogleDriveMediaDownloadClient.CreateSdkRequest(
                drive,
                "private-id-marker");

        Assert.Equal("private-id-marker", request.FileId);
        Assert.False(request.SupportsAllDrives);
        Assert.False(request.AcknowledgeAbuse);
        Assert.Null(request.IncludePermissionsForView);
        Assert.Null(request.Fields);
        Assert.Equal("GET", request.HttpMethod);
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveMediaDownloadClient.CreateSdkRequest(drive, "  "));
        Assert.Throws<ArgumentNullException>(() =>
            GoogleDriveMediaDownloadClient.CreateSdkRequest(null!, "id"));
    }

    [Theory]
    [InlineData(DownloadStatus.NotStarted,
        (int)GoogleDriveMediaDownloadProgressStatus.NotStarted)]
    [InlineData(DownloadStatus.Downloading,
        (int)GoogleDriveMediaDownloadProgressStatus.Downloading)]
    [InlineData(DownloadStatus.Completed,
        (int)GoogleDriveMediaDownloadProgressStatus.Completed)]
    [InlineData(DownloadStatus.Failed,
        (int)GoogleDriveMediaDownloadProgressStatus.Failed)]
    public void SdkProgress_IsMappedWithoutProviderExceptions(
        DownloadStatus sdkStatus,
        int expectedStatus)
    {
        var sdkProgress = new StubDownloadProgress(
            sdkStatus,
            42,
            new IOException("private-provider-marker"));

        GoogleDriveMediaDownloadProgress progress =
            GoogleDriveMediaDownloadClient.MapProgress(sdkProgress);

        Assert.Equal(
            (GoogleDriveMediaDownloadProgressStatus)expectedStatus,
            progress.Status);
        Assert.Equal(42, progress.BytesDownloaded);
        Assert.DoesNotContain(
            "private-provider-marker",
            progress.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposedSdkClient_RejectsDownloadWithoutNetworkWork()
    {
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            ApplicationName = "Game Save Manager Tests"
        });
        var client = new GoogleDriveMediaDownloadClient(drive);
        client.Dispose();
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.DownloadAsync(
                "private-id-marker",
                destination,
                progress: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task InvalidInput_IsRejectedBeforeAnyRequest()
    {
        var handler = new BlockingRequestHandler();
        using var client = new GoogleDriveMediaDownloadClient(
            DriveWithHandler(handler));
        using var destination = new MemoryStream();
        using var readOnly = new MemoryStream([1], writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DownloadAsync("  ", destination, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.DownloadAsync("id", null!, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DownloadAsync("id", readOnly, null, CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SdkClient_PreCanceledTokenStopsBeforeTheRequest()
    {
        var handler = new BlockingRequestHandler();
        using var client = new GoogleDriveMediaDownloadClient(
            DriveWithHandler(handler));
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadAsync(
                "private-id-marker",
                destination,
                progress: null,
                cancellation.Token));

        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task SdkClient_ForwardsCancellationDuringTheRequest()
    {
        var handler = new BlockingRequestHandler();
        using var client = new GoogleDriveMediaDownloadClient(
            DriveWithHandler(handler));
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();

        Task<long> download = client.DownloadAsync(
            "private-id-marker",
            destination,
            progress: null,
            cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SdkClient_HandlesExactlyOneDownload()
    {
        var handler = new BlockingRequestHandler();
        using var client = new GoogleDriveMediaDownloadClient(
            DriveWithHandler(handler));
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();

        Task<long> first = client.DownloadAsync(
            "private-id-marker",
            destination,
            progress: null,
            cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DownloadAsync(
                "private-id-marker",
                destination,
                progress: null,
                CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task DeterministicFake_StreamsChunkedContentWithProgress()
    {
        var drive = new OfflineDriveStore("root-id");
        byte[] content = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        drive.AddFile("file-id", "save.bin", "root-id", content, "application/octet-stream");
        var factory = new OfflineDriveMediaDownloadClientFactory(drive)
        {
            ChunkSize = 4
        };
        var progress = new RecordingProgress<GoogleDriveMediaDownloadProgress>();
        using var destination = new MemoryStream();
        using GoogleAuthorizedCredential credential = Credential();

        using IGoogleDriveMediaDownloadClient client = factory.Create(credential);
        long bytes = await client.DownloadAsync(
            "file-id",
            destination,
            progress,
            CancellationToken.None);

        Assert.Equal(content.Length, bytes);
        Assert.Equal(content, destination.ToArray());
        Assert.Equal(
            [0, 4, 8, 10, 10],
            progress.Values.Select(value => value.BytesDownloaded));
        Assert.Equal(
            GoogleDriveMediaDownloadProgressStatus.Completed,
            progress.Values[^1].Status);
        MediaDownloadCall call = Assert.Single(factory.Calls);
        Assert.Equal("file-id", call.FileId);
        Assert.Equal(content.Length, call.BytesWritten);
    }

    [Fact]
    public async Task DeterministicFake_SupportsInjectedFailureAndCancellation()
    {
        var drive = new OfflineDriveStore("root-id");
        drive.AddFile("file-id", "save.bin", "root-id", new byte[16], "application/octet-stream");
        var failure = new IOException("private-provider-marker");
        var factory = new OfflineDriveMediaDownloadClientFactory(drive)
        {
            ChunkSize = 4,
            FailureFor = _ => failure
        };
        using var destination = new MemoryStream();
        using GoogleAuthorizedCredential credential = Credential();

        using (IGoogleDriveMediaDownloadClient failing = factory.Create(credential))
        {
            IOException thrown = await Assert.ThrowsAsync<IOException>(() =>
                failing.DownloadAsync("file-id", destination, null, CancellationToken.None));
            Assert.Same(failure, thrown);
        }

        Assert.Empty(factory.Calls);
        Assert.Equal(0, destination.Length);

        factory.FailureFor = null;
        using var cancellation = new CancellationTokenSource();
        factory.ChunkWritten = written =>
        {
            if (written >= 8)
                cancellation.Cancel();
        };

        IGoogleDriveMediaDownloadClient cancelled = factory.Create(credential);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelled.DownloadAsync("file-id", destination, null, cancellation.Token));
        cancelled.Dispose();

        Assert.Empty(factory.Calls);
        Assert.Equal(2, factory.CreatedClients);
        Assert.Equal(2, factory.DisposedClients);
        Assert.InRange(destination.Length, 1, 16);
    }

    [Fact]
    public void DownloadBoundary_IsNotWiredIntoTheRemoteFileSystemYet()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveRemoteFileSystem.cs"));

        Assert.DoesNotContain(
            "IGoogleDriveMediaDownloadClient",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Unsupported<long>();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSource_StaysReadOnlyAndMediaOnly()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveMediaDownloadClient.cs"));

        Assert.DoesNotContain("Files.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Files.Update", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Files.Create", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Export", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Permissions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.Contains("AcknowledgeAbuse = false", source, StringComparison.Ordinal);
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

    private static string FindManagerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Manager.sln from the test output directory.");
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
        return new GoogleAuthorizedCredential(new UserCredential(
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

    private sealed class BoundaryFakeClient : IGoogleDriveMediaDownloadClient
    {
        public Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
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

    private sealed class BlockingRequestHandler : HttpMessageHandler
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

    private sealed class StubDownloadProgress(
        DownloadStatus status,
        long bytesDownloaded,
        Exception? exception) : IDownloadProgress
    {
        public DownloadStatus Status { get; } = status;

        public long BytesDownloaded { get; } = bytesDownloaded;

        public Exception? Exception { get; } = exception;
    }
}
