namespace GameSaves.Tests;

public sealed class RoadmapIntegrityTests
{
    [Fact]
    public void RoadmapFile_ExistsAndIsNonEmpty()
    {
        string roadmapPath = FindRoadmapPath();
        Assert.True(File.Exists(roadmapPath), $"Roadmap file not found at: {roadmapPath}");

        string content = File.ReadAllText(roadmapPath);
        Assert.False(string.IsNullOrWhiteSpace(content), "Roadmap content is empty.");
    }

    [Fact]
    public void RoadmapFile_ContainsAllRequiredSections()
    {
        string content = File.ReadAllText(FindRoadmapPath());

        string[] requiredSections =
        [
            "## Now",
            "## Next",
            "## Later",
            "## Research",
            "## Blocked",
            "## Deferred and Cancelled",
            "## Completed"
        ];

        foreach (string section in requiredSections)
        {
            Assert.Contains(section, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RoadmapTables_HaveConsistentFiveColumnSchema()
    {
        string content = File.ReadAllText(FindRoadmapPath());
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        int rowCount = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
            {
                continue;
            }

            // Separator row (e.g., | --- | --- | ...)
            if (trimmed.Contains("---"))
            {
                continue;
            }

            string[] cells = trimmed
                .Split('|', StringSplitOptions.None)
                .Skip(1)
                .TakeWhile((_, idx) => idx < trimmed.Split('|').Length - 2)
                .Select(c => c.Trim())
                .ToArray();

            // Check column count is 5
            Assert.True(cells.Length == 5, $"Table row has {cells.Length} columns, expected 5: '{trimmed}'");
            rowCount++;
        }

        Assert.True(rowCount > 40, $"Expected at least 40 table rows, found {rowCount}");
    }

    [Fact]
    public void Roadmap_ContainsAllCanonicalBacklogAndExtendedCards()
    {
        string content = File.ReadAllText(FindRoadmapPath());

        string[] requiredCards =
        [
            // Epic A
            "DOC-001", "DOC-002", "DOC-003", "DOC-004", "DOC-005", "DOC-006", "DOC-007", "DOC-008",
            "DOC-009", "DOC-010", "DOC-011", "DOC-012", "DOC-013", "DOC-014", "DOC-015", "DOC-016",
            "DOC-017", "DOC-018", "DOC-019", "DOC-020", "DOC-021",
            // Epic B & M
            "UI-001", "UI-002", "UI-003", "UI-004", "UI-005", "UI-006", "UI-007", "UI-008",
            "UI-012", "UI-013", "UI-015",
            // Epic C
            "DRIVE-001", "DRIVE-002", "DRIVE-003", "DRIVE-004", "DRIVE-005", "DRIVE-006", "DRIVE-007", "DRIVE-008", "DRIVE-009",
            // Epic D
            "DATA-001", "DATA-002", "DATA-003", "DATA-004", "DATA-005",
            // Epic E
            "MAINT-001", "MAINT-002", "MAINT-003",
            // Epic F
            "PROVIDER-001", "PROVIDER-002", "PROVIDER-003", "PROVIDER-004", "PROVIDER-005", "PROVIDER-006", "PROVIDER-007", "PROVIDER-008",
            "PRODUCT-001", "SYNC-001", "SYNC-003", "SYNC-004",
            // Epic G
            "BACKUP-001", "BACKUP-002", "BACKUP-003", "BACKUP-004", "BACKUP-005", "BACKUP-006", "BACKUP-007", "BACKUP-008", "BACKUP-009", "BACKUP-010",
            // Epic H
            "PLATFORM-001", "PLATFORM-002", "PLATFORM-003", "PLATFORM-004", "PLATFORM-005",
            // Epic I
            "RELEASE-001", "RELEASE-002", "RELEASE-003",
            // Epic J
            "DISCORD-001", "DISCORD-002", "DISCORD-003", "DISCORD-004", "DISCORD-005", "DISCORD-006",
            // Epic S
            "SECURITY-001", "SECURITY-002",
            // Governance
            "GOVERNANCE-001",
            // Proposals OBS-001..OBS-028
            "OBS-001", "OBS-003", "OBS-004", "OBS-005", "OBS-006", "OBS-007", "OBS-008", "OBS-009", "OBS-010",
            "OBS-011", "OBS-012", "OBS-013", "OBS-014", "OBS-015", "OBS-016", "OBS-017", "OBS-018", "OBS-019",
            "OBS-020", "OBS-021", "OBS-022", "OBS-023", "OBS-024", "OBS-025", "OBS-026", "OBS-027", "OBS-028"
        ];

        foreach (string card in requiredCards)
        {
            Assert.True(content.Contains(card, StringComparison.Ordinal),
                $"Roadmap is missing card reference: {card}");
        }
    }

    [Fact]
    public void Roadmap_Obs023IsMarkedCancelled()
    {
        string content = File.ReadAllText(FindRoadmapPath());

        Assert.Contains("OBS-023", content, StringComparison.Ordinal);
        Assert.True(
            content.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Obsolete", StringComparison.OrdinalIgnoreCase),
            "OBS-023 must be explicitly marked as Cancelled or Obsolete as directed by the user.");
    }

    private static string FindRoadmapPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                string repoRoot = directory.Parent?.FullName
                    ?? throw new DirectoryNotFoundException("Could not find repository root above Manager folder.");
                return Path.Combine(repoRoot, "docs", "ROADMAP.md");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Manager.sln by walking up from test directory.");
    }
}
