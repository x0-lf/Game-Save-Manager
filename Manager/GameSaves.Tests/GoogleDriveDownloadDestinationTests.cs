using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveDownloadDestinationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyDestination_IsRejectedWithFixedCode(string path)
    {
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => opener.OpenAsync(path));

        Assert.Equal(
            "GoogleDriveDownloadInvalidDestinationPath",
            exception.SafeErrorCode);
    }

    [Fact]
    public async Task ExistingFile_IsRefusedAndLeftUntouched()
    {
        using var temp = new TemporaryDownloadDirectory();
        string existing = temp.Path("save.bin");
        File.WriteAllBytes(existing, [1, 2, 3]);
        DateTime written = File.GetLastWriteTimeUtc(existing);
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => opener.OpenAsync(existing));

        Assert.Equal("GoogleDriveDownloadDestinationExists", exception.SafeErrorCode);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(existing));
        Assert.Equal(written, File.GetLastWriteTimeUtc(existing));
        Assert.Empty(temp.TemporaryFiles());
    }

    [Fact]
    public async Task ExistingDirectory_IsRefusedAndLeftUntouched()
    {
        using var temp = new TemporaryDownloadDirectory();
        string existing = temp.Path("occupied");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => opener.OpenAsync(existing));

        Assert.Equal("GoogleDriveDownloadDestinationExists", exception.SafeErrorCode);
        Assert.True(Directory.Exists(existing));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public async Task MissingDirectory_IsCreatedAndOnlyTheTemporaryFileAppears()
    {
        using var temp = new TemporaryDownloadDirectory();
        string finalPath = temp.Path("nested", "deep", "save.bin");
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        using GoogleDriveLocalDownloadDestination destination =
            await opener.OpenAsync(finalPath);

        Assert.Equal(finalPath, destination.FinalPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(finalPath)));
        Assert.False(File.Exists(finalPath));
        Assert.True(File.Exists(destination.TemporaryPath));
        Assert.Equal(
            Path.GetDirectoryName(finalPath),
            Path.GetDirectoryName(destination.TemporaryPath));
        Assert.EndsWith(
            GoogleDriveLocalDownloadDestination.TemporarySuffix,
            destination.TemporaryPath,
            StringComparison.Ordinal);
        Assert.NotEqual(finalPath, destination.TemporaryPath);
        Assert.StartsWith(
            "save.bin.",
            Path.GetFileName(destination.TemporaryPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentPreparations_UseDistinctTemporaryFiles()
    {
        using var temp = new TemporaryDownloadDirectory();
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        using GoogleDriveLocalDownloadDestination first =
            await opener.OpenAsync(temp.Path("save.bin"));
        using GoogleDriveLocalDownloadDestination second =
            await opener.OpenAsync(temp.Path("save.bin"));

        Assert.NotEqual(first.TemporaryPath, second.TemporaryPath);
        Assert.Equal(first.FinalPath, second.FinalPath);
        Assert.Equal(2, temp.TemporaryFiles().Length);
        Assert.False(File.Exists(first.FinalPath));
    }

    [Fact]
    public async Task TemporaryStream_IsWriteOnlyAndExclusive()
    {
        using var temp = new TemporaryDownloadDirectory();
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        using GoogleDriveLocalDownloadDestination destination =
            await opener.OpenAsync(temp.Path("save.bin"));

        Assert.True(destination.Stream.CanWrite);
        Assert.False(destination.Stream.CanRead);
        Assert.Equal(0, destination.Stream.Length);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => new FileStream(
                destination.TemporaryPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None));
        }

        await destination.Stream.WriteAsync(new byte[] { 7, 7 });
        await destination.Stream.FlushAsync();
        destination.Dispose();

        Assert.Equal([7, 7], File.ReadAllBytes(destination.TemporaryPath));
        Assert.False(File.Exists(destination.FinalPath));
    }

    [Fact]
    public async Task PreCanceledPreparation_CreatesNothing()
    {
        using var temp = new TemporaryDownloadDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            opener.OpenAsync(temp.Path("nested", "save.bin"), cancellation.Token));

        Assert.Empty(temp.TemporaryFiles());
        Assert.False(Directory.Exists(temp.Path("nested")));
    }

    [Fact]
    public void Dispose_ReleasesTheHandleAndIsIdempotent()
    {
        using var temp = new TemporaryDownloadDirectory();
        string finalPath = temp.Path("save.bin");
        string temporaryPath =
            GoogleDriveLocalDownloadDestinationOpener.TemporaryPathFor(finalPath);
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        var destination = new GoogleDriveLocalDownloadDestination(
            finalPath,
            temporaryPath,
            stream);

        destination.Dispose();
        destination.Dispose();

        Assert.False(stream.CanWrite);
        Assert.True(File.Exists(temporaryPath));
    }

    [Fact]
    public void InvalidDestinationConstruction_IsRejected()
    {
        using var temp = new TemporaryDownloadDirectory();
        string finalPath = temp.Path("save.bin");
        using var readable = new FileStream(
            temp.Path("readable.bin"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveLocalDownloadDestination("  ", "temp", readable));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveLocalDownloadDestination(finalPath, "  ", readable));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveLocalDownloadDestination(finalPath, finalPath, readable));
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveLocalDownloadDestination(finalPath, "temp", null!));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveLocalDownloadDestination(
                finalPath,
                temp.Path("other.bin"),
                readable));
    }

    [Fact]
    public void FailureTaxonomy_IsStableDistinctAndFreeOfPrivateValues()
    {
        const string privatePath = @"C:\Users\Someone\Saves\Personal Save.bin";
        GoogleDriveLocalDownloadDestinationFailure[] failures =
            Enum.GetValues<GoogleDriveLocalDownloadDestinationFailure>();
        string[] codes = failures
            .Select(GoogleDriveLocalDownloadDestinationErrorCodes.ForFailure)
            .ToArray();

        Assert.Equal(
            [
                "GoogleDriveDownloadInvalidDestinationPath",
                "GoogleDriveDownloadDestinationExists",
                "GoogleDriveDownloadDestinationDirectoryUnavailable",
                "GoogleDriveDownloadDestinationUnwritable",
                "GoogleDriveDownloadDestinationFailed"
            ],
            codes);
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(failures, failure =>
        {
            var exception = new GoogleDriveLocalDownloadDestinationException(failure);
            string formatted = string.Join(
                Environment.NewLine,
                exception.Message,
                exception.ToString());

            Assert.DoesNotContain(privatePath, formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Personal Save.bin", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain(".gsdownload", formatted, StringComparison.Ordinal);
            Assert.Null(exception.InnerException);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveLocalDownloadDestinationErrorCodes.ForFailure(
                (GoogleDriveLocalDownloadDestinationFailure)int.MaxValue));
    }

    [Fact]
    public async Task RefusalMessages_NeverContainTheDestinationPath()
    {
        using var temp = new TemporaryDownloadDirectory();
        string existing = temp.Path("Personal Save.bin");
        File.WriteAllBytes(existing, [1]);
        var opener = new GoogleDriveLocalDownloadDestinationOpener();

        GoogleDriveLocalDownloadDestinationException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalDownloadDestinationException>(
                () => opener.OpenAsync(existing));

        string formatted = string.Join(
            Environment.NewLine,
            exception.Message,
            exception.ToString());
        Assert.DoesNotContain(existing, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Personal Save.bin", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestinationTypes_AreInfrastructureInternalAndSdkFree()
    {
        Type[] types =
        [
            typeof(GoogleDriveLocalDownloadDestination),
            typeof(GoogleDriveLocalDownloadDestinationOpener),
            typeof(GoogleDriveLocalDownloadDestinationException),
            typeof(GoogleDriveLocalDownloadDestinationFailure),
            typeof(GoogleDriveLocalDownloadDestinationErrorCodes)
        ];

        Assert.All(types, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            Assert.DoesNotContain(
                type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static)
                    .Select(method => method.ReturnType),
                returnType => returnType.Namespace?.StartsWith(
                    "Google.",
                    StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public void DestinationSource_NeverDeletesOrOverwrites()
    {
        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveLocalDownloadDestination.cs"));

        Assert.DoesNotContain("File.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Move", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Replace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Truncate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.OpenOrCreate", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
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

    private sealed class TemporaryDownloadDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"gamesaves-s2-{Guid.NewGuid():N}");

        public TemporaryDownloadDirectory() => Directory.CreateDirectory(_root);

        public string Path(params string[] segments) =>
            System.IO.Path.Combine([_root, .. segments]);

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
