using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadServiceTests
{
    private const string RootId = "root-id";
    private const string FileId = "file-id";

    private static readonly Guid ProfileId =
        Guid.Parse("9b3f1c26-6d51-4c78-8a0e-4b2f7d5a9e10");

    [Fact]
    public async Task DownloadAsync_ComposesOneCompleteFileDownload()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(4096));

        GoogleDriveBinaryDownloadResult result = await harness.Service.DownloadAsync(
            GoogleDriveBinaryDownloadRequest.Parse(ProfileId, "Run 42/save.bin"),
            area.FinalPath);

        Assert.Equal(GoogleDriveBinaryDownloadStatus.Completed, result.Status);
        Assert.Equal(4096, result.CompletedBytes);
        Assert.Null(result.SafeErrorCode);
        Assert.Equal(Content(4096), File.ReadAllBytes(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        Assert.True(harness.Context.IsDisposed);
        Assert.Equal(1, harness.MediaClient.DisposeCalls);
        Assert.Equal(ProfileId, harness.ContextFactory.ProfileId);
    }

    [Fact]
    public async Task DownloadAsync_ForwardsTheCallerTokenToEveryProviderCall()
    {
        using var area = new DownloadArea();
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness(area, Content(64));

        await harness.Service.DownloadAsync(
            GoogleDriveBinaryDownloadRequest.Parse(ProfileId, "Run 42/save.bin"),
            area.FinalPath,
            cancellation.Token);

        Assert.Equal(cancellation.Token, harness.ContextFactory.CancellationToken);
        Assert.Equal(cancellation.Token, harness.Enumeration.CancellationTokens.Last());
        Assert.Equal(cancellation.Token, harness.MediaClient.MetadataToken);
        Assert.Equal(cancellation.Token, harness.MediaClient.DownloadToken);
        Assert.Equal(cancellation.Token, harness.DestinationToken);
    }

    public static TheoryData<string> CancellationBoundaries => new()
    {
        "BeforeStart",
        "DuringDestination",
        "AfterDestination",
        "AfterContext",
        "AfterResolve",
        "AfterMetadata",
        "DuringStream",
        "AfterStream"
    };

    [Theory]
    [MemberData(nameof(CancellationBoundaries))]
    public async Task Cancellation_AtEveryBoundaryLeavesNoLocalFile(string boundary)
    {
        using var area = new DownloadArea();
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness(area, Content(8192));
        harness.CancelAt(boundary, cancellation);

        if (boundary == "BeforeStart")
            cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Service.DownloadAsync(
                GoogleDriveBinaryDownloadRequest.Parse(ProfileId, "Run 42/save.bin"),
                area.FinalPath,
                cancellation.Token));

        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        if (boundary is not ("BeforeStart" or "DuringDestination" or "AfterDestination"))
            Assert.True(harness.Context.IsDisposed);
    }

    [Fact]
    public async Task CancellationDuringPlacement_LeavesNoFinalOrTemporaryFile()
    {
        using var area = new DownloadArea();
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness(area, Content(16));
        harness.MediaClient.AfterDownload = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Service.DownloadAsync(
                GoogleDriveBinaryDownloadRequest.Parse(ProfileId, "Run 42/save.bin"),
                area.FinalPath,
                cancellation.Token));

        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        Assert.True(harness.Context.IsDisposed);
        Assert.Equal(1, harness.MediaClient.DisposeCalls);
    }

    [Fact]
    public async Task ProviderFailure_EscapesSanitizedAndRemovesTheTemporaryFile()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(16));
        var failure = new IOException(
            @"private-provider-marker C:\Users\Someone\Personal Save.bin");
        harness.MediaClient.DownloadFailure = failure;

        GoogleDriveRemoteOperationException thrown =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Service.DownloadAsync(
                    GoogleDriveBinaryDownloadRequest.Parse(
                        ProfileId,
                        "Run 42/save.bin"),
                    area.FinalPath));

        Assert.NotSame(failure, thrown);
        Assert.Equal(
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            thrown.Result.ErrorCode);
        Assert.Null(thrown.InnerException);
        Assert.DoesNotContain(
            "private-provider-marker",
            thrown.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Personal Save.bin",
            thrown.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        Assert.True(harness.Context.IsDisposed);
    }

    [Fact]
    public async Task SizeMismatch_NeverPlacesAndRemovesTheTemporaryFile()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(16)) { ReportedSize = 15 };

        GoogleDriveDownloadCompletionException exception =
            await Assert.ThrowsAsync<GoogleDriveDownloadCompletionException>(() =>
                harness.Service.DownloadAsync(
                    GoogleDriveBinaryDownloadRequest.Parse(
                        ProfileId,
                        "Run 42/save.bin"),
                    area.FinalPath));

        Assert.Equal(
            GoogleDriveDownloadCompletionErrorCodes.SizeMismatch,
            exception.SafeErrorCode);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
    }

    [Fact]
    public async Task ExistingDestination_IsRefusedBeforeAnyProviderWork()
    {
        using var area = new DownloadArea();
        File.WriteAllBytes(area.FinalPath, [7, 7]);
        var harness = new Harness(area, Content(16));

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => harness.Service.DownloadAsync(
                    GoogleDriveBinaryDownloadRequest.Parse(
                        ProfileId,
                        "Run 42/save.bin"),
                    area.FinalPath));

        Assert.Equal(
            "GoogleDriveDownloadDestinationExists",
            exception.SafeErrorCode);
        Assert.Equal([7, 7], File.ReadAllBytes(area.FinalPath));
        Assert.Null(harness.ContextFactory.ProfileId);
        Assert.Equal(0, harness.MediaClient.DownloadCalls);
        Assert.Empty(area.TemporaryFiles());
    }

    [Fact]
    public async Task UnresolvableSource_RemovesTheTemporaryFileAndKeepsItsCode()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(16));
        harness.Enumeration.Children[RootId] = [];

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(() =>
                harness.Service.DownloadAsync(
                    GoogleDriveBinaryDownloadRequest.Parse(
                        ProfileId,
                        "Run 42/save.bin"),
                    area.FinalPath));

        Assert.Equal(
            GoogleDriveDownloadSourceErrorCodes.NotFound,
            exception.Result.ErrorCode);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        Assert.Equal(0, harness.MediaClient.DownloadCalls);
    }

    [Fact]
    public void InvalidServiceConstruction_IsRejected()
    {
        var streamer = new GoogleDriveDownloadContentStreamer();
        var resolver = new GoogleDriveDownloadSourceResolver(
            new StubChildEnumerationService());

        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveBinaryDownloadService(null!, null!, resolver, null!, streamer));
    }

    [Fact]
    public void ServiceSource_OwnsOneFileAndNoRunOrProviderWork()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveBinaryDownloadService.cs"));

        Assert.DoesNotContain("manifest.json", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnumerateFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ListFilesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", source, StringComparison.Ordinal);
        Assert.Contains(
            "GoogleDriveDownloadTemporaryFileCleanup.Remove",
            source,
            StringComparison.Ordinal);
    }

    private static byte[] Content(int length)
    {
        byte[] content = new byte[length];
        for (int index = 0; index < length; index++)
            content[index] = (byte)(index % 251);
        return content;
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

    private sealed class Harness
    {
        private readonly DownloadArea _area;

        public Harness(DownloadArea area, byte[] content)
        {
            _area = area;
            Content = content;
            Enumeration = new StubChildEnumerationService
            {
                Children =
                {
                    [RootId] = [Folder("run-id", "Run 42", RootId)],
                    ["run-id"] = [Blob(FileId, "save.bin", "run-id")]
                }
            };
            Context = CreateContext();
            ContextFactory = new RecordingContextFactory(Context);
            MediaClient = new StubMediaDownloadClient(this);
            Service = new GoogleDriveBinaryDownloadService(
                OpenDestinationAsync,
                ContextFactory,
                new GoogleDriveDownloadSourceResolver(Enumeration),
                new StubMediaClientFactory(MediaClient),
                new GoogleDriveDownloadContentStreamer());
        }

        public byte[] Content { get; }

        public long? ReportedSize { get; set; }

        public StubChildEnumerationService Enumeration { get; }

        public GoogleDriveRemoteOperationContext Context { get; }

        public RecordingContextFactory ContextFactory { get; }

        public StubMediaDownloadClient MediaClient { get; }

        public GoogleDriveBinaryDownloadService Service { get; }

        public CancellationToken DestinationToken { get; private set; }

        public Action? AfterDestination { get; set; }

        public void CancelAt(string boundary, CancellationTokenSource cancellation)
        {
            switch (boundary)
            {
                case "DuringDestination":
                    DuringDestination = cancellation.Cancel;
                    break;
                case "AfterDestination":
                    AfterDestination = cancellation.Cancel;
                    break;
                case "AfterContext":
                    ContextFactory.AfterCreate = cancellation.Cancel;
                    break;
                case "AfterResolve":
                    Enumeration.AfterEnumerate = cancellation.Cancel;
                    break;
                case "AfterMetadata":
                    MediaClient.AfterMetadata = cancellation.Cancel;
                    break;
                case "DuringStream":
                    MediaClient.DuringDownload = cancellation.Cancel;
                    break;
                case "AfterStream":
                    MediaClient.AfterDownload = cancellation.Cancel;
                    break;
            }
        }

        public Action? DuringDestination { get; set; }

        private async Task<GoogleDriveLocalDownloadDestination> OpenDestinationAsync(
            string localFilePath,
            CancellationToken cancellationToken)
        {
            DestinationToken = cancellationToken;
            DuringDestination?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            GoogleDriveLocalDownloadDestination destination =
                await new GoogleDriveLocalDownloadDestinationOpener().OpenAsync(
                    localFilePath,
                    cancellationToken);
            AfterDestination?.Invoke();
            return destination;
        }

        private static GoogleDriveRemoteOperationContext CreateContext()
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
            var credential = new GoogleAuthorizedCredential(new UserCredential(
                flow,
                ProfileId.ToString("D"),
                new TokenResponse { AccessToken = "test-access-token" }));
            return new GoogleDriveRemoteOperationContext(
                ProfileId,
                RootId,
                credential,
                new UnusedPathResolver());
        }

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
                "application/octet-stream",
                GoogleDriveRecursiveObjectKind.BlobFile,
                [parentId],
                trashed: false,
                driveId: null);
    }

    private sealed class DownloadArea : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"gamesaves-s10-{Guid.NewGuid():N}");

        public DownloadArea()
        {
            Directory.CreateDirectory(_root);
            FinalPath = Path.Combine(_root, "save.bin");
        }

        public string FinalPath { get; }

        public string[] TemporaryFiles() =>
            Directory.GetFiles(
                _root,
                $"*{GoogleDriveLocalDownloadDestination.TemporarySuffix}",
                SearchOption.AllDirectories);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingContextFactory(
        GoogleDriveRemoteOperationContext context)
        : IGoogleDriveRemoteOperationContextFactory
    {
        public Guid? ProfileId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Action? AfterCreate { get; set; }

        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileId = remoteProfileId;
            CancellationToken = cancellationToken;
            AfterCreate?.Invoke();
            return Task.FromResult(context);
        }
    }

    private sealed class StubMediaClientFactory(StubMediaDownloadClient client)
        : IGoogleDriveMediaDownloadClientFactory
    {
        public IGoogleDriveMediaDownloadClient Create(
            GoogleAuthorizedCredential credential)
        {
            Assert.False(credential.IsDisposed);
            return client;
        }
    }

    private sealed class StubMediaDownloadClient(GoogleDriveDownloadServiceHarnessContent owner)
        : IGoogleDriveMediaDownloadClient
    {
        private readonly GoogleDriveDownloadServiceHarnessContent _owner = owner;

        public StubMediaDownloadClient(Harness harness)
            : this(new GoogleDriveDownloadServiceHarnessContent(harness))
        {
        }

        public Exception? DownloadFailure { get; set; }

        public Action? AfterMetadata { get; set; }

        public Action? DuringDownload { get; set; }

        public Action? AfterDownload { get; set; }

        public CancellationToken MetadataToken { get; private set; }

        public CancellationToken DownloadToken { get; private set; }

        public int DownloadCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveMediaDownloadMetadata> GetMetadataAsync(
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetadataToken = cancellationToken;
            var metadata = new GoogleDriveMediaDownloadMetadata(
                fileId,
                "save.bin",
                "application/octet-stream",
                trashed: false,
                parentIds: ["run-id"],
                driveId: null,
                size: _owner.ReportedSize ?? _owner.Content.LongLength);
            AfterMetadata?.Invoke();
            return Task.FromResult(metadata);
        }

        public async Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            DownloadToken = cancellationToken;

            if (DownloadFailure is not null)
                throw DownloadFailure;

            byte[] content = _owner.Content;
            const int chunk = 1024;
            long written = 0;
            for (int offset = 0; offset < content.Length; offset += chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(chunk, content.Length - offset);
                await destination.WriteAsync(
                    content.AsMemory(offset, length),
                    cancellationToken);
                written += length;
                DuringDownload?.Invoke();
            }

            AfterDownload?.Invoke();
            return written;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class GoogleDriveDownloadServiceHarnessContent(Harness harness)
    {
        public byte[] Content => harness.Content;

        public long? ReportedSize => harness.ReportedSize;
    }

    private sealed class StubChildEnumerationService
        : IGoogleDriveFolderChildEnumerationService
    {
        public Dictionary<string, GoogleDriveFolderChildEntry[]> Children { get; } =
            new(StringComparer.Ordinal);

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Action? AfterEnumerate { get; set; }

        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokens.Add(cancellationToken);
            IReadOnlyList<GoogleDriveFolderChildEntry> result =
                Children.TryGetValue(parentFolderId, out GoogleDriveFolderChildEntry[]? entries)
                    ? entries
                    : [];
            AfterEnumerate?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class UnusedPathResolver : IGoogleDriveObjectPathResolver
    {
        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
