using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GameSaves.App.ViewModels;

namespace GameSaves.Tests;

// RELEASE-001. The release package is produced by build/publish-release.ps1
// and build/GameSaveManager.iss; no test runs either. These tests pin what the
// scripts say, so a change that would publish another project, ship a harness,
// hard-code a second version, elevate, or touch user data fails here.
public sealed class ReleasePackagingTests
{
    private static readonly string RepoRoot = DocumentationIntegrityTests.FindRepoRoot();

    private static string Script(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    internal static string ProjectVersion() =>
        XDocument.Load(Path.Combine(RepoRoot, "Manager", "GameSaves.App", "GameSaves.App.csproj"))
            .Descendants("Version").Single().Value;

    [Fact]
    public void TheProjectVersion_IsWhatTheAssemblyAndItsFilePropertiesCarry()
    {
        string version = ProjectVersion();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);

        Assembly app = typeof(SettingsViewModel).Assembly;
        string informational = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(version, informational, StringComparison.Ordinal);

        FileVersionInfo file = FileVersionInfo.GetVersionInfo(app.Location);
        Assert.Equal(
            Version.Parse(version + ".0"),
            new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart));
        Assert.StartsWith(version, file.ProductVersion!, StringComparison.Ordinal);
        Assert.Equal("Game Save Manager", file.ProductName);
    }

    [Fact]
    public void ThePackagingScript_PublishesOnlyTheApp_SelfContained_AndNeverRunsIt()
    {
        string script = Script("build", "publish-release.ps1");

        MatchCollection publishes = Regex.Matches(script, @"&\s*dotnet\s+publish\s+(?<project>\S+)");
        Assert.NotEmpty(publishes);
        Assert.All(publishes, match =>
            Assert.Equal("$project", match.Groups["project"].Value));
        Assert.Contains("$project = 'Manager/GameSaves.App/GameSaves.App.csproj'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Manager.sln", script, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("--self-contained true", script, StringComparison.Ordinal);
        Assert.Contains("-r win-x64", script, StringComparison.Ordinal);
        Assert.Contains("--artifacts-path Manager/.artifacts", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"dotnet\s+run", script);
        Assert.DoesNotContain(@"\GameSave\", script, StringComparison.OrdinalIgnoreCase);

        // Signing is gated on a supplied certificate and silence is not an option.
        Assert.Contains("signtool", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CertificatePath", script, StringComparison.Ordinal);
        Assert.Contains("unsigned", script, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.Combine(RepoRoot, "build"), "*.pfx"));
        Assert.Empty(Directory.GetFiles(Path.Combine(RepoRoot, "build"), "*.p12"));
    }

    // RELEASE-002 reads this file to verify a downloaded installer: lowercase
    // hex, two spaces, file name, written after the installer is signed.
    [Fact]
    public void ThePackagingScript_PublishesTheInstallersDigest_AfterSigningIt()
    {
        string script = Script("build", "publish-release.ps1");

        int signInstaller = script.LastIndexOf("Invoke-Sign $installer", StringComparison.Ordinal);
        int digest = script.IndexOf("Get-FileHash", StringComparison.Ordinal);
        Assert.True(signInstaller >= 0, "the installer is signed when a certificate is supplied");
        Assert.True(digest > signInstaller, "the digest is computed after signing, which changes the bytes");

        Assert.Contains("-Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains(".ToLowerInvariant()", script, StringComparison.Ordinal);
        Assert.Contains("\"$installer.sha256\"", script, StringComparison.Ordinal);
        Assert.Contains("\"$digest  $(Split-Path -Leaf $installer)`n\"", script, StringComparison.Ordinal);
        Assert.Contains("UTF8Encoding($false)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstaller_InstallsPerUser_ShipsTheAppWithItsNotices_AndLeavesUserDataAlone()
    {
        string iss = Script("build", "GameSaveManager.iss");

        Assert.Contains("PrivilegesRequired=lowest", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", iss, StringComparison.Ordinal);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\Game Save Manager", iss, StringComparison.Ordinal);

        // One version: the one the script passes from the project file.
        Assert.Contains("AppVersion={#AppVersion}", iss, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"AppVersion=\d", iss);
        Assert.Contains("#ifndef AppVersion", iss, StringComparison.Ordinal);

        string[] sources = Regex.Matches(Section(iss, "Files"), @"Source:\s*""(?<source>[^""]+)""")
            .Select(match => match.Groups["source"].Value)
            .ToArray();
        Assert.Equal(new[] { @"{#PublishDir}\*", @"..\LICENSE", @"..\THIRD-PARTY-NOTICES.md" }, sources);
        Assert.True(File.Exists(Path.Combine(RepoRoot, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(RepoRoot, "THIRD-PARTY-NOTICES.md")));

        // Nothing is deleted beyond what was installed, and the user-data folder
        // is named only in the uninstaller's "kept in" message.
        Assert.DoesNotContain("[UninstallDelete]", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("[InstallDelete]", iss, StringComparison.Ordinal);
        string withoutComments = Regex.Replace(iss, @"^\s*;.*$", string.Empty, RegexOptions.Multiline);
        string code = Section(iss, "Code");
        Assert.Equal(
            Regex.Count(code, @"\\GameSave(?! Manager)"),
            Regex.Count(withoutComments, @"\\GameSave(?! Manager)"));
        Assert.Contains("usPostUninstall", code, StringComparison.Ordinal);
        Assert.Contains("UninstallSilent", code, StringComparison.Ordinal);
        Assert.Contains("were kept in", code, StringComparison.Ordinal);
    }

    private static string Section(string iss, string name)
    {
        int header = iss.IndexOf($"[{name}]", StringComparison.Ordinal);
        Assert.True(header >= 0, $"[{name}] section missing");
        int bodyStart = header + name.Length + 2;
        Match next = Regex.Match(iss[bodyStart..], @"^\[[A-Za-z]+\]", RegexOptions.Multiline);
        return next.Success ? iss.Substring(bodyStart, next.Index) : iss[bodyStart..];
    }
}
