using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Collections;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursiveFileListingContractTests
{
    [Fact]
    public void StatusValues_AreStable()
    {
        Assert.Equal(0, (int)GoogleDriveRecursiveFileListingStatus.Completed);
        Assert.Equal(1, (int)GoogleDriveRecursiveFileListingStatus.FolderNotFound);
        Assert.Equal(2, (int)GoogleDriveRecursiveFileListingStatus.InvalidPath);
        Assert.Equal(3, (int)GoogleDriveRecursiveFileListingStatus.Ambiguous);
        Assert.Equal(4, (int)GoogleDriveRecursiveFileListingStatus.CaseCollision);
        Assert.Equal(5, (int)GoogleDriveRecursiveFileListingStatus.TypeCollision);
        Assert.Equal(6, (int)GoogleDriveRecursiveFileListingStatus.UnsupportedObject);
        Assert.Equal(7, (int)GoogleDriveRecursiveFileListingStatus.TrashedObject);
        Assert.Equal(8, (int)GoogleDriveRecursiveFileListingStatus.UnsupportedLocation);
        Assert.Equal(9, (int)GoogleDriveRecursiveFileListingStatus.InvalidMetadata);
        Assert.Equal(10, (int)GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired);
        Assert.Equal(11, (int)GoogleDriveRecursiveFileListingStatus.AccessDenied);
        Assert.Equal(12, (int)GoogleDriveRecursiveFileListingStatus.RateLimited);
        Assert.Equal(13, (int)GoogleDriveRecursiveFileListingStatus.QuotaExceeded);
        Assert.Equal(14, (int)GoogleDriveRecursiveFileListingStatus.Unavailable);
        Assert.Equal(15, (int)GoogleDriveRecursiveFileListingStatus.Cancelled);
        Assert.Equal(16, (int)GoogleDriveRecursiveFileListingStatus.Failed);
    }

    [Theory]
    [InlineData(GoogleDriveRecursiveFileListingStatus.InvalidPath, "GoogleDriveFileListingInvalidPath")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.FolderNotFound, "GoogleDriveFileListingFolderNotFound")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.Ambiguous, "GoogleDriveFileListingAmbiguous")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.CaseCollision, "GoogleDriveFileListingCaseCollision")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.TypeCollision, "GoogleDriveFileListingTypeCollision")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.UnsupportedObject, "GoogleDriveFileListingUnsupportedObject")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.TrashedObject, "GoogleDriveFileListingTrashed")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.UnsupportedLocation, "GoogleDriveFileListingUnsupportedLocation")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.InvalidMetadata, "GoogleDriveFileListingInvalidMetadata")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.ReauthenticationRequired, "GoogleDriveFileListingAuthenticationRequired")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.AccessDenied, "GoogleDriveFileListingAccessDenied")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.RateLimited, "GoogleDriveFileListingRateLimited")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.QuotaExceeded, "GoogleDriveFileListingQuotaExceeded")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.Unavailable, "GoogleDriveFileListingUnavailable")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.Cancelled, "GoogleDriveFileListingCancelled")]
    [InlineData(GoogleDriveRecursiveFileListingStatus.Failed, "GoogleDriveFileListingFailed")]
    public void ErrorCodes_AreStableAndMappedToStatus(
        object statusValue,
        string expected)
    {
        GoogleDriveRecursiveFileListingStatus status = Assert.IsType<
            GoogleDriveRecursiveFileListingStatus>(statusValue);

        Assert.Equal(
            expected,
            GoogleDriveRecursiveFileListingErrorCodes.ForStatus(status));
    }

    [Fact]
    public void Entry_PreservesTrustedIdentityAndCanonicalPath()
    {
        var entry = Entry();

        Assert.Equal("authoritative-file-id-marker", entry.FileId);
        Assert.Equal("authoritative-parent-id-marker", entry.ParentFolderId);
        Assert.Equal("save.dat", entry.ExactFileName);
        Assert.Equal("files/C/Player/save.dat", entry.CanonicalRelativePath);
        Assert.Equal("application/octet-stream", entry.MimeType);
    }

    [Fact]
    public void SafeFormatting_OmitsIdentityNamesPathsMimeAndMessages()
    {
        const string fileId = "authoritative-file-id-marker";
        const string parentId = "authoritative-parent-id-marker";
        const string name = "private-save-name.dat";
        const string path = "private/folder/private-save-name.dat";
        const string mimeType = "application/private-marker";
        const string message = "Unsafe detail with authoritative-file-id-marker";
        var entry = new GoogleDriveRecursiveFileEntry(
            fileId,
            parentId,
            name,
            path,
            mimeType);
        var result = new GoogleDriveRecursiveFileListingResult(
            GoogleDriveRecursiveFileListingStatus.Failed,
            Array.Empty<GoogleDriveRecursiveFileEntry>(),
            retryable: false,
            GoogleDriveRecursiveFileListingErrorCodes.Failed,
            message);

        foreach (string sensitiveValue in new[]
                 {
                     fileId, parentId, name, path, mimeType, message
                 })
        {
            Assert.DoesNotContain(
                sensitiveValue,
                entry.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                sensitiveValue,
                result.ToString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(result.ToString(), result.ToSafeDiagnosticString());
    }

    [Fact]
    public void Result_DefensivelyCopiesEntriesAndExposesReadOnlyCollection()
    {
        GoogleDriveRecursiveFileEntry original = Entry();
        var source = new List<GoogleDriveRecursiveFileEntry> { original };
        var result = new GoogleDriveRecursiveFileListingResult(
            GoogleDriveRecursiveFileListingStatus.Completed,
            source,
            retryable: false);

        source.Clear();

        Assert.Same(original, Assert.Single(result.Entries));
        IList collection = Assert.IsAssignableFrom<IList>(result.Entries);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
    }

    [Fact]
    public void InvalidEntryConstruction_FailsWithoutEchoingSensitiveValues()
    {
        const string rejectedPath = "private/../authoritative-file-id-marker";

        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            " ", "parent", "save.dat", "save.dat", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            "file", " ", "save.dat", "save.dat", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            "file", "parent", "", "save.dat", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            "file", "parent", "save.dat", "save.dat", " "));
        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            "file", "parent", "save.dat", "other.dat", "application/octet-stream"));
        Assert.Throws<ArgumentException>(() => new GoogleDriveRecursiveFileEntry(
            "file",
            "parent",
            "save.dat",
            "save.dat",
            "application/vnd.google-apps.folder"));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileEntry(
                "file",
                "parent",
                "save.dat",
                rejectedPath,
                "application/octet-stream"));
        Assert.DoesNotContain(rejectedPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidResultConstruction_RejectsInconsistentOrPartialResults()
    {
        GoogleDriveRecursiveFileEntry entry = Entry();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                (GoogleDriveRecursiveFileListingStatus)999,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                false));
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                GoogleDriveRecursiveFileListingStatus.Completed,
                null!,
                false));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                GoogleDriveRecursiveFileListingStatus.Completed,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                true));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                GoogleDriveRecursiveFileListingStatus.Failed,
                new[] { entry },
                false,
                GoogleDriveRecursiveFileListingErrorCodes.Failed,
                "Safe failure."));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                GoogleDriveRecursiveFileListingStatus.Failed,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                false,
                GoogleDriveRecursiveFileListingErrorCodes.Unavailable,
                "Safe failure."));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileListingResult(
                GoogleDriveRecursiveFileListingStatus.Failed,
                Array.Empty<GoogleDriveRecursiveFileEntry>(),
                false,
                GoogleDriveRecursiveFileListingErrorCodes.Failed,
                " "));
    }

    [Fact]
    public void Contracts_AreInfrastructureInternalAndExposeNoGoogleSdkTypes()
    {
        Type[] contractTypes =
        {
            typeof(GoogleDriveRecursiveFileListingStatus),
            typeof(GoogleDriveRecursiveFileListingErrorCodes),
            typeof(GoogleDriveRecursiveFileEntry),
            typeof(GoogleDriveRecursiveFileListingResult)
        };

        Assert.All(contractTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            AssertNoGoogleSdkType(type);
        });

        string[] contractNames = contractTypes.Select(type => type.Name).ToArray();
        Assert.DoesNotContain(
            typeof(ISyncProvider).Assembly.GetTypes(),
            type => contractNames.Contains(type.Name, StringComparer.Ordinal));
        Assert.DoesNotContain(
            typeof(SyncViewModel).Assembly.GetTypes(),
            type => contractNames.Contains(type.Name, StringComparer.Ordinal));
    }

    private static GoogleDriveRecursiveFileEntry Entry() => new(
        "authoritative-file-id-marker",
        "authoritative-parent-id-marker",
        "save.dat",
        "files/C/Player/save.dat",
        "application/octet-stream");

    private static void AssertNoGoogleSdkType(Type type)
    {
        const BindingFlags members =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        IEnumerable<Type> exposedTypes = type.GetConstructors(members)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(type.GetProperties(members).Select(property => property.PropertyType))
            .Concat(type.GetMethods(members).Select(method => method.ReturnType));

        Assert.DoesNotContain(
            exposedTypes,
            exposed => exposed.Namespace?.StartsWith(
                "Google.",
                StringComparison.Ordinal) == true);
    }
}
