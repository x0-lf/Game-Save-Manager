using GameSaves.Infrastructure.GoogleDrive;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadCleanupTests
{
    [Fact]
    public async Task Cleanup_RemovesTheTemporaryFileAndClosesItsHandle()
    {
        using var temp = new TemporaryCleanupArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        await destination.Stream.WriteAsync(new byte[] { 1, 2, 3 });

        bool removed = GoogleDriveDownloadTemporaryFileCleanup.Remove(destination);

        Assert.True(removed);
        Assert.False(File.Exists(destination.TemporaryPath));
        Assert.False(destination.Stream.CanWrite);
        Assert.Empty(temp.TemporaryFiles());
    }

    [Fact]
    public async Task Cleanup_NeverTouchesTheFinalDestination()
    {
        using var temp = new TemporaryCleanupArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        File.WriteAllBytes(destination.FinalPath, [7, 7]);

        Assert.True(GoogleDriveDownloadTemporaryFileCleanup.Remove(destination));

        Assert.True(File.Exists(destination.FinalPath));
        Assert.Equal([7, 7], File.ReadAllBytes(destination.FinalPath));
    }

    [Fact]
    public async Task Cleanup_IsIdempotentAndReportsNothingToRemove()
    {
        using var temp = new TemporaryCleanupArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");

        Assert.True(GoogleDriveDownloadTemporaryFileCleanup.Remove(destination));
        Assert.False(GoogleDriveDownloadTemporaryFileCleanup.Remove(destination));
    }

    [Fact]
    public async Task Cleanup_AfterPlacementRemovesNothing()
    {
        using var temp = new TemporaryCleanupArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("save.bin");
        await destination.Stream.WriteAsync(new byte[] { 5 });
        GoogleDriveDownloadPlacement.Place(destination);

        bool removed = GoogleDriveDownloadTemporaryFileCleanup.Remove(destination);

        Assert.False(removed);
        Assert.True(File.Exists(destination.FinalPath));
        Assert.Equal([5], File.ReadAllBytes(destination.FinalPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("save.bin")]
    [InlineData("save.bin.partial")]
    public void Cleanup_RefusesAnyPathThatIsNotOneOfItsTemporaryFiles(string name)
    {
        using var temp = new TemporaryCleanupArea();
        string candidate = string.IsNullOrWhiteSpace(name)
            ? name
            : temp.Path(name);
        if (!string.IsNullOrWhiteSpace(candidate))
            File.WriteAllBytes(candidate, [1]);

        bool removed = GoogleDriveDownloadTemporaryFileCleanup.Remove(
            candidate,
            temp.Path("final.bin"));

        Assert.False(removed);
        if (!string.IsNullOrWhiteSpace(candidate))
            Assert.True(File.Exists(candidate));
    }

    [Fact]
    public void Cleanup_RefusesToRemoveTheFinalPathEvenWithTheSuffix()
    {
        using var temp = new TemporaryCleanupArea();
        string path = temp.Path(
            $"save.bin{GoogleDriveLocalDownloadDestination.TemporarySuffix}");
        File.WriteAllBytes(path, [1]);

        bool removed = GoogleDriveDownloadTemporaryFileCleanup.Remove(path, path);

        Assert.False(removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LockedTemporaryFile_LeavesTheOriginalFailureAuthoritative()
    {
        using var temp = new TemporaryCleanupArea();
        GoogleDriveLocalDownloadDestination destination =
            await temp.PrepareAsync("locked.bin");
        destination.Stream.Dispose();

        // A handle that does not share delete access blocks removal on Windows.
        using var holder = new FileStream(
            destination.TemporaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        bool removed = GoogleDriveDownloadTemporaryFileCleanup.Remove(destination);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(removed);
            Assert.True(File.Exists(destination.TemporaryPath));
        }
    }

    [Fact]
    public void InvalidCleanupInput_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GoogleDriveDownloadTemporaryFileCleanup.Remove(null!));
    }

    [Fact]
    public void CleanupSource_DeletesOnlyItsOwnTemporaryFile()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveDownloadTemporaryFileCleanup.cs"));

        Assert.DoesNotContain("Directory.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFiles", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchOption", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("File.Delete", StringSplitOptions.None).Length - 1);
        Assert.Contains("TemporarySuffix", source, StringComparison.Ordinal);
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

    private sealed class TemporaryCleanupArea : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gamesaves-s9-{Guid.NewGuid():N}");

        public TemporaryCleanupArea() => Directory.CreateDirectory(_root);

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public Task<GoogleDriveLocalDownloadDestination> PrepareAsync(string name) =>
            new GoogleDriveLocalDownloadDestinationOpener().OpenAsync(Path(name));

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
