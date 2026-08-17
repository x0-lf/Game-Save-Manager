using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using System.Security.Cryptography;

namespace GameSaves.Tests;

/// <summary>
/// Milestone S resource-disposal proof and the size and failure matrix. Every
/// case runs the real one-file download service against hermetic fakes.
/// </summary>
public sealed class GoogleDriveDownloadResilienceTests
{
    private const string RootId = "root-id";
    private const string FileId = "file-id";

    private static readonly Guid ProfileId =
        Guid.Parse("2f9a44c8-6de1-4d2b-8e77-53a1c0b9e642");

    public static TheoryData<int> DownloadSizes => new()
    {
        0,
        1,
        64 * 1024,
        (5 * 1024 * 1024) - 1,
        5 * 1024 * 1024,
        (5 * 1024 * 1024) + 1,
        (10 * 1024 * 1024) + 7
    };

    [Theory]
    [MemberData(nameof(DownloadSizes))]
    public async Task EverySize_TakesTheSameBoundaryAndPreservesExactBytes(int size)
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(size));

        GoogleDriveBinaryDownloadResult result = await harness.DownloadAsync();

        Assert.Equal(GoogleDriveBinaryDownloadStatus.Completed, result.Status);
        Assert.Equal(size, result.CompletedBytes);
        Assert.Equal(
            SHA256.HashData(Content(size)),
            SHA256.HashData(File.ReadAllBytes(area.FinalPath)));
        Assert.Equal(1, harness.Client.DownloadCalls);
        Assert.Equal(1, harness.Client.MetadataCalls);
        Assert.Empty(area.TemporaryFiles());
        harness.AssertEverythingReleasedOnce();
    }

    public static TheoryData<string> DisposalOutcomes => new()
    {
        "Success",
        "ValidationFailure",
        "ProviderFailure",
        "Cancellation"
    };

    [Theory]
    [MemberData(nameof(DisposalOutcomes))]
    public async Task EveryOutcome_ReleasesEachOwnedResourceExactlyOnce(
        string outcome)
    {
        using var area = new DownloadArea();
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness(area, Content(4096));

        switch (outcome)
        {
            case "ValidationFailure":
                harness.ReportedSize = 4095;
                break;
            case "ProviderFailure":
                harness.Client.MidStreamFailure =
                    new IOException("private-provider-marker");
                break;
            case "Cancellation":
                harness.Client.DuringDownload = cancellation.Cancel;
                break;
        }

        Exception? thrown = await Record.ExceptionAsync(() =>
            harness.DownloadAsync(cancellation.Token));

        switch (outcome)
        {
            case "Success":
                Assert.Null(thrown);
                Assert.True(File.Exists(area.FinalPath));
                break;
            case "ValidationFailure":
                Assert.IsType<GoogleDriveDownloadCompletionException>(thrown);
                Assert.False(File.Exists(area.FinalPath));
                break;
            case "ProviderFailure":
                Assert.IsType<GoogleDriveRemoteOperationException>(thrown);
                Assert.False(File.Exists(area.FinalPath));
                break;
            case "Cancellation":
                Assert.IsAssignableFrom<OperationCanceledException>(thrown);
                Assert.False(File.Exists(area.FinalPath));
                break;
        }

        Assert.Empty(area.TemporaryFiles());
        harness.AssertEverythingReleasedOnce();
    }

    [Fact]
    public async Task LateReturningProvider_LeavesNoBackgroundWorkBehind()
    {
        using var area = new DownloadArea();
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness(area, Content(1024));
        var released = new TaskCompletionSource();
        harness.Client.BeforeReturn = async () =>
        {
            cancellation.Cancel();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        Task<GoogleDriveBinaryDownloadResult> download =
            harness.DownloadAsync(cancellation.Token);
        await Task.Delay(50, CancellationToken.None);

        Assert.False(download.IsCompleted);
        Assert.Equal(0, harness.Client.DisposeCalls);

        released.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        harness.AssertEverythingReleasedOnce();
    }

    [Fact]
    public async Task DiskFullDuringTransfer_LeavesNoLocalFileAndStaysSanitized()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(8192))
        {
            DestinationFailure = new IOException(
                "There is not enough space on the disk. C:\\Users\\Someone\\save.bin")
        };

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => harness.DownloadAsync());

        Assert.Equal(
            GoogleDriveBinaryDownloadErrorCodes.Failed,
            exception.Result.ErrorCode);
        Assert.DoesNotContain(
            "Someone",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
        harness.AssertEverythingReleasedOnce();
    }

    [Fact]
    public async Task NetworkInterruption_IsReportedAsTemporarilyUnavailable()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(8192));
        harness.Client.MidStreamFailure = new HttpRequestException(
            "The connection was closed. https://www.googleapis.com/drive/v3/files/x");

        GoogleDriveRemoteOperationException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
                () => harness.DownloadAsync());

        Assert.Equal(
            "GoogleDriveDownloadUnavailable",
            exception.Result.ErrorCode);
        Assert.True(exception.Result.Retryable);
        Assert.DoesNotContain(
            "googleapis.com",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
    }

    [Fact]
    public async Task TruncatedBody_FailsClosedWithoutPlacing()
    {
        using var area = new DownloadArea();
        var harness = new Harness(area, Content(4096)) { WriteLimit = 2048 };

        GoogleDriveDownloadCompletionException exception =
            await Assert.ThrowsAsync<GoogleDriveDownloadCompletionException>(
                () => harness.DownloadAsync());

        Assert.Equal(
            GoogleDriveDownloadCompletionErrorCodes.SizeMismatch,
            exception.SafeErrorCode);
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
    }

    [Fact]
    public async Task OrphanTemporaryFileFromAnInterruptedRun_IsNeverTouched()
    {
        using var area = new DownloadArea();
        string orphan = area.WriteOrphanTemporaryFile([9, 9, 9]);
        var harness = new Harness(area, Content(2048));

        GoogleDriveBinaryDownloadResult result = await harness.DownloadAsync();

        Assert.Equal(GoogleDriveBinaryDownloadStatus.Completed, result.Status);
        Assert.True(File.Exists(area.FinalPath));
        Assert.True(File.Exists(orphan));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(orphan));
        Assert.Equal([orphan], area.TemporaryFiles());
    }

    [Fact]
    public async Task InterruptedRun_LeavesTheExistingLocalRunUntouched()
    {
        using var area = new DownloadArea();
        string sibling = area.Path("existing.sav");
        File.WriteAllBytes(sibling, [1, 2, 3, 4]);
        var harness = new Harness(area, Content(4096));
        harness.Client.MidStreamFailure = new IOException("interrupted");

        await Assert.ThrowsAsync<GoogleDriveRemoteOperationException>(
            () => harness.DownloadAsync());

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(sibling));
        Assert.False(File.Exists(area.FinalPath));
        Assert.Empty(area.TemporaryFiles());
    }

    private static byte[] Content(int length)
    {
        byte[] content = new byte[length];
        for (int index = 0; index < length; index++)
            content[index] = (byte)((index * 7) % 251);
        return content;
    }

    private sealed class Harness
    {
        private readonly DownloadArea _area;
        private readonly byte[] _content;
        private DisposalTrackingFileStream? _destinationStream;

        public Harness(DownloadArea area, byte[] content)
        {
            _area = area;
            _content = content;
            Client = new ResilienceDownloadClient(this);
            Credential = CreateCredential();
            Context = new GoogleDriveRemoteOperationContext(
                ProfileId,
                RootId,
                Credential,
                new UnusedPathResolver());
            Service = new GoogleDriveBinaryDownloadService(
                OpenDestinationAsync,
                new StubContextFactory(Context),
                new GoogleDriveDownloadSourceResolver(
                    new StubChildEnumerationService(RootId, FileId)),
                new StubClientFactory(Client),
                new GoogleDriveDownloadContentStreamer());
        }

        public ResilienceDownloadClient Client { get; }

        public GoogleAuthorizedCredential Credential { get; }

        public GoogleDriveRemoteOperationContext Context { get; }

        public GoogleDriveBinaryDownloadService Service { get; }

        public long? ReportedSize { get; set; }

        public int? WriteLimit { get; set; }

        public Exception? DestinationFailure { get; set; }

        public byte[] Content => _content;

        public long DeclaredSize => ReportedSize ?? _content.LongLength;

        public Task<GoogleDriveBinaryDownloadResult> DownloadAsync(
            CancellationToken cancellationToken = default) =>
            Service.DownloadAsync(
                GoogleDriveBinaryDownloadRequest.Parse(ProfileId, "Run 42/save.bin"),
                _area.FinalPath,
                cancellationToken);

        public void AssertEverythingReleasedOnce()
        {
            Assert.NotNull(_destinationStream);
            Assert.Equal(1, _destinationStream!.DisposeCalls);
            Assert.Equal(1, Client.DisposeCalls);
            Assert.True(Context.IsDisposed);
            Assert.True(Credential.IsDisposed);
        }

        private Task<GoogleDriveLocalDownloadDestination> OpenDestinationAsync(
            string localFilePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string temporaryPath =
                GoogleDriveLocalDownloadDestinationOpener.TemporaryPathFor(
                    localFilePath);
            GoogleDriveLocalDownloadDestinationOpener.ValidateAvailable(
                localFilePath);
            _destinationStream = new DisposalTrackingFileStream(
                temporaryPath,
                WriteLimit,
                DestinationFailure);
            return Task.FromResult(new GoogleDriveLocalDownloadDestination(
                localFilePath,
                temporaryPath,
                _destinationStream));
        }

        private static GoogleAuthorizedCredential CreateCredential()
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
                ProfileId.ToString("D"),
                new TokenResponse { AccessToken = "test-access-token" }));
        }
    }

    private sealed class DownloadArea : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gamesaves-s13-{Guid.NewGuid():N}");

        public DownloadArea()
        {
            Directory.CreateDirectory(_root);
            FinalPath = System.IO.Path.Combine(_root, "save.bin");
        }

        public string FinalPath { get; }

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public string WriteOrphanTemporaryFile(byte[] content)
        {
            string orphan = Path(
                $"save.bin.orphan{GoogleDriveLocalDownloadDestination.TemporarySuffix}");
            File.WriteAllBytes(orphan, content);
            return orphan;
        }

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

    private sealed class DisposalTrackingFileStream : FileStream
    {
        private readonly int? _writeLimit;
        private readonly Exception? _failure;
        private long _written;

        public DisposalTrackingFileStream(
            string path,
            int? writeLimit,
            Exception? failure)
            : base(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        {
            _writeLimit = writeLimit;
            _failure = failure;
        }

        public int DisposeCalls { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_failure is not null)
                return ValueTask.FromException(_failure);

            if (_writeLimit is int limit)
            {
                // Simulates a body that stops short: the provider reports a
                // full transfer while the file receives fewer bytes.
                long remaining = Math.Max(0, limit - _written);
                if (remaining == 0)
                    return ValueTask.CompletedTask;
                if (buffer.Length > remaining)
                    buffer = buffer[..(int)remaining];
            }

            _written += buffer.Length;
            return base.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class ResilienceDownloadClient(Harness harness)
        : IGoogleDriveMediaDownloadClient
    {
        public Exception? MidStreamFailure { get; set; }

        public Action? DuringDownload { get; set; }

        public Func<Task>? BeforeReturn { get; set; }

        public int MetadataCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<GoogleDriveMediaDownloadMetadata> GetMetadataAsync(
            string fileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetadataCalls++;
            return Task.FromResult(new GoogleDriveMediaDownloadMetadata(
                fileId,
                "save.bin",
                "application/octet-stream",
                trashed: false,
                parentIds: ["run-id"],
                driveId: null,
                size: harness.DeclaredSize));
        }

        public async Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;

            byte[] content = harness.Content;
            const int chunk = 4096;
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

                if (MidStreamFailure is not null && written >= content.Length / 2)
                    throw MidStreamFailure;
            }

            if (BeforeReturn is not null)
                await BeforeReturn();

            return written;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class StubContextFactory(
        GoogleDriveRemoteOperationContext context)
        : IGoogleDriveRemoteOperationContextFactory
    {
        public Task<GoogleDriveRemoteOperationContext> CreateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }

    private sealed class StubClientFactory(ResilienceDownloadClient client)
        : IGoogleDriveMediaDownloadClientFactory
    {
        public IGoogleDriveMediaDownloadClient Create(
            GoogleAuthorizedCredential credential) => client;
    }

    private sealed class StubChildEnumerationService(string rootId, string fileId)
        : IGoogleDriveFolderChildEnumerationService
    {
        public Task<IReadOnlyList<GoogleDriveFolderChildEntry>> EnumerateAsync(
            GoogleDriveRemoteOperationContext context,
            string parentFolderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GoogleDriveFolderChildEntry> children =
                string.Equals(parentFolderId, rootId, StringComparison.Ordinal)
                    ? [Entry("run-id", "Run 42", rootId, folder: true)]
                    : [Entry(fileId, "save.bin", "run-id", folder: false)];
            return Task.FromResult(children);
        }

        private static GoogleDriveFolderChildEntry Entry(
            string id,
            string name,
            string parentId,
            bool folder) =>
            new(
                id,
                name,
                folder
                    ? GoogleDriveApplicationRoot.FolderMimeType
                    : "application/octet-stream",
                folder
                    ? GoogleDriveRecursiveObjectKind.Folder
                    : GoogleDriveRecursiveObjectKind.BlobFile,
                [parentId],
                trashed: false,
                driveId: null);
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
