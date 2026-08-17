using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadPlacementTests
{
    [Fact]
    public async Task ValidatedTemporaryFile_MovesToItsFinalName()
    {
        using var temp = new TemporaryPlacementArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        await destination.Stream.WriteAsync(new byte[] { 1, 2, 3 });

        GoogleDriveDownloadPlacement.Place(destination);

        Assert.True(File.Exists(destination.FinalPath));
        Assert.False(File.Exists(destination.TemporaryPath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(destination.FinalPath));
        Assert.Empty(temp.TemporaryFiles());
    }

    [Fact]
    public async Task ZeroByteDownload_IsPlacedAsAnEmptyFile()
    {
        using var temp = new TemporaryPlacementArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("empty.bin");

        GoogleDriveDownloadPlacement.Place(destination);

        Assert.True(File.Exists(destination.FinalPath));
        Assert.Empty(File.ReadAllBytes(destination.FinalPath));
    }

    [Fact]
    public async Task DestinationThatAppearedDuringTheTransfer_IsNeverOverwritten()
    {
        using var temp = new TemporaryPlacementArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        await destination.Stream.WriteAsync(new byte[] { 9, 9, 9 });
        File.WriteAllBytes(destination.FinalPath, [7, 7]);

        GoogleDriveLocalDownloadDestinationException exception =
            Assert.Throws<GoogleDriveLocalDownloadDestinationException>(() =>
                GoogleDriveDownloadPlacement.Place(destination));

        Assert.Equal(
            "GoogleDriveDownloadDestinationExists",
            exception.SafeErrorCode);
        Assert.Equal([7, 7], File.ReadAllBytes(destination.FinalPath));
        Assert.True(File.Exists(destination.TemporaryPath));
    }

    [Fact]
    public async Task DirectoryThatAppearedAtTheFinalPath_IsNeverReplaced()
    {
        using var temp = new TemporaryPlacementArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        Directory.CreateDirectory(destination.FinalPath);
        File.WriteAllText(Path.Combine(destination.FinalPath, "keep.txt"), "keep");

        GoogleDriveLocalDownloadDestinationException exception =
            Assert.Throws<GoogleDriveLocalDownloadDestinationException>(() =>
                GoogleDriveDownloadPlacement.Place(destination));

        Assert.Equal(
            "GoogleDriveDownloadDestinationExists",
            exception.SafeErrorCode);
        Assert.Equal(
            "keep",
            File.ReadAllText(Path.Combine(destination.FinalPath, "keep.txt")));
    }

    [Fact]
    public async Task PlacementClosesTheHandleSoTheFileIsImmediatelyUsable()
    {
        using var temp = new TemporaryPlacementArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        await destination.Stream.WriteAsync(new byte[] { 4, 5, 6 });

        GoogleDriveDownloadPlacement.Place(destination);

        Assert.False(destination.Stream.CanWrite);
        using var reader = new FileStream(
            destination.FinalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        Assert.Equal(3, reader.Length);
    }

    [Fact]
    public async Task CancelledPlacement_CreatesNoFinalFile()
    {
        using var temp = new TemporaryPlacementArea();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");

        Assert.ThrowsAny<OperationCanceledException>(() =>
            GoogleDriveDownloadPlacement.Place(destination, cancellation.Token));

        Assert.False(File.Exists(destination.FinalPath));
        Assert.True(File.Exists(destination.TemporaryPath));
        Assert.True(destination.Stream.CanWrite);
        destination.Dispose();
    }

    [Fact]
    public void InvalidPlacementInput_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GoogleDriveDownloadPlacement.Place(null!));
    }

    [Fact]
    public void PlacementSource_NeverOverwritesOrDeletes()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveDownloadPlacement.cs"));

        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Replace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", source, StringComparison.Ordinal);
        Assert.Contains("overwrite: false", source, StringComparison.Ordinal);
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

    private sealed class TemporaryPlacementArea : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"gamesaves-s8-{Guid.NewGuid():N}");

        public TemporaryPlacementArea() => Directory.CreateDirectory(_root);

        public Task<GoogleDriveLocalDownloadDestination> PrepareAsync(string name) =>
            new GoogleDriveLocalDownloadDestinationOpener().OpenAsync(
                Path.Combine(_root, name));

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
}
