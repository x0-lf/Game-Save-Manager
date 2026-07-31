using System.Xml.Linq;

namespace GameSaves.Tests;

public sealed class DependencySecurityArchitectureTests
{
    private const string SqliteVersion = "10.0.10";
    private const string SqliteBundleVersion = "2.1.12";
    private const string ValveKeyValueVersion = "0.20.0.417";

    [Fact]
    public void ValveKeyValue_IsOwnedOnlyByInfrastructureAndGameloopIsRemoved()
    {
        string managerRoot = FindManagerRoot();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> packages =
            LoadProductionPackageReferences(managerRoot);

        Assert.Equal(
            ValveKeyValueVersion,
            packages["GameSaves.Infrastructure"]["ValveKeyValue"]);

        foreach ((string project, IReadOnlyDictionary<string, string> references) in packages)
        {
            Assert.DoesNotContain("Gameloop.Vdf", references.Keys);

            if (!project.Equals("GameSaves.Infrastructure", StringComparison.Ordinal))
                Assert.DoesNotContain("ValveKeyValue", references.Keys);
        }
    }

    [Fact]
    public void ValveKeyValueTypes_RemainOutsideCoreAndAppSource()
    {
        string managerRoot = FindManagerRoot();

        foreach (string project in new[] { "GameSaves.Core", "GameSaves.App" })
        {
            foreach (string sourcePath in EnumerateProductionSource(
                         Path.Combine(managerRoot, project)))
            {
                string source = File.ReadAllText(sourcePath);
                Assert.DoesNotContain("using ValveKeyValue", source, StringComparison.Ordinal);
                Assert.DoesNotContain("KVSerializer", source, StringComparison.Ordinal);
                Assert.DoesNotContain("KVDocument", source, StringComparison.Ordinal);
                Assert.DoesNotContain("KVObject", source, StringComparison.Ordinal);
                Assert.DoesNotContain("KVValue", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DirectSqliteOwners_UsePatchedProviderAndBundleVersions()
    {
        string managerRoot = FindManagerRoot();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> packages =
            LoadProductionPackageReferences(managerRoot);

        foreach (string project in new[]
                 {
                     "GameSaves.Infrastructure",
                     "GameSaves.Reviewer",
                     "GameSaves"
                 })
        {
            Assert.Equal(SqliteVersion, packages[project]["Microsoft.Data.Sqlite"]);
            Assert.Equal(
                SqliteBundleVersion,
                packages[project]["SQLitePCLRaw.bundle_e_sqlite3"]);
        }

        Assert.DoesNotContain("Microsoft.Data.Sqlite", packages["GameSaves.Core"].Keys);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", packages["GameSaves.App"].Keys);
    }

    [Fact]
    public void NoProjectAddsDirectLegacyFrameworkPackageWorkarounds()
    {
        string managerRoot = FindManagerRoot();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> packages =
            LoadProductionPackageReferences(managerRoot);

        Assert.All(packages.Values, references =>
        {
            Assert.DoesNotContain("System.Net.Http", references.Keys);
            Assert.DoesNotContain("System.Text.RegularExpressions", references.Keys);
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        LoadProductionPackageReferences(string managerRoot)
    {
        string[] projects =
        {
            "GameSaves.Core",
            "GameSaves.Infrastructure",
            "GameSaves.App",
            "GameSaves",
            "GameSaves.Reviewer"
        };

        return projects.ToDictionary(
            project => project,
            project => (IReadOnlyDictionary<string, string>)XDocument
                .Load(Path.Combine(managerRoot, project, $"{project}.csproj"))
                .Descendants("PackageReference")
                .ToDictionary(
                    reference => (string)reference.Attribute("Include")!,
                    reference => (string?)reference.Attribute("Version") ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateProductionSource(string projectRoot) =>
        Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

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
