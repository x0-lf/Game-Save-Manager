using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveUploadSourceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EmptyInput_IsRejectedWithFixedCode(string value)
    {
        AssertFailure(
            () => GoogleDriveLocalUploadSourceValidator.Validate(value),
            GoogleDriveLocalUploadSourceFailure.InvalidPath,
            GoogleDriveLocalUploadSourceErrorCodes.InvalidPath);
    }

    [Fact]
    public void MissingInput_IsRejectedWithFixedCode()
    {
        using var temporary = new TemporaryDirectory();

        AssertFailure(
            () => GoogleDriveLocalUploadSourceValidator.Validate(
                temporary.GetPath("missing.bin")),
            GoogleDriveLocalUploadSourceFailure.NotFound,
            GoogleDriveLocalUploadSourceErrorCodes.NotFound);
    }

    [Fact]
    public void DirectoryInput_IsRejectedWithFixedCode()
    {
        using var temporary = new TemporaryDirectory();

        AssertFailure(
            () => GoogleDriveLocalUploadSourceValidator.Validate(temporary.Path),
            GoogleDriveLocalUploadSourceFailure.NotRegularFile,
            GoogleDriveLocalUploadSourceErrorCodes.NotRegularFile);
    }

    [Fact]
    public void ReparsePoint_IsRejectedWithFixedCode()
    {
        using var temporary = new TemporaryDirectory();
        string target = temporary.GetPath("target.bin");
        string link = temporary.GetPath("link.bin");
        File.WriteAllText(target, "synthetic");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            AssertFailure(
                () => GoogleDriveLocalUploadSourceValidator.ValidateAttributes(
                    FileAttributes.ReparsePoint),
                GoogleDriveLocalUploadSourceFailure.ReparsePoint,
                GoogleDriveLocalUploadSourceErrorCodes.ReparsePoint);
            return;
        }

        AssertFailure(
            () => GoogleDriveLocalUploadSourceValidator.Validate(link),
            GoogleDriveLocalUploadSourceFailure.ReparsePoint,
            GoogleDriveLocalUploadSourceErrorCodes.ReparsePoint);
    }

    [Fact]
    public void ExistingRegularFile_IsAccepted()
    {
        using var temporary = new TemporaryDirectory();
        string source = temporary.GetPath("source.bin");
        File.WriteAllText(source, "synthetic");

        GoogleDriveLocalUploadSourceValidator.Validate(source);
    }

    [Fact]
    public async Task OpenAsync_ReturnsReadOnlyStreamAndCapturedLength()
    {
        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("source.bin");
        byte[] contents = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, contents);

        using GoogleDriveLocalUploadSource source =
            await new GoogleDriveLocalUploadSourceOpener().OpenAsync(path);

        Assert.Equal(contents.Length, source.Length);
        Assert.True(source.Stream.CanRead);
        Assert.False(source.Stream.CanWrite);
        Assert.Equal(0, source.Stream.Position);
    }

    [Fact]
    public async Task OpenAsync_AcceptsZeroByteFile()
    {
        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("empty.bin");
        await File.WriteAllBytesAsync(path, []);

        using GoogleDriveLocalUploadSource source =
            await new GoogleDriveLocalUploadSourceOpener().OpenAsync(path);

        Assert.Equal(0, source.Length);
    }

    [Fact]
    public async Task OpenAsync_BlocksConcurrentWriteAndDeleteOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("locked.bin");
        await File.WriteAllTextAsync(path, "synthetic");
        using GoogleDriveLocalUploadSource source =
            await new GoogleDriveLocalUploadSourceOpener().OpenAsync(path);

        Exception? writeFailure = Record.Exception(
            () => new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read).Dispose());
        Exception? deleteFailure = Record.Exception(() => File.Delete(path));

        Assert.True(writeFailure is IOException or UnauthorizedAccessException);
        Assert.True(deleteFailure is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task OpenAsync_CapturesLengthOnce()
    {
        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("length.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        GoogleDriveLocalUploadSource source =
            await new GoogleDriveLocalUploadSourceOpener().OpenAsync(path);

        source.Dispose();
        await File.AppendAllTextAsync(path, "changed");

        Assert.Equal(3, source.Length);
    }

    [Fact]
    public async Task OpenAsync_PreCanceled_OpensNoHandle()
    {
        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("cancel.bin");
        await File.WriteAllTextAsync(path, "synthetic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GoogleDriveLocalUploadSourceOpener().OpenAsync(
                path,
                cancellation.Token));

        using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task OpenAsync_UnreadableFileUsesFixedPrivateError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temporary = new TemporaryDirectory();
        string marker = "private-source-marker.bin";
        string path = temporary.GetPath(marker);
        await File.WriteAllTextAsync(path, "synthetic");
        using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        GoogleDriveLocalUploadSourceException exception =
            await Assert.ThrowsAsync<GoogleDriveLocalUploadSourceException>(
                () => new GoogleDriveLocalUploadSourceOpener().OpenAsync(path));

        Assert.Equal(
            GoogleDriveLocalUploadSourceFailure.Unreadable,
            exception.Failure);
        Assert.Equal(
            GoogleDriveLocalUploadSourceErrorCodes.Unreadable,
            exception.SafeErrorCode);
        Assert.DoesNotContain(
            marker,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            marker,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Dispose_ReleasesHandleAndIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        string path = temporary.GetPath("dispose.bin");
        await File.WriteAllTextAsync(path, "synthetic");
        GoogleDriveLocalUploadSource source =
            await new GoogleDriveLocalUploadSourceOpener().OpenAsync(path);

        source.Dispose();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.Stream.ReadByte());
        using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public void SourceOpener_RemainsInternalAndDoesNoUploadWork()
    {
        Type[] sourceTypes =
        [
            typeof(GoogleDriveLocalUploadSource),
            typeof(GoogleDriveLocalUploadSourceOpener),
            typeof(GoogleDriveLocalUploadSourceValidator),
            typeof(GoogleDriveLocalUploadSourceException),
            typeof(GoogleDriveLocalUploadSourceFailure),
            typeof(GoogleDriveLocalUploadSourceErrorCodes)
        ];
        Assert.All(sourceTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
        });
        Assert.DoesNotContain(
            typeof(GoogleDriveLocalUploadSource).GetProperties(
                BindingFlags.Public | BindingFlags.Instance),
            property => property.Name.Contains("Path", StringComparison.Ordinal));

        string source = File.ReadAllText(Path.Combine(
            FindManagerRoot(),
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveLocalUploadSource.cs"));
        string[] forbidden =
        [
            "ReadAllBytes",
            "ReadAllText",
            "CopyTo",
            "HashData",
            "Google.Apis",
            "DriveService",
            "CreateMediaUpload",
            "MimeType",
            "ContentType"
        ];

        Assert.All(forbidden, value =>
            Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    }

    private static void AssertFailure(
        Action action,
        GoogleDriveLocalUploadSourceFailure expectedFailure,
        string expectedErrorCode)
    {
        GoogleDriveLocalUploadSourceException exception = Assert.Throws<
            GoogleDriveLocalUploadSourceException>(action);

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(expectedErrorCode, exception.SafeErrorCode);
        Assert.Null(exception.InnerException);
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
            "Could not locate Manager.sln by walking up from the test output directory.");
    }
}
