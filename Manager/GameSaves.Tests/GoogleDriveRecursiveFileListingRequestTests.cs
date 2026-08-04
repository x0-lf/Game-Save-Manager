using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRecursiveFileListingRequestTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("4ca361fa-6ded-4beb-bbb8-a931e501ae80");

    [Fact]
    public void OneSegmentRunFolder_IsPreservedExactly()
    {
        var request = GoogleDriveRecursiveFileListingRequest.Parse(
            ProfileId,
            "run-2026-08-04");

        Assert.Equal(ProfileId, request.RemoteProfileId);
        Assert.Equal("run-2026-08-04", request.CanonicalFolderPath);
        Assert.Equal(["run-2026-08-04"], request.FolderPath.Segments);
    }

    [Fact]
    public void NestedFolderPath_IsPreservedForProviderNeutralCompatibility()
    {
        var request = GoogleDriveRecursiveFileListingRequest.Parse(
            ProfileId,
            "archive/run-2026-08-04");

        Assert.Equal("archive/run-2026-08-04", request.CanonicalFolderPath);
        Assert.Equal(
            ["archive", "run-2026-08-04"],
            request.FolderPath.Segments);
    }

    [Theory]
    [InlineData("Pokémon/保存データ")]
    [InlineData("Player's Saves/Run 'A'")]
    [InlineData(@"folder\name/run\name")]
    [InlineData("folder:name/file*name?/trailing.")]
    public void ValidDriveNameCharacters_ArePreservedExactly(string value)
    {
        var request = GoogleDriveRecursiveFileListingRequest.Parse(
            ProfileId,
            value);

        Assert.Equal(value, request.CanonicalFolderPath);
    }

    [Fact]
    public void Backslash_IsAnOrdinaryNameCharacterNotASeparator()
    {
        var request = GoogleDriveRecursiveFileListingRequest.Parse(
            ProfileId,
            @"run\nested");

        Assert.Single(request.FolderPath.Segments);
        Assert.Equal(@"run\nested", request.FolderPath.Segments[0]);
        Assert.Equal(@"run\nested", request.CanonicalFolderPath);
    }

    [Fact]
    public void EmptyProfileId_IsRejectedWithoutEchoingPath()
    {
        const string privatePath = "private-run-name";

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(
                Guid.Empty,
                privatePath));

        Assert.DoesNotContain(privatePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullAndRootPaths_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GoogleDriveRecursiveFileListingRequest(ProfileId, null!));
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveRecursiveFileListingRequest(
                ProfileId,
                GoogleDriveRelativePath.Root));
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, string.Empty));
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, null!));
    }

    [Theory]
    [InlineData("/run")]
    [InlineData("run/")]
    [InlineData("run//child")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("run/./child")]
    [InlineData("run/../child")]
    public void StructurallyInvalidPaths_AreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, value));
    }

    [Fact]
    public void RejectedTraversalPath_IsNotEchoed()
    {
        const string rejectedPath = "private/../object-id-marker";

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(
                ProfileId,
                rejectedPath));

        Assert.DoesNotContain(
            rejectedPath,
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
        string value = $"run{control}name";

        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, value));
    }

    [Fact]
    public void MalformedSurrogatePairs_AreRejectedButValidPairsArePreserved()
    {
        const string valid = "Runs/\U0001F3AE";
        string unpairedHigh = "Runs/" + '\uD800';
        string unpairedLow = "Runs/" + '\uDC00';

        Assert.Equal(
            valid,
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, valid)
                .CanonicalFolderPath);
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, unpairedHigh));
        Assert.Throws<ArgumentException>(() =>
            GoogleDriveRecursiveFileListingRequest.Parse(ProfileId, unpairedLow));
    }

    [Fact]
    public void SafeFormatting_RevealsOnlySegmentCount()
    {
        const string privatePath = "Personal Folder/Secret Run";
        var request = GoogleDriveRecursiveFileListingRequest.Parse(
            ProfileId,
            privatePath);

        string diagnostic = request.ToSafeDiagnosticString();

        Assert.Equal(
            "Google Drive recursive file listing request (segments=2)",
            diagnostic);
        Assert.Equal(diagnostic, request.ToString());
        Assert.DoesNotContain(privatePath, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Folder", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Run", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileId.ToString(), diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_IsInfrastructureInternalImmutableAndSdkFree()
    {
        Type requestType = typeof(GoogleDriveRecursiveFileListingRequest);

        Assert.False(requestType.IsPublic || requestType.IsNestedPublic);
        Assert.True(requestType.IsSealed);
        Assert.Equal("GameSaves.Infrastructure.GoogleDrive", requestType.Namespace);
        Assert.All(
            requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.False(property.CanWrite));
        AssertNoGoogleSdkType(requestType);

        Assert.DoesNotContain(
            typeof(ISyncProvider).Assembly.GetTypes(),
            type => type.Name == requestType.Name);
        Assert.DoesNotContain(
            typeof(SyncViewModel).Assembly.GetTypes(),
            type => type.Name == requestType.Name);
    }

    [Fact]
    public void Source_DoesNotUseOperatingSystemPathRules()
    {
        string managerRoot = FindManagerRoot();
        string source = File.ReadAllText(Path.Combine(
            managerRoot,
            "GameSaves.Infrastructure",
            "GoogleDrive",
            "GoogleDriveRecursiveFileListingRequest.cs"));

        Assert.DoesNotContain("Path.Combine", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AltDirectorySeparatorChar", source, StringComparison.Ordinal);
    }

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
