using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursiveObjectClassificationTests
{
    public static TheoryData<string?> MalformedMimeTypes => new()
    {
        null,
        string.Empty,
        " ",
        "application",
        "/octet-stream",
        "application/",
        "application//octet-stream",
        " application/octet-stream",
        "application/octet-stream ",
        "application/octet stream",
        "application/octet-stream; charset=utf-8",
        "application/保存",
        "application/\uD800"
    };

    [Fact]
    public void KindValues_AreExplicitAndStable()
    {
        Assert.Equal(0, (int)GoogleDriveRecursiveObjectKind.Folder);
        Assert.Equal(1, (int)GoogleDriveRecursiveObjectKind.BlobFile);
        Assert.Equal(2, (int)GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument);
        Assert.Equal(3, (int)GoogleDriveRecursiveObjectKind.Shortcut);
        Assert.Equal(4, (int)GoogleDriveRecursiveObjectKind.Unsupported);
    }

    [Fact]
    public void Folder_IsClassifiedAsFolder()
    {
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.Folder,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                "application/vnd.google-apps.folder"));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    [InlineData("application/x-game-save")]
    public void OrdinaryUploadedFiles_AreClassifiedAsBlobFiles(string mimeType)
    {
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.BlobFile,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType));
    }

    [Fact]
    public void Shortcut_IsClassifiedSeparatelyAndCannotBecomeAFileEntry()
    {
        const string mimeType = "application/vnd.google-apps.shortcut";

        Assert.Equal(
            GoogleDriveRecursiveObjectKind.Shortcut,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType));
        Assert.Throws<ArgumentException>(() => Entry(mimeType));
    }

    [Theory]
    [InlineData("application/vnd.google-apps.document")]
    [InlineData("application/vnd.google-apps.spreadsheet")]
    [InlineData("application/vnd.google-apps.presentation")]
    [InlineData("application/vnd.google-apps.form")]
    [InlineData("application/vnd.google-apps.drawing")]
    [InlineData("application/vnd.google-apps.site")]
    public void CommonWorkspaceObjects_AreClassifiedAsWorkspaceDocuments(
        string mimeType)
    {
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType));
        Assert.Throws<ArgumentException>(() => Entry(mimeType));
    }

    [Fact]
    public void UnknownGoogleAppsMimeType_IsStillAWorkspaceDocument()
    {
        const string mimeType = "application/vnd.google-apps.future-object";

        Assert.Equal(
            GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType));
    }

    [Theory]
    [MemberData(nameof(MalformedMimeTypes))]
    public void MissingOrMalformedMimeType_IsUnsupported(string? mimeType)
    {
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.Unsupported,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(mimeType));
    }

    [Fact]
    public void MimeTypeMatching_IsCaseInsensitive()
    {
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.Folder,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                "APPLICATION/VND.GOOGLE-APPS.FOLDER"));
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.Shortcut,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                "APPLICATION/VND.GOOGLE-APPS.SHORTCUT"));
        Assert.Equal(
            GoogleDriveRecursiveObjectKind.GoogleWorkspaceDocument,
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                "APPLICATION/VND.GOOGLE-APPS.DOCUMENT"));
    }

    [Fact]
    public void RecursiveFileEntry_AcceptsOnlyClassifiedBlobFiles()
    {
        GoogleDriveRecursiveFileEntry entry = Entry("application/octet-stream");

        Assert.Equal("application/octet-stream", entry.MimeType);
        Assert.Throws<ArgumentException>(() => Entry(
            "application/vnd.google-apps.folder"));
        Assert.Throws<ArgumentException>(() => Entry("not-a-mime-type"));
    }

    [Fact]
    public void SafeDiagnostics_RevealOnlyTheFixedKind()
    {
        const string privateMimeMarker = "application/x-private-backup-marker";
        GoogleDriveRecursiveObjectKind kind =
            GoogleDriveRecursiveObjectClassificationPolicy.Classify(
                privateMimeMarker);

        string diagnostic =
            GoogleDriveRecursiveObjectClassificationPolicy.ToSafeDiagnosticString(
                kind);

        Assert.Equal(
            "Google Drive recursive object classification: kind=BlobFile",
            diagnostic);
        Assert.DoesNotContain(
            privateMimeMarker,
            diagnostic,
            StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GoogleDriveRecursiveObjectClassificationPolicy.ToSafeDiagnosticString(
                (GoogleDriveRecursiveObjectKind)999));
    }

    [Fact]
    public void Policy_IsInfrastructureInternalAndExposesNoGoogleSdkTypes()
    {
        Type[] policyTypes =
        {
            typeof(GoogleDriveRecursiveObjectKind),
            typeof(GoogleDriveRecursiveObjectClassificationPolicy)
        };

        Assert.All(policyTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            AssertNoGoogleSdkType(type);
        });

        string[] names = policyTypes.Select(type => type.Name).ToArray();
        Assert.DoesNotContain(
            typeof(ISyncProvider).Assembly.GetTypes(),
            type => names.Contains(type.Name, StringComparer.Ordinal));
        Assert.DoesNotContain(
            typeof(SyncViewModel).Assembly.GetTypes(),
            type => names.Contains(type.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void ExistingObjectKindValues_RemainUnchanged()
    {
        Assert.Equal(0, (int)GoogleDriveObjectKind.File);
        Assert.Equal(1, (int)GoogleDriveObjectKind.Folder);
    }

    private static GoogleDriveRecursiveFileEntry Entry(string mimeType) => new(
        "file-id-marker",
        "parent-id-marker",
        "save.dat",
        "save.dat",
        mimeType);

    private static void AssertNoGoogleSdkType(Type type)
    {
        const BindingFlags members =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        IEnumerable<Type> exposedTypes = type.GetMethods(members)
            .Select(method => method.ReturnType)
            .Concat(type.GetMethods(members)
                .SelectMany(method => method.GetParameters())
                .Select(parameter => parameter.ParameterType));

        Assert.DoesNotContain(
            exposedTypes,
            exposed => exposed.Namespace?.StartsWith(
                "Google.",
                StringComparison.Ordinal) == true);
    }
}
