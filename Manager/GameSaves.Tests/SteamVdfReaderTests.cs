using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Steam;
using System.Text;

namespace GameSaves.Tests;

public sealed class SteamVdfReaderTests
{
    [Fact]
    public void AppManifest_ParsesStandardInstalledGameFromStream()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        CreateInstalledGameFolder(library, "Example Game");
        WriteManifest(library, "1234", Manifest(
            appId: "1234",
            name: "Example Game",
            installDirectory: "Example Game"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library));

        Assert.Equal("1234", game.AppId);
        Assert.Equal("Example Game", game.Name);
        Assert.Equal("Example Game", game.InstallDirectory);
        Assert.True(game.FolderExists);
        Assert.Equal(SteamDiscoveryConfidence.High, game.Confidence);
    }

    [Fact]
    public void AppManifest_MissingAppIdFallsBackToManifestFileName()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "5678", Manifest(
            appId: null,
            name: "Filename Fallback",
            installDirectory: "Fallback"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library));

        Assert.Equal("5678", game.AppId);
    }

    [Fact]
    public void AppManifest_MissingNameUsesStableFallback()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "42", Manifest(
            appId: "42",
            name: null,
            installDirectory: "Nameless"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library));

        Assert.Equal("Unknown Steam App 42", game.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppManifest_MissingOrBlankInstallDirectoryIsSkipped(
        string? installDirectory)
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "100", Manifest(
            appId: "100",
            name: "No Install Directory",
            installDirectory));

        Assert.Empty(new SteamAppManifestReader().ReadInstalledGames(library));
    }

    [Fact]
    public void AppManifest_MissingInstalledFolderUsesMediumConfidence()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "101", Manifest(
            appId: "101",
            name: "Not Installed",
            installDirectory: "Missing"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library, SteamDiscoveryConfidence.Low));

        Assert.False(game.FolderExists);
        Assert.Equal(SteamDiscoveryConfidence.Medium, game.Confidence);
    }

    [Fact]
    public void AppManifest_ExistingFolderPreservesSuppliedConfidence()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        CreateInstalledGameFolder(library, "Low Confidence");
        WriteManifest(library, "102", Manifest(
            appId: "102",
            name: "Low Confidence",
            installDirectory: "Low Confidence"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library, SteamDiscoveryConfidence.Low));

        Assert.Equal(SteamDiscoveryConfidence.Low, game.Confidence);
    }

    [Theory]
    [InlineData("\"AppState\" { \"appid\" \"1\"")]
    [InlineData("\"AppState\" { \"appid\" \"1\" \"name\"")]
    [InlineData("not-key-values")]
    public void AppManifest_MalformedOrTruncatedInputIsSkipped(string content)
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "1", content);

        Assert.Empty(new SteamAppManifestReader().ReadInstalledGames(library));
    }

    [Fact]
    public void AppManifest_UnicodeCommentsBomAndEscapesArePreserved()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        string installDirectory = @"Jeu d'été\Épisode 1";
        string name = "L'été \"Édition\" 東京";
        CreateInstalledGameFolder(library, installDirectory);
        string content = Manifest(
            appId: "200",
            name,
            installDirectory,
            comment: "// Steam-generated comment");
        WriteManifest(library, "200", content, emitBom: true);

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library));

        Assert.Equal(name, game.Name);
        Assert.Equal(installDirectory, game.InstallDirectory);
        Assert.True(game.FolderExists);
    }

    [Fact]
    public void AppManifest_OneInvalidManifestDoesNotStopOtherManifests()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(library, "1", "\"AppState\" {");
        WriteManifest(library, "2", Manifest(
            appId: "2",
            name: "Valid Game",
            installDirectory: "Valid"));

        SteamGame game = Assert.Single(new SteamAppManifestReader()
            .ReadInstalledGames(library));

        Assert.Equal("2", game.AppId);
    }

    [Fact]
    public void AppManifest_IgnoresFilesOutsideAppManifestPattern()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        File.WriteAllText(
            Path.Combine(library, "steamapps", "unrelated.acf"),
            Manifest("99", "Unrelated", "Unrelated"));

        Assert.Empty(new SteamAppManifestReader().ReadInstalledGames(library));
    }

    [Fact]
    public void AppManifest_UnknownEscapeIsRejectedRatherThanTruncatingPath()
    {
        using var temp = new TemporaryDirectory();
        string library = CreateLibrary(temp);
        WriteManifest(
            library,
            "300",
            "\"AppState\" { \"appid\" \"300\" \"name\" \"Bad\" \"installdir\" \"D:\\Unknown\" }");

        Assert.Empty(new SteamAppManifestReader().ReadInstalledGames(library));
    }

    [Fact]
    public void LibraryFolders_ParsesLegacySingleLineFormat()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string library = CreateExistingDirectory(temp.GetPath("Legacy Library"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            LegacyEntry("1", library)));

        Assert.Equal(
            Path.GetFullPath(library),
            Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
    }

    [Fact]
    public void LibraryFolders_ParsesModernNestedFormatAndMainEntry()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string main = CreateExistingDirectory(temp.GetPath("Main Steam"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            ModernEntry("0", main)));

        Assert.Equal(
            Path.GetFullPath(main),
            Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
    }

    [Fact]
    public void LibraryFolders_PreservesMultipleLibraryOrder()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string first = CreateExistingDirectory(temp.GetPath("First"));
        string second = CreateExistingDirectory(temp.GetPath("Second"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            ModernEntry("0", first),
            LegacyEntry("1", second)));

        Assert.Equal(
            new[] { Path.GetFullPath(first), Path.GetFullPath(second) },
            new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot));
    }

    [Fact]
    public void LibraryFolders_DecodesEscapedWindowsSeparatorsSpacesAndApostrophes()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string library = CreateExistingDirectory(temp.GetPath("Player's Steam Library"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            ModernEntry("1", library)));

        Assert.Equal(
            Path.GetFullPath(library),
            Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
    }

    [Fact]
    public void LibraryFolders_PreservesUnicodePathAndUtf8Bom()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string library = CreateExistingDirectory(temp.GetPath("Bibliothèque 東京"));
        WriteLibraryFolders(
            steamRoot,
            LibraryFolders(ModernEntry("1", library)),
            emitBom: true);

        Assert.Equal(
            Path.GetFullPath(library),
            Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
    }

    [Fact]
    public void LibraryFolders_ExpandsEnvironmentVariables()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string variableName = $"GSM_STEAM_{Guid.NewGuid():N}";
        string basePath = CreateExistingDirectory(temp.GetPath("Environment Base"));
        string library = CreateExistingDirectory(Path.Combine(basePath, "Library"));
        Environment.SetEnvironmentVariable(variableName, basePath);

        try
        {
            WriteLibraryFolders(steamRoot, LibraryFolders(
                ModernEntry("1", $"%{variableName}%\\Library")));

            Assert.Equal(
                Path.GetFullPath(library),
                Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void LibraryFolders_SkipsMissingPathInvalidPathAndNonexistentDirectory()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        WriteLibraryFolders(steamRoot, LibraryFolders(
            "\"1\" { \"label\" \"No path\" }",
            ModernEntry("2", "invalid\0path"),
            ModernEntry("3", temp.GetPath("Does Not Exist"))));

        Assert.Empty(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot));
    }

    [Theory]
    [InlineData("\"libraryfolders\" {")]
    [InlineData("\"libraryfolders\" { \"1\"")]
    [InlineData("not-key-values")]
    public void LibraryFolders_MalformedOrTruncatedInputIsSkipped(string content)
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        WriteLibraryFolders(steamRoot, content);

        Assert.Empty(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot));
    }

    [Fact]
    public void LibraryFolders_UnknownEscapeIsRejectedRatherThanTruncatingPath()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        WriteLibraryFolders(
            steamRoot,
            "\"libraryfolders\" { \"1\" { \"path\" \"D:\\Unknown\" } }");

        Assert.Empty(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot));
    }

    [Fact]
    public void LibraryFolders_ReadsBothCandidateFilesAndSuppressesDuplicates()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string first = CreateExistingDirectory(temp.GetPath("First Candidate"));
        string second = CreateExistingDirectory(temp.GetPath("Second Candidate"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            ModernEntry("1", first),
            ModernEntry("2", second)));
        WriteLibraryFolders(
            steamRoot,
            LibraryFolders(
                ModernEntry("1", first),
                ModernEntry("2", second)),
            underConfig: true);

        Assert.Equal(
            new[] { Path.GetFullPath(first), Path.GetFullPath(second) },
            new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot));
    }

    [Fact]
    public void LibraryFolders_IgnoresNonNumericMetadataEntriesAndSupportsComments()
    {
        using var temp = new TemporaryDirectory();
        string steamRoot = CreateSteamRoot(temp);
        string library = CreateExistingDirectory(temp.GetPath("Numeric Entry"));
        WriteLibraryFolders(steamRoot, LibraryFolders(
            "// metadata follows",
            ModernEntry("contentstatsid", library),
            ModernEntry("1", library)));

        Assert.Equal(
            Path.GetFullPath(library),
            Assert.Single(new SteamLibraryFoldersReader().ReadLibraryPaths(steamRoot)));
    }

    private static string CreateLibrary(TemporaryDirectory temp)
    {
        string library = temp.GetPath("SteamLibrary");
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        return library;
    }

    private static string CreateSteamRoot(TemporaryDirectory temp)
    {
        string root = temp.GetPath("SteamRoot");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static string CreateExistingDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateInstalledGameFolder(string library, string installDirectory) =>
        Directory.CreateDirectory(Path.Combine(
            library,
            "steamapps",
            "common",
            installDirectory));

    private static void WriteManifest(
        string library,
        string appId,
        string content,
        bool emitBom = false)
    {
        string path = Path.Combine(
            library,
            "steamapps",
            $"appmanifest_{appId}.acf");
        File.WriteAllText(path, content, new UTF8Encoding(emitBom));
    }

    private static void WriteLibraryFolders(
        string steamRoot,
        string content,
        bool underConfig = false,
        bool emitBom = false)
    {
        string path = Path.Combine(
            steamRoot,
            underConfig ? "config" : "steamapps",
            "libraryfolders.vdf");
        File.WriteAllText(path, content, new UTF8Encoding(emitBom));
    }

    private static string Manifest(
        string? appId,
        string? name,
        string? installDirectory,
        string? comment = null)
    {
        var entries = new List<string>();
        if (comment is not null)
            entries.Add(comment);
        if (appId is not null)
            entries.Add($"\"appid\" \"{Escape(appId)}\"");
        if (name is not null)
            entries.Add($"\"name\" \"{Escape(name)}\"");
        if (installDirectory is not null)
            entries.Add($"\"installdir\" \"{Escape(installDirectory)}\"");

        return $"\"AppState\"{Environment.NewLine}{{{Environment.NewLine}    {string.Join($"{Environment.NewLine}    ", entries)}{Environment.NewLine}}}";
    }

    private static string LibraryFolders(params string[] entries) =>
        $"\"libraryfolders\"{Environment.NewLine}{{{Environment.NewLine}    {string.Join($"{Environment.NewLine}    ", entries)}{Environment.NewLine}}}";

    private static string LegacyEntry(string index, string path) =>
        $"\"{index}\" \"{Escape(path)}\"";

    private static string ModernEntry(string index, string path) =>
        $"\"{index}\"{Environment.NewLine}    {{{Environment.NewLine}        \"path\" \"{Escape(path)}\"{Environment.NewLine}    }}";

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
