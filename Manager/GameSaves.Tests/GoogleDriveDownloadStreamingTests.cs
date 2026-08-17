using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadStreamingTests
{
    [Fact]
    public async Task Streaming_HandsTheDestinationStreamToTheClientUnchanged()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        var client = new RecordingDownloadClient(Content(2048));

        long bytes = await new GoogleDriveDownloadContentStreamer().StreamAsync(
            client,
            "file-id",
            destination);

        Assert.Same(destination.Stream, client.Destination);
        Assert.Equal(2048, bytes);
        Assert.Equal("file-id", client.FileId);
        destination.Dispose();
        Assert.Equal(Content(2048), File.ReadAllBytes(destination.TemporaryPath));
        Assert.False(File.Exists(destination.FinalPath));
    }

    [Fact]
    public async Task ZeroByteContent_ProducesAnEmptyTemporaryFile()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("empty.bin");
        var client = new RecordingDownloadClient([]);

        long bytes = await new GoogleDriveDownloadContentStreamer().StreamAsync(
            client,
            "file-id",
            destination);

        Assert.Equal(0, bytes);
        Assert.True(File.Exists(destination.TemporaryPath));
        destination.Dispose();
        Assert.Empty(File.ReadAllBytes(destination.TemporaryPath));
    }

    [Fact]
    public async Task LargeContent_IsWrittenInBoundedChunksWithExactBytes()
    {
        const int length = (5 * 1024 * 1024) + 7;
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("large.bin", maximumWrite: 64 * 1024);
        var client = new RecordingDownloadClient(Content(length))
        {
            ChunkSize = 32 * 1024
        };

        long bytes = await new GoogleDriveDownloadContentStreamer().StreamAsync(
            client,
            "file-id",
            destination);

        Assert.Equal(length, bytes);
        destination.Dispose();
        Assert.Equal(length, new FileInfo(destination.TemporaryPath).Length);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(Content(length)),
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(destination.TemporaryPath)));
    }

    [Fact]
    public async Task ContentThatFailsAnEagerCopy_StillDownloads()
    {
        const int length = 1024 * 1024;
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("streamed.bin", maximumWrite: 8 * 1024);
        var client = new RecordingDownloadClient(Content(length))
        {
            ChunkSize = 4 * 1024
        };

        long bytes = await new GoogleDriveDownloadContentStreamer().StreamAsync(
            client,
            "file-id",
            destination);

        Assert.Equal(length, bytes);
        Assert.True(client.WriteCount > 1);
    }

    [Fact]
    public async Task Streaming_ForwardsProgressAndTheCallerToken()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress<GoogleDriveMediaDownloadProgress>();
        var client = new RecordingDownloadClient(Content(64)) { ChunkSize = 16 };

        await new GoogleDriveDownloadContentStreamer().StreamAsync(
            client,
            "file-id",
            destination,
            progress,
            cancellation.Token);

        Assert.Equal(cancellation.Token, client.CancellationToken);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(64, progress.Values[^1].BytesDownloaded);
    }

    [Fact]
    public async Task PreCanceledStreaming_NeverReachesTheClient()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RecordingDownloadClient(Content(16));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GoogleDriveDownloadContentStreamer().StreamAsync(
                client,
                "file-id",
                destination,
                progress: null,
                cancellation.Token));

        Assert.Equal(0, client.DownloadCalls);
        Assert.False(File.Exists(destination.FinalPath));
    }

    [Fact]
    public async Task ProviderFailure_PropagatesAndLeavesNoFinalFile()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        var failure = new IOException("private-provider-marker");
        var client = new RecordingDownloadClient(Content(16)) { Failure = failure };

        IOException thrown = await Assert.ThrowsAsync<IOException>(() =>
            new GoogleDriveDownloadContentStreamer().StreamAsync(
                client,
                "file-id",
                destination));

        Assert.Same(failure, thrown);
        Assert.False(File.Exists(destination.FinalPath));
    }

    [Fact]
    public async Task InvalidStreamingInputs_AreRejected()
    {
        using var temp = new TemporaryDownloadArea();
        using GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        var streamer = new GoogleDriveDownloadContentStreamer();
        var client = new RecordingDownloadClient([]);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            streamer.StreamAsync(null!, "file-id", destination));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            streamer.StreamAsync(client, "file-id", null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            streamer.StreamAsync(client, "  ", destination));
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public void StreamerSource_MaterializesNoPayloadCopy()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveDownloadContentStreamer.cs"));

        Assert.DoesNotContain("MemoryStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new byte[", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToArray", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyTo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", source, StringComparison.Ordinal);
        Assert.Contains("destination.Stream", source, StringComparison.Ordinal);
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

    private sealed class RecordingDownloadClient(byte[] content)
        : IGoogleDriveMediaDownloadClient
    {
        public int ChunkSize { get; set; } = 4096;

        public Exception? Failure { get; set; }

        public Stream? Destination { get; private set; }

        public string? FileId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public int DownloadCalls { get; private set; }

        public int WriteCount { get; private set; }

        public Task<GoogleDriveMediaDownloadMetadata> GetMetadataAsync(
            string fileId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<long> DownloadAsync(
            string fileId,
            Stream destination,
            IProgress<GoogleDriveMediaDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            FileId = fileId;
            Destination = destination;
            CancellationToken = cancellationToken;

            if (Failure is not null)
                throw Failure;

            long written = 0;
            for (int offset = 0; offset < content.Length; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(ChunkSize, content.Length - offset);
                await destination.WriteAsync(
                    content.AsMemory(offset, length),
                    cancellationToken);
                WriteCount++;
                written += length;
                progress?.Report(new GoogleDriveMediaDownloadProgress(
                    GoogleDriveMediaDownloadProgressStatus.Downloading,
                    written));
            }

            progress?.Report(new GoogleDriveMediaDownloadProgress(
                GoogleDriveMediaDownloadProgressStatus.Completed,
                written));
            return written;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDownloadArea : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"gamesaves-s6-{Guid.NewGuid():N}");

        public TemporaryDownloadArea() => Directory.CreateDirectory(_root);

        public async Task<GoogleDriveLocalDownloadDestination> PrepareAsync(
            string name,
            int? maximumWrite = null)
        {
            string finalPath = Path.Combine(_root, name);
            if (maximumWrite is null)
                return await new GoogleDriveLocalDownloadDestinationOpener().OpenAsync(finalPath);

            string temporaryPath =
                GoogleDriveLocalDownloadDestinationOpener.TemporaryPathFor(finalPath);
            return new GoogleDriveLocalDownloadDestination(
                finalPath,
                temporaryPath,
                new BoundedWriteFileStream(temporaryPath, maximumWrite.Value));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Fails if any single write exceeds the bound, so an eager whole-file
    /// copy cannot pass.
    /// </summary>
    private sealed class BoundedWriteFileStream(string path, int maximumWrite)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Guard(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Guard(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        private void Guard(int count)
        {
            if (count > maximumWrite)
            {
                throw new InvalidOperationException(
                    "The download materialized more than one bounded chunk.");
            }
        }
    }
}
