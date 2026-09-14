using System.Text.RegularExpressions;

namespace GameSaves.Tests;

public sealed class DocumentationIntegrityTests
{
    private static readonly Regex MarkdownLinkRegex = new(
        @"(?<!!)\[.*?\]\((?!https?:\/\/|mailto:|#)(?<target>[^)\s]+)\)",
        RegexOptions.Compiled);

    [Fact]
    public void AllMarkdownRelativeLinks_ResolveToExistingFiles_WithExactCasing()
    {
        string repoRoot = FindRepoRoot();
        List<string> markdownFiles =
        [
            Path.Combine(repoRoot, "README.md"),
            Path.Combine(repoRoot, "CONTRIBUTING.md"),
            Path.Combine(repoRoot, "SECURITY.md"),
            Path.Combine(repoRoot, "CODE_OF_CONDUCT.md"),
            Path.Combine(repoRoot, "THIRD-PARTY-NOTICES.md")
        ];

        string docsDir = Path.Combine(repoRoot, "docs");
        if (Directory.Exists(docsDir))
        {
            markdownFiles.AddRange(Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories));
        }

        List<string> brokenLinks = [];

        foreach (string mdFile in markdownFiles)
        {
            // Skip observation files and reports which are snapshot historical notes
            string relativeToDocs = Path.GetRelativePath(repoRoot, mdFile).Replace('\\', '/');
            if (relativeToDocs.StartsWith("docs/observation", StringComparison.OrdinalIgnoreCase) ||
                relativeToDocs.StartsWith("docs/raports", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = File.ReadAllText(mdFile);
            string mdDir = Path.GetDirectoryName(mdFile)!;

            foreach (Match match in MarkdownLinkRegex.Matches(content))
            {
                string rawTarget = match.Groups["target"].Value.Trim();
                if (string.IsNullOrEmpty(rawTarget))
                {
                    continue;
                }

                // Strip anchor fragment if present
                int hashIdx = rawTarget.IndexOf('#');
                string targetPath = hashIdx >= 0 ? rawTarget[..hashIdx] : rawTarget;
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    continue; // Internal page anchor
                }

                // Unescape url-encoded chars (e.g. %20)
                targetPath = Uri.UnescapeDataString(targetPath);

                string resolvedTarget = Path.GetFullPath(Path.Combine(mdDir, targetPath));

                if (!File.Exists(resolvedTarget) && !Directory.Exists(resolvedTarget))
                {
                    brokenLinks.Add($"{relativeToDocs} -> '{rawTarget}' (Resolved to non-existent: {resolvedTarget})");
                    continue;
                }

                // Exact casing verification on Windows
                if (!VerifyPathCasingMatchesDisk(resolvedTarget))
                {
                    brokenLinks.Add($"{relativeToDocs} -> '{rawTarget}' (Casing mismatch against disk: {resolvedTarget})");
                }
            }
        }

        Assert.True(brokenLinks.Count == 0,
            "Broken or incorrectly cased markdown links detected:\n" + string.Join("\n", brokenLinks));
    }

    [Fact]
    public void ProspectiveUserWalkthrough_VerifiesAllRequiredSections()
    {
        string repoRoot = FindRepoRoot();
        string readme = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "README.md")));
        string gettingStarted = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "getting-started.md")));
        string safetyModel = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "safety-model.md")));
        string syncProviders = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "sync-providers.md")));

        // 1. Support status
        Assert.Contains("Windows is the primary supported environment", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-release", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows 10 or 11", gettingStarted, StringComparison.OrdinalIgnoreCase);

        // 2. System requirements
        Assert.Contains(".NET 10 SDK", gettingStarted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Git", gettingStarted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steam installed", gettingStarted, StringComparison.OrdinalIgnoreCase);

        // 3. First safe backup
        Assert.Contains("startup scan", gettingStarted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manual backup with disposable or copied data first", gettingStarted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inspect the preview, destination, and overwrite settings", gettingStarted, StringComparison.OrdinalIgnoreCase);

        // 4. Deletion boundaries
        Assert.Contains("Backup cleanup is the only user-facing feature that deletes user backup content", safetyModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest-bearing run directories inside the application backup base", safetyModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sync never overwrites or deletes", readme, StringComparison.OrdinalIgnoreCase);

        // 5. Available sync providers
        Assert.Contains("Local Folder, SFTP, and Google Drive synchronization are implemented", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebDAV and OneDrive appear in the provider catalog but cannot be configured or used", readme, StringComparison.OrdinalIgnoreCase);

        // 6. Google Drive limitations
        Assert.Contains("https://www.googleapis.com/auth/drive.file", syncProviders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Google's own rate limits", syncProviders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Local Folder alternative for bulk transfers", syncProviders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContributorWalkthrough_VerifiesAllRequiredSections()
    {
        string repoRoot = FindRepoRoot();
        string dev = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "development.md")));
        string arch = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "architecture.md")));
        string syncProviders = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "sync-providers.md")));

        // 1. Build and test instructions for all 8 projects
        string[] expectedProjects =
        [
            "GameSaves.Core",
            "GameSaves.Infrastructure",
            "GameSaves.App",
            "GameSaves",
            "GameSaves.Reviewer",
            "GameSaves.Tests",
            "GameSaves.UiCapture",
            "GameSaves.UiMaterialCapture"
        ];

        foreach (string proj in expectedProjects)
        {
            Assert.Contains(proj, dev, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(proj, arch, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("dotnet restore Manager/Manager.sln", dev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet build Manager/Manager.sln --configuration Release", dev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet test Manager/Manager.sln --configuration Release", dev, StringComparison.OrdinalIgnoreCase);

        // 2. Architecture owners and dependency direction
        Assert.Contains("App -------> Core", arch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("App -------> Infrastructure ------> Core", arch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reviewer (independent)", arch, StringComparison.OrdinalIgnoreCase);

        // 3. Provider Definition of Done
        Assert.Contains("Provider Definition of Done", syncProviders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stable kind, capability metadata, configuration surface, and factory path agree", syncProviders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-overwrite, no-delete, manifest-last, conflict, and path-containment rules remain proven", syncProviders, StringComparison.OrdinalIgnoreCase);

        // 4. Reporting unverified platform checks
        Assert.Contains("explicitly reported as unverified", syncProviders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Record warnings, failures, skipped checks, credential/network needs, and any unverified claim", dev, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintainerWalkthrough_VerifiesAllRequiredSections()
    {
        string repoRoot = FindRepoRoot();
        string db = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "database-and-mappings.md")));
        string safetyModel = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "safety-model.md")));
        string dev = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "development.md")));
        string roadmap = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "ROADMAP.md")));
        string security = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "SECURITY.md")));
        string notices = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "THIRD-PARTY-NOTICES.md")));
        string docsReadme = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "README.md")));

        // 1. Runtime and curated data paths
        Assert.Contains(@"%LOCALAPPDATA%\GameSave\gamesave.db", db, StringComparison.OrdinalIgnoreCase);

        // 2. Mapping trust rules
        Assert.Contains("Only mappings with `Approved` review status participate in trusted transfer behavior", safetyModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Automated candidates enter the database disabled and `Pending`", db, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Only a checked candidate becomes `Approved` and enabled", db, StringComparison.OrdinalIgnoreCase);

        // 3. Release checks
        Assert.Contains("Pre-release and release checks", dev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet list Manager/Manager.sln package --vulnerable", dev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git diff --check", dev, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git status --short", dev, StringComparison.OrdinalIgnoreCase);

        // 4. Active backlog
        Assert.Contains("authoritative roadmap for active, future, and completed work", roadmap, StringComparison.OrdinalIgnoreCase);

        // 5. Security response
        Assert.Contains("Reporting a vulnerability", security, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Supported versions", security, StringComparison.OrdinalIgnoreCase);

        // 6. Dependency records
        Assert.Contains("Third-Party Notices", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Avalonia", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Google.Apis.Drive.v3", notices, StringComparison.OrdinalIgnoreCase);

        // 7. Historical acceptance archives
        Assert.Contains("history/google-drive-roadmap.md", docsReadme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("history/google-drive-acceptance.md", docsReadme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefinitionOfDoneContract_VerifiesAllRequiredPillars()
    {
        string repoRoot = FindRepoRoot();
        string dodPath = Path.Combine(repoRoot, "docs", "definition-of-done.md");
        Assert.True(File.Exists(dodPath), "docs/definition-of-done.md must exist.");

        string dod = NormalizeWhitespace(File.ReadAllText(dodPath));
        string contributing = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "CONTRIBUTING.md")));
        string docsReadme = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "README.md")));
        string roadmap = NormalizeWhitespace(File.ReadAllText(Path.Combine(repoRoot, "docs", "ROADMAP.md")));

        // 1. Pillar 1: Code Quality
        Assert.Contains("Pillar 1: Code Quality", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zero Warnings and Zero Errors", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Strict Nullability", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Architectural Boundary Enforcement", dod, StringComparison.OrdinalIgnoreCase);

        // 2. Pillar 2: Automated Testing
        Assert.Contains("Pillar 2: Automated Testing", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100% Deterministic and Offline", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zero Test Failures or Skips", dod, StringComparison.OrdinalIgnoreCase);

        // 3. Pillar 3: Accessibility & UX
        Assert.Contains("Pillar 3: Accessibility", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WCAG Contrast Compliance", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Information Never Conveyed by Color Alone", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("High Contrast Mode Support", dod, StringComparison.OrdinalIgnoreCase);

        // 4. Pillar 4: Data Safety
        Assert.Contains("Pillar 4: Data Safety", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preview Before Execution", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Strict Mapping Approval Trust Boundary", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Protected Secrets", dod, StringComparison.OrdinalIgnoreCase);

        // 5. Pillar 5: Platform Verification
        Assert.Contains("Pillar 5: Platform Verification", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Truth in Status", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Explicit Disclosure of Unverified Checks", dod, StringComparison.OrdinalIgnoreCase);

        // 6. Pillar 6: Documentation & Traceability
        Assert.Contains("Pillar 6: Documentation", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authoritative Documentation Synchronization", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Implementation Report", dod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Commit Log Recording", dod, StringComparison.OrdinalIgnoreCase);

        // Cross-repository formalization
        Assert.Contains("definition-of-done.md", contributing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("definition-of-done.md", docsReadme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("definition-of-done.md", roadmap, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ");

    private static bool VerifyPathCasingMatchesDisk(string fullPath)
    {
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break; // Reached root drive (e.g. C:\)
            }

            string expectedName = Path.GetFileName(current);
            if (string.IsNullOrEmpty(expectedName))
            {
                break;
            }

            if (Directory.Exists(parent))
            {
                string? actualEntry = Directory.EnumerateFileSystemEntries(parent, expectedName)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(name => string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase));

                if (actualEntry is not null && !string.Equals(actualEntry, expectedName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            current = parent;
        }

        return true;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                return directory.Parent?.FullName
                    ?? throw new DirectoryNotFoundException("Could not find repository root above Manager folder.");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Manager.sln by walking up from test directory.");
    }
}
