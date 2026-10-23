using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GameSaves.App.ViewModels;
using GameSaves.Infrastructure.Transfers;

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

        // OBS-027: the public Google client id is a build property fed from a
        // variable; the script never takes the client secret.
        Assert.Contains("\"-p:GoogleDesktopClientId=$GoogleDesktopClientId\"", script, StringComparison.Ordinal);
        Assert.Contains("[string]$GoogleDesktopClientId = $env:GSM_GOOGLE_DESKTOP_CLIENT_ID", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIENT_SECRET", script, StringComparison.OrdinalIgnoreCase);

        // Signing is gated on a supplied certificate and silence is not an option.
        Assert.Contains("signtool", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CertificatePath", script, StringComparison.Ordinal);
        Assert.Contains("unsigned", script, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.Combine(RepoRoot, "build"), "*.pfx"));
        Assert.Empty(Directory.GetFiles(Path.Combine(RepoRoot, "build"), "*.p12"));

        // The sanitized seed database (`GameSaves seed-export`) ships beside the
        // program when it exists; the app copies it on its first start only. No
        // other database may sit under build/.
        Assert.Contains(@"Join-Path $PSScriptRoot 'seed\gamesave.db'", script, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $publishDir 'seed\gamesave.db'", script, StringComparison.Ordinal);
        Assert.Contains("No seed database", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.GetFiles(Path.Combine(RepoRoot, "build"), "*.db", SearchOption.AllDirectories),
            file => !file.EndsWith(Path.Combine("build", "seed", "gamesave.db"), StringComparison.OrdinalIgnoreCase));
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

    // OBS-028. The workflow builds with warnings as errors, tests, packages with
    // the same script a developer runs, uploads the two asset names the update
    // check reads, and needs no secret: the certificate is optional input that
    // exists in RUNNER_TEMP for one step.
    [Fact]
    public void TheWorkflow_BuildsTestsAndPackagesWithTheSameScript_AndNeedsNoSecret()
    {
        string workflow = Script(".github", "workflows", "release.yml");

        Assert.Contains("tags: ['v*']", workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build Manager/Manager.sln -c Release --no-incremental -warnaserror", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test Manager/Manager.sln -c Release --no-build", workflow, StringComparison.Ordinal);
        Assert.Contains("-ExecutionPolicy Bypass -File build/publish-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/release/GameSaveManager-Setup-${{ steps.version.outputs.version }}.exe\n", workflow.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("artifacts/release/GameSaveManager-Setup-${{ steps.version.outputs.version }}.exe.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/release/publish-release.log", workflow, StringComparison.Ordinal);

        string[] secrets = Regex.Matches(workflow, @"secrets\.(\w+)").Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(new[] { "GSM_SIGNING_CERTIFICATE", "GSM_SIGNING_PASSWORD" }, secrets);
        Assert.Contains("[Convert]::FromBase64String($env:SIGNING_CERTIFICATE_BASE64)", workflow, StringComparison.Ordinal);
        Assert.Contains("Join-Path $env:RUNNER_TEMP 'gsm-signing.pfx'", workflow, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $pfx -Force", workflow, StringComparison.Ordinal);
        Assert.Contains("so this package is unsigned", workflow, StringComparison.Ordinal);

        // The Google client id comes from a repository variable; the secrets
        // list above proves no Google secret is referenced.
        Assert.Contains("GSM_GOOGLE_DESKTOP_CLIENT_ID: ${{ vars.GSM_GOOGLE_DESKTOP_CLIENT_ID }}", workflow, StringComparison.Ordinal);
    }

    // The gates job has no setup-dotnet step and only runs the gate script;
    // the release job runs after it, only for a tag, and publishes a full
    // release with the two assets, never a draft or pre-release.
    [Fact]
    public void TheWorkflow_GatesThePackageWithoutAnSdk_AndReleasesOnlyAMatchingTag()
    {
        string workflow = Script(".github", "workflows", "release.yml");

        Assert.Contains("does not match <Version>", workflow, StringComparison.Ordinal);
        Assert.Contains("'${{ github.ref_name }}' -ne \"v$version\"", workflow, StringComparison.Ordinal);

        int gates = workflow.IndexOf("\n  gates:", StringComparison.Ordinal);
        int release = workflow.IndexOf("\n  release:", StringComparison.Ordinal);
        Assert.True(gates > 0 && release > gates, "the gates job precedes the release job");
        string gatesJob = workflow[gates..release];
        string releaseJob = workflow[release..];

        Assert.Contains("needs: build", gatesJob, StringComparison.Ordinal);
        Assert.DoesNotContain("setup-dotnet", gatesJob, StringComparison.Ordinal);
        Assert.Contains("./build/recovery-gates.ps1 -Installer", gatesJob, StringComparison.Ordinal);

        Assert.Contains("needs: [build, gates]", releaseJob, StringComparison.Ordinal);
        Assert.Contains("if: github.ref_type == 'tag'", releaseJob, StringComparison.Ordinal);
        Assert.Contains("gh release create \"$GITHUB_REF_NAME\"", releaseJob, StringComparison.Ordinal);
        Assert.Contains("\"package/GameSaveManager-Setup-$VERSION.exe\"", releaseJob, StringComparison.Ordinal);
        Assert.Contains("\"package/GameSaveManager-Setup-$VERSION.exe.sha256\"", releaseJob, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", releaseJob, StringComparison.Ordinal);
        Assert.DoesNotContain("--draft", releaseJob, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", releaseJob, StringComparison.Ordinal);
    }

    // The gate script refuses any machine that is not a clean runner, expects
    // the headless host's real exit codes, proves the self-contained start,
    // fabricates the newer database the way MigrationFixtureTests does, and
    // never deletes anything under the data folder.
    [Fact]
    public void TheRecoveryGates_RunOnlyOnACleanRunner_ExpectTheRealExitCodes_AndNeverDeleteUserData()
    {
        string gates = Script("build", "recovery-gates.ps1");

        Assert.Contains("$env:GITHUB_ACTIONS -ne 'true'", gates, StringComparison.Ordinal);
        Assert.Contains("already exists and the gates need a machine that never ran Game Save Manager", gates, StringComparison.Ordinal);
        Assert.Contains("@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')", gates, StringComparison.Ordinal);
        Assert.Contains("'--run-job'", gates, StringComparison.Ordinal);
        Assert.Equal((int)ScheduledBackupOutcome.Refused, int.Parse(Regex.Match(gates, @"\$Refused = (\d+)").Groups[1].Value));
        Assert.Equal((int)ScheduledBackupOutcome.Failed, int.Parse(Regex.Match(gates, @"\$Failed = (\d+)").Groups[1].Value));
        Assert.Contains("includedFrameworks", gates, StringComparison.Ordinal);
        Assert.Contains("$env:DOTNET_ROOT = $emptyRoot", gates, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO schema_migrations (id, name, applied_utc) VALUES (?, ?, ?)", gates, StringComparison.Ordinal);
        Assert.Contains("SELECT count(*) FROM game_titles", gates, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $appDir 'seed\gamesave.db'", gates, StringComparison.Ordinal);
        Assert.Contains("unins000.exe", gates, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"Remove-Item[^\r\n]*(dataDir|GameSave\\|database)", gates);
        Assert.DoesNotContain(".Delete(", gates, StringComparison.Ordinal);
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
