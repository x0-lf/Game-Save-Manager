using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GameSaves.Tests;

public sealed class SanitizationAndGalleryTests
{
    private static readonly Regex MarkdownImageRegex = new(
        @"!\[.*?\]\((?!https?:\/\/)(?<target>[^)\s]+)\)",
        RegexOptions.Compiled);

    [Fact]
    public void AllMarkdownImages_ResolveToExistingFiles_WithExactCasing()
    {
        string repoRoot = FindRepoRoot();
        string docsDir = Path.Combine(repoRoot, "docs");
        Assert.True(Directory.Exists(docsDir), "docs directory should exist");

        var mdFiles = Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories)
            .Where(f => !IsIgnoredDocFile(repoRoot, f))
            .ToList();

        List<string> brokenImages = [];

        foreach (string mdFile in mdFiles)
        {
            string content = File.ReadAllText(mdFile);
            string mdDir = Path.GetDirectoryName(mdFile)!;
            string relativeDoc = Path.GetRelativePath(repoRoot, mdFile).Replace('\\', '/');

            foreach (Match match in MarkdownImageRegex.Matches(content))
            {
                string rawTarget = match.Groups["target"].Value.Trim();
                if (string.IsNullOrEmpty(rawTarget))
                    continue;

                // Strip anchor or query if any
                int queryIdx = rawTarget.IndexOfAny(['?', '#']);
                string targetPath = queryIdx >= 0 ? rawTarget[..queryIdx] : rawTarget;
                targetPath = Uri.UnescapeDataString(targetPath);

                string resolvedTarget = Path.GetFullPath(Path.Combine(mdDir, targetPath));

                if (!File.Exists(resolvedTarget))
                {
                    brokenImages.Add($"{relativeDoc} -> '{rawTarget}' (File not found: {resolvedTarget})");
                    continue;
                }

                if (!VerifyPathCasingMatchesDisk(resolvedTarget))
                {
                    brokenImages.Add($"{relativeDoc} -> '{rawTarget}' (Casing mismatch: {resolvedTarget})");
                }
            }
        }

        Assert.True(brokenImages.Count == 0,
            "Broken or mis-cased markdown image links detected:\n" + string.Join("\n", brokenImages));
    }

    [Fact]
    public void GalleryManifest_IsCompleteAndAccurate()
    {
        string repoRoot = FindRepoRoot();
        string imagesDir = Path.Combine(repoRoot, "docs", "images");
        string manifestPath = Path.Combine(imagesDir, "gallery-manifest.tsv");

        Assert.True(File.Exists(manifestPath), $"Gallery manifest must exist at {manifestPath}");

        string[] lines = File.ReadAllLines(manifestPath);
        Assert.True(lines.Length > 1, "Manifest must contain a header and data rows");

        string header = lines[0];
        Assert.Equal("filename\ttab\ttitle\tdescription\twidth\theight\ttheme\tsanitized", header);

        var manifestFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            string[] cols = lines[i].Split('\t');
            Assert.True(cols.Length >= 8, $"Row {i} must have at least 8 columns");

            string filename = cols[0];
            manifestFilenames.Add(filename);

            string width = cols[4];
            string height = cols[5];
            string sanitized = cols[7];

            Assert.Equal("1400", width);
            Assert.Equal("900", height);
            Assert.Equal("true", sanitized, ignoreCase: true);

            string diskPath = Path.Combine(imagesDir, filename);
            Assert.True(File.Exists(diskPath), $"Image {filename} referenced in manifest must exist on disk");
            Assert.True(VerifyPathCasingMatchesDisk(diskPath), $"Image {filename} casing must match disk exactly");
        }

        // Check that every png file in docs/images is accounted for in the manifest
        var diskPngs = Directory.GetFiles(imagesDir, "*.png").Select(Path.GetFileName).ToList();
        foreach (string? png in diskPngs)
        {
            if (png != null)
            {
                Assert.Contains(png, manifestFilenames);
            }
        }
    }

    [Fact]
    public void DocumentationAndManifest_ContainZeroPersonalOrSensitiveData()
    {
        string repoRoot = FindRepoRoot();
        string docsDir = Path.Combine(repoRoot, "docs");

        var textFiles = Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories)
            .Where(f => !IsIgnoredDocFile(repoRoot, f))
            .Concat(Directory.GetFiles(docsDir, "*.tsv", SearchOption.AllDirectories))
            .ToList();

        // Sensitive pattern indicators
        var forbiddenPatterns = new (string Pattern, string Description)[]
        {
            (@"(?i)C:\\Users\\(?!username\b|public\b|default\b)[a-zA-Z0-9_.-]+", "Personal Windows user profile path"),
            (@"(?i)\/home\/(?!username\b)[a-zA-Z0-9_.-]+", "Personal Linux home directory path"),
            (@"(?i)ya29\.[a-zA-Z0-9_-]{20,}", "Google OAuth access token"),
            (@"(?i)client_secret[_\-a-zA-Z0-9]*\s*[:=]\s*[""'][a-zA-Z0-9_\-]{16,}[""']", "Client secret"),
            (@"(?i)bearer\s+[a-zA-Z0-9_\-\.]{30,}", "Bearer token"),
            (@"(?i)password\s*[:=]\s*[""'][^""'\r\n]{4,}[""']", "Hardcoded password"),
        };

        List<string> violations = [];

        foreach (string file in textFiles)
        {
            string content = File.ReadAllText(file);
            string relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            foreach (var (pattern, description) in forbiddenPatterns)
            {
                var matches = Regex.Matches(content, pattern);
                foreach (Match m in matches)
                {
                    violations.Add($"{relPath}: Found {description} ('{m.Value}')");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Sanitization check failed. Sensitive patterns found:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void GalleryDocument_ReferencesAllPrimaryCaptures()
    {
        string repoRoot = FindRepoRoot();
        string galleryPath = Path.Combine(repoRoot, "docs", "gallery.md");
        Assert.True(File.Exists(galleryPath), "docs/gallery.md must exist");

        string content = File.ReadAllText(galleryPath);

        string[] requiredImages =
        [
            "00-dashboard.png",
            "01-installed-games.png",
            "02-profiles.png",
            "03-transfer-preview.png",
            "04-manual-backup.png",
            "05-backups-tree.png",
            "05-backups-table.png",
            "06-sync-plan.png",
            "07-history.png",
            "08-settings-appearance.png",
            "09-custom-accent.png",
            "10-workspace-layout.png"
        ];

        foreach (string image in requiredImages)
        {
            Assert.Contains(image, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsIgnoredDocFile(string repoRoot, string filePath)
    {
        string relative = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
        return relative.StartsWith("docs/observation", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("docs/raports", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(".claude", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VerifyPathCasingMatchesDisk(string path)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                break; // Hit drive root

            string name = Path.GetFileName(current);
            string? exactMatch = Directory.GetFileSystemEntries(parent, name)
                .Select(Path.GetFileName)
                .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is null || !string.Equals(exactMatch, name, StringComparison.Ordinal))
                return false;

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
