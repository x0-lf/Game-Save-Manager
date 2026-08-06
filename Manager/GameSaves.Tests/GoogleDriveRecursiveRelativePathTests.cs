using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Collections;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursiveRelativePathTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("f4645542-9859-4ba1-af9b-e0ea13717e5d");

    [Fact]
    public void DirectFile_IsRelativeToRunFolder()
    {
        GoogleDriveRecursiveFileListingRequest request =
            Request("run-2026-08-03");

        GoogleDriveRecursiveRelativePath root =
            GoogleDriveRecursiveRelativePath.Start(request);
        GoogleDriveRecursiveRelativePath file =
            root.AppendChild("manifest.json");

        Assert.True(root.IsRunFolderRoot);
        Assert.Equal(string.Empty, root.Canonical);
        Assert.Equal("manifest.json", file.Canonical);
        Assert.Equal(["manifest.json"], file.Segments);
        Assert.DoesNotContain(
            request.CanonicalFolderPath,
            file.Canonical,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NestedFile_AppendsOneSegmentAtATimeWithForwardSlashes()
    {
        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(
                    Request("run-2026-08-03"))
                .AppendChild("files")
                .AppendChild("C")
                .AppendChild("Users")
                .AppendChild("Test")
                .AppendChild("save.dat");

        Assert.Equal("files/C/Users/Test/save.dat", path.Canonical);
        Assert.Equal(
            ["files", "C", "Users", "Test", "save.dat"],
            path.Segments);
        Assert.Equal(5, path.Depth);
        Assert.Equal(5, path.SegmentCount);
        Assert.False(path.Canonical.StartsWith('/'));
        Assert.False(path.Canonical.EndsWith('/'));
        Assert.DoesNotContain("//", path.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedRequestedRunFolder_IsStillExcludedFromResult()
    {
        GoogleDriveRecursiveFileListingRequest request =
            Request("archive/season-one/run-001");

        GoogleDriveRecursiveRelativePath file =
            GoogleDriveRecursiveRelativePath.Start(request)
                .AppendChild("slot")
                .AppendChild("save.dat");

        Assert.Equal("slot/save.dat", file.Canonical);
        Assert.DoesNotContain("archive", file.Canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("season-one", file.Canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("run-001", file.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeAndOrdinalCase_ArePreservedExactly()
    {
        const string first = "Pokémon-保存-🎮";
        const string second = "SaveFILE.DAT";

        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"))
                .AppendChild(first)
                .AppendChild(second);

        Assert.Equal($"{first}/{second}", path.Canonical);
        Assert.Equal(first, path.Segments[0]);
        Assert.Equal(second, path.Segments[1]);
    }

    [Fact]
    public void LongChildName_IsNotRestrictedByOperatingSystemRules()
    {
        string longName = new('x', 4096);

        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"))
                .AppendChild(longName);

        Assert.Equal(longName, path.Canonical);
        Assert.Single(path.Segments);
    }

    [Fact]
    public void Apostrophes_ArePreservedExactly()
    {
        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"))
                .AppendChild("Player's Saves")
                .AppendChild("Slot 'A'.dat");

        Assert.Equal("Player's Saves/Slot 'A'.dat", path.Canonical);
    }

    [Fact]
    public void Backslashes_AreOrdinaryNameCharactersNotSeparators()
    {
        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"))
                .AppendChild(@"files\C")
                .AppendChild(@"save\slot.dat");

        Assert.Equal(@"files\C/save\slot.dat", path.Canonical);
        Assert.Equal(2, path.SegmentCount);
        Assert.Equal(@"files\C", path.Segments[0]);
        Assert.Equal(@"save\slot.dat", path.Segments[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("nested/name")]
    [InlineData("nested//name")]
    public void InvalidChildSegments_AreRejectedWithoutChangingPrefix(
        string? childName)
    {
        GoogleDriveRecursiveRelativePath prefix =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"))
                .AppendChild("files");

        Assert.Throws<ArgumentException>(() =>
            prefix.AppendChild(childName!));

        Assert.Equal("files", prefix.Canonical);
        Assert.Equal(1, prefix.SegmentCount);
    }

    [Fact]
    public void InvalidChildException_DoesNotEchoRejectedName()
    {
        const string privateInvalidName = "private-name/secret-object";
        GoogleDriveRecursiveRelativePath root =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            root.AppendChild(privateInvalidName));

        Assert.DoesNotContain(
            privateInvalidName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\0')]
    [InlineData('\u0001')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\u007F')]
    public void ControlCharacters_AreRejected(char control)
    {
        GoogleDriveRecursiveRelativePath root =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"));

        Assert.Throws<ArgumentException>(() =>
            root.AppendChild($"save{control}.dat"));
    }

    [Fact]
    public void MalformedUnicode_IsRejectedButValidSurrogatePairsArePreserved()
    {
        string valid = "save-🎮.dat";
        string unpairedHigh = "save-" + '\uD800';
        string unpairedLow = "save-" + '\uDC00';
        GoogleDriveRecursiveRelativePath root =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"));

        Assert.Equal(valid, root.AppendChild(valid).Canonical);
        Assert.Throws<ArgumentException>(() => root.AppendChild(unpairedHigh));
        Assert.Throws<ArgumentException>(() => root.AppendChild(unpairedLow));
    }

    [Fact]
    public void Appending_IsImmutableAndEqualityIsOrdinal()
    {
        GoogleDriveRecursiveRelativePath root =
            GoogleDriveRecursiveRelativePath.Start(Request("run-001"));
        GoogleDriveRecursiveRelativePath first = root.AppendChild("Save.dat");
        GoogleDriveRecursiveRelativePath same = root.AppendChild("Save.dat");
        GoogleDriveRecursiveRelativePath differentCase =
            root.AppendChild("save.dat");

        Assert.Equal(string.Empty, root.Canonical);
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, differentCase);
        IList segments = Assert.IsAssignableFrom<IList>(first.Segments);
        Assert.True(segments.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => segments[0] = "changed");
    }

    [Fact]
    public void SafeDiagnostics_ExposeOnlyDepthAndSegmentCount()
    {
        const string privateFolder = "Personal Folder";
        const string privateFile = "Secret Save.dat";
        GoogleDriveRecursiveRelativePath path =
            GoogleDriveRecursiveRelativePath.Start(
                    Request("Private Run Name"))
                .AppendChild(privateFolder)
                .AppendChild(privateFile);

        string diagnostic = path.ToSafeDiagnosticString();

        Assert.Equal(
            "Google Drive recursive relative path (depth=2; segments=2)",
            diagnostic);
        Assert.Equal(diagnostic, path.ToString());
        Assert.DoesNotContain("Private Run Name", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(privateFolder, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(privateFile, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(path.Canonical, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void NullRequest_IsRejectedBeforeComposition()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GoogleDriveRecursiveRelativePath.Start(null!));
    }

    [Fact]
    public void Helper_IsInfrastructureInternalImmutableAndSdkFree()
    {
        Type helperType = typeof(GoogleDriveRecursiveRelativePath);

        Assert.False(helperType.IsPublic || helperType.IsNestedPublic);
        Assert.True(helperType.IsSealed);
        Assert.Equal("GameSaves.Infrastructure.GoogleDrive", helperType.Namespace);
        Assert.All(
            helperType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite));
        AssertNoGoogleSdkType(helperType);

        Assert.DoesNotContain(
            typeof(ISyncProvider).Assembly.GetTypes(),
            type => type.Name == helperType.Name);
        Assert.DoesNotContain(
            typeof(SyncViewModel).Assembly.GetTypes(),
            type => type.Name == helperType.Name);
    }

    [Fact]
    public void Source_UsesNoOperatingSystemPathApi()
    {
        string managerRoot = FindManagerRoot();
        string source = File.ReadAllText(Path.Combine(
            managerRoot,
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveRecursiveRelativePath.cs"));

        Assert.DoesNotContain("Path.Combine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AltDirectorySeparatorChar", source, StringComparison.Ordinal);
    }

    private static GoogleDriveRecursiveFileListingRequest Request(string path) =>
        GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, path);

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
