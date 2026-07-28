using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using System.Collections;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDrivePathContractTests
{
    [Fact]
    public void EmptyPath_RepresentsApplicationRoot()
    {
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse(string.Empty);

        Assert.Same(GoogleDriveRelativePath.Root, path);
        Assert.True(path.IsRoot);
        Assert.Empty(path.Segments);
        Assert.Equal(string.Empty, path.Canonical);
        Assert.DoesNotContain("id", path.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneSegment_IsPreservedExactly()
    {
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse("Backup Run");

        Assert.False(path.IsRoot);
        Assert.Equal(new[] { "Backup Run" }, path.Segments);
        Assert.Equal("Backup Run", path.Canonical);
    }

    [Fact]
    public void NestedSegments_UseOnlyForwardSlashAsSeparator()
    {
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse(
            "game/run-2026/manifest.json");

        Assert.Equal(
            new[] { "game", "run-2026", "manifest.json" },
            path.Segments);
        Assert.Equal("game/run-2026/manifest.json", path.Canonical);
    }

    [Theory]
    [InlineData("Pokémon/保存データ/Żółć")]
    [InlineData("Player's Saves/Run 'A'")]
    [InlineData(@"folder\name/file\name")]
    [InlineData("folder:name/file*name?/trailing.")]
    public void ValidDriveNameCharacters_ArePreservedWithoutFilesystemRules(
        string value)
    {
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse(value);

        Assert.Equal(value, path.Canonical);
    }

    [Fact]
    public void Backslash_IsANameCharacterAndNotASeparator()
    {
        GoogleDriveRelativePath path = GoogleDriveRelativePath.Parse(
            @"folder\child");

        Assert.Single(path.Segments);
        Assert.Equal(@"folder\child", path.Segments[0]);
    }

    [Theory]
    [InlineData("/folder")]
    [InlineData("folder/")]
    [InlineData("folder//child")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/./child")]
    [InlineData("folder/../child")]
    public void StructurallyInvalidPaths_AreRejected(string value)
    {
        Assert.False(GoogleDriveRelativePath.TryParse(value, out _));
        Assert.Throws<ArgumentException>(() => GoogleDriveRelativePath.Parse(value));
    }

    [Fact]
    public void NullPath_IsRejectedButEmptyPathIsValid()
    {
        Assert.False(GoogleDriveRelativePath.TryParse(null, out _));
        Assert.True(GoogleDriveRelativePath.TryParse(string.Empty, out _));
    }

    [Theory]
    [InlineData('\0')]
    [InlineData('\u0001')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\u007F')]
    public void ControlCharacters_AreRejected(char control)
    {
        string value = $"folder{control}name";

        Assert.False(GoogleDriveRelativePath.TryParse(value, out _));
    }

    [Fact]
    public void MalformedUtf16_IsRejectedWithoutRejectingValidSurrogatePairs()
    {
        const string validSupplementaryCharacter = "Saves/\U0001F3AE";
        string unpairedHighSurrogate = "Saves/" + '\uD800';
        string unpairedLowSurrogate = "Saves/" + '\uDC00';

        Assert.True(GoogleDriveRelativePath.TryParse(
            validSupplementaryCharacter,
            out GoogleDriveRelativePath? valid));
        Assert.Equal(validSupplementaryCharacter, valid!.Canonical);
        Assert.False(GoogleDriveRelativePath.TryParse(unpairedHighSurrogate, out _));
        Assert.False(GoogleDriveRelativePath.TryParse(unpairedLowSurrogate, out _));
    }

    [Fact]
    public void InvalidPathException_DoesNotEchoRejectedPath()
    {
        const string rejectedPath = "private/../object-id-marker";

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            GoogleDriveRelativePath.Parse(rejectedPath));

        Assert.DoesNotContain(rejectedPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Segments_AreExposedAsReadOnlyAndInputEqualityIsOrdinal()
    {
        GoogleDriveRelativePath first = GoogleDriveRelativePath.Parse("Résumé/Run");
        GoogleDriveRelativePath same = GoogleDriveRelativePath.Parse("Résumé/Run");
        GoogleDriveRelativePath differentCase = GoogleDriveRelativePath.Parse("résumé/Run");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentCase);
        IList nonGeneric = Assert.IsAssignableFrom<IList>(first.Segments);
        Assert.True(nonGeneric.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => nonGeneric[0] = "changed");
    }

    [Fact]
    public void ObjectKind_HasStableValues()
    {
        Assert.Equal(0, (int)GoogleDriveObjectKind.File);
        Assert.Equal(1, (int)GoogleDriveObjectKind.Folder);
    }

    [Fact]
    public void ResolutionStatus_HasStableValues()
    {
        Assert.Equal(0, (int)GoogleDriveObjectResolutionStatus.Found);
        Assert.Equal(1, (int)GoogleDriveObjectResolutionStatus.Created);
        Assert.Equal(2, (int)GoogleDriveObjectResolutionStatus.NotFound);
        Assert.Equal(3, (int)GoogleDriveObjectResolutionStatus.InvalidPath);
        Assert.Equal(4, (int)GoogleDriveObjectResolutionStatus.Ambiguous);
        Assert.Equal(5, (int)GoogleDriveObjectResolutionStatus.TypeMismatch);
        Assert.Equal(6, (int)GoogleDriveObjectResolutionStatus.Trashed);
        Assert.Equal(7, (int)GoogleDriveObjectResolutionStatus.UnsupportedLocation);
        Assert.Equal(8, (int)GoogleDriveObjectResolutionStatus.ReauthenticationRequired);
        Assert.Equal(9, (int)GoogleDriveObjectResolutionStatus.AccessDenied);
        Assert.Equal(10, (int)GoogleDriveObjectResolutionStatus.RateLimited);
        Assert.Equal(11, (int)GoogleDriveObjectResolutionStatus.QuotaExceeded);
        Assert.Equal(12, (int)GoogleDriveObjectResolutionStatus.Unavailable);
        Assert.Equal(13, (int)GoogleDriveObjectResolutionStatus.Failed);
    }

    [Fact]
    public void Metadata_DefensivelyCopiesParentsAndFormatsSafely()
    {
        const string objectId = "drive-object-id-marker";
        const string parentId = "drive-parent-id-marker";
        var parents = new List<string> { parentId };
        var metadata = new GoogleDriveObjectMetadata(
            objectId,
            "Personal folder name",
            "application/vnd.google-apps.folder",
            false,
            parents,
            "shared-drive-id-marker");

        parents[0] = "changed";

        Assert.Equal(parentId, metadata.ParentIds[0]);
        Assert.DoesNotContain(objectId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(parentId, metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Personal folder name", metadata.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("shared-drive-id-marker", metadata.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionResult_ProvidesInfrastructureIdButSafeFormattingOmitsSensitiveData()
    {
        const string objectId = "drive-object-id-marker";
        const string account = "user@example.invalid";
        const string token = "oauth-token-marker";
        var metadata = new GoogleDriveObjectMetadata(
            objectId,
            "Folder",
            "application/vnd.google-apps.folder",
            false,
            Array.Empty<string>(),
            null);
        var result = new GoogleDriveObjectResolutionResult(
            GoogleDriveObjectResolutionStatus.Found,
            GoogleDriveRelativePath.Parse("Folder"),
            GoogleDriveObjectKind.Folder,
            metadata,
            $"UnsafeErrorCode-{objectId}-{account}-{token}",
            $"Unsafe provider detail {objectId} {account} {token}");

        Assert.Equal(objectId, result.ObjectId);
        Assert.Equal(metadata, result.Metadata);
        Assert.DoesNotContain(objectId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(account, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(result.ToString(), result.ToSafeDiagnosticString());
    }

    [Fact]
    public void Contracts_AreInfrastructureInternalAndDoNotLeakGoogleSdkTypes()
    {
        Type[] contractTypes =
        {
            typeof(GoogleDriveRelativePath),
            typeof(GoogleDriveObjectKind),
            typeof(GoogleDriveObjectMetadata),
            typeof(GoogleDriveObjectResolutionStatus),
            typeof(GoogleDriveObjectResolutionResult)
        };

        Assert.All(contractTypes, type =>
        {
            Assert.False(type.IsPublic);
            Assert.Equal("GameSaves.Infrastructure.GoogleDrive", type.Namespace);
            AssertNoGoogleSdkType(type);
        });

        Assert.DoesNotContain(
            typeof(ISyncProvider).Assembly.GetTypes(),
            type => contractTypes.Select(contract => contract.Name).Contains(type.Name));
        Assert.DoesNotContain(
            typeof(SyncViewModel).Assembly.GetTypes(),
            type => contractTypes.Select(contract => contract.Name).Contains(type.Name));
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
            exposed => exposed.Namespace?.StartsWith("Google.", StringComparison.Ordinal) == true);
    }
}
