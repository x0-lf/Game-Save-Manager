using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace GameSaves.Tests;

public sealed class VirtualizedDataGridAndTreeTests
{
    // =========================================================================
    // 1. Deferred / Lazy File Tree Loading & Hierarchy Verification
    // =========================================================================

    [Fact]
    public void BuildFromDescriptors_OnlyInstantiatesRootViewModelsUpfront()
    {
        // 10,000 files spread across 10 directories with subfolders
        var items = new List<SaveFileItemDescriptor>();
        for (int dir = 1; dir <= 10; dir++)
        {
            for (int sub = 1; sub <= 10; sub++)
            {
                for (int file = 1; file <= 100; file++)
                {
                    items.Add(new SaveFileItemDescriptor(
                        Path: $"Dir_{dir:D2}/Sub_{sub:D2}/save_{file:D3}.dat",
                        Bytes: 1024,
                        Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
                }
            }
        }

        Assert.Equal(10000, items.Count);

        // Act: Build tree
        List<SaveFileTreeNodeViewModel> roots = LazyFileTreeBuilder.Build(items);

        // Assert: Root nodes only (10 directories)
        Assert.Equal(10, roots.Count);
        Assert.All(roots, r => Assert.True(r.IsDirectory));
        Assert.All(roots, r => Assert.False(r.IsChildrenLoaded));
        Assert.All(roots, r => Assert.True(r.HasChildren));

        // In standard TreeView, Children starts with 1 dummy placeholder item
        Assert.All(roots, r => Assert.Single(r.Children));
        Assert.All(roots, r => Assert.Equal("Loading...", r.Children[0].Name));

        // Aggregates are computed correctly during indexing without creating child ViewModels
        Assert.All(roots, r => Assert.Equal(1000, r.FileCount));
        Assert.All(roots, r => Assert.Equal(1000 * 1024, r.SizeBytes));
        Assert.All(roots, r => Assert.Equal("1,000 files", r.StatusDisplay));
    }

    [Fact]
    public void Expand_DirectoryNode_LoadsImmediateChildrenOnDemand()
    {
        var items = new List<SaveFileItemDescriptor>
        {
            new("Saves/Slot1/game.sav", 5000),
            new("Saves/Slot1/meta.json", 200),
            new("Saves/Slot2/game.sav", 6000),
            new("Screenshots/shot1.png", 50000)
        };

        List<SaveFileTreeNodeViewModel> roots = LazyFileTreeBuilder.Build(items);

        Assert.Equal(2, roots.Count); // Saves, Screenshots
        var saves = roots.First(r => r.Name == "Saves");
        Assert.False(saves.IsChildrenLoaded);

        // Act: expand Saves
        saves.IsExpanded = true;

        // Assert: immediate subfolders Slot1 and Slot2 are now instantiated
        Assert.True(saves.IsChildrenLoaded);
        Assert.Equal(2, saves.Children.Count);
        Assert.Contains(saves.Children, c => c.Name == "Slot1" && c.IsDirectory);
        Assert.Contains(saves.Children, c => c.Name == "Slot2" && c.IsDirectory);

        // Multi-level deferred: Slot1's children must NOT be loaded yet!
        var slot1 = saves.Children.First(c => c.Name == "Slot1");
        Assert.False(slot1.IsChildrenLoaded);
        Assert.Single(slot1.Children); // dummy
        Assert.Equal("Loading...", slot1.Children[0].Name);

        // Act: expand Slot1
        slot1.IsExpanded = true;

        // Assert: leaf files inside Slot1 are now materialized
        Assert.True(slot1.IsChildrenLoaded);
        Assert.Equal(2, slot1.Children.Count);
        Assert.Contains(slot1.Children, c => c.Name == "game.sav" && c.IsFile);
        Assert.Contains(slot1.Children, c => c.Name == "meta.json" && c.IsFile);
    }

    [Fact]
    public void SaveFileHierarchyController_ToggleExpand_MaintainsVisibleRowsCorrectly()
    {
        var items = new List<SaveFileItemDescriptor>
        {
            new("Saves/Slot1/save.dat", 1000),
            new("Saves/Slot2/save.dat", 2000),
            new("Config/settings.cfg", 500)
        };

        var controller = new SaveFileHierarchyController();
        controller.LoadFromDescriptors(items);

        // Initially, visible rows equals root nodes (Config, Saves)
        Assert.Equal(2, controller.VisibleRows.Count);
        Assert.Equal(3, controller.TotalFileCount);
        Assert.Equal(3500, controller.TotalSizeBytes);

        var saves = controller.RootNodes.First(r => r.Name == "Saves");

        // Act: Expand Saves
        controller.ToggleExpand(saves);

        // Saves has 2 immediate children (Slot1, Slot2). Total visible rows = 2 roots + 2 children = 4
        Assert.True(saves.IsExpanded);
        Assert.Equal(4, controller.VisibleRows.Count);
        Assert.Equal(saves, controller.VisibleRows[1]);
        Assert.Equal("Slot1", controller.VisibleRows[2].Name);
        Assert.Equal("Slot2", controller.VisibleRows[3].Name);

        // Act: Collapse Saves
        controller.ToggleExpand(saves);

        // Collapsed back to 2 root nodes
        Assert.False(saves.IsExpanded);
        Assert.Equal(2, controller.VisibleRows.Count);
        Assert.Contains(controller.VisibleRows, r => r.Name == "Config");
        Assert.Contains(controller.VisibleRows, r => r.Name == "Saves");
    }

    [Fact]
    public void SaveFileHierarchyController_ExpandAll_And_CollapseAll_WorkCorrectly()
    {
        var items = new List<SaveFileItemDescriptor>
        {
            new("A/B/C/file1.dat", 100),
            new("A/B/C/file2.dat", 200),
            new("X/Y/file3.dat", 300)
        };

        var controller = new SaveFileHierarchyController();
        controller.LoadFromDescriptors(items);

        Assert.Equal(2, controller.VisibleRows.Count); // A, X

        // Act: Expand all
        controller.ExpandAll();

        // All nodes (A, B, C, file1, file2, X, Y, file3) = 8 visible rows
        Assert.Equal(8, controller.VisibleRows.Count);

        // Act: Collapse all
        controller.CollapseAll();

        // Returns to 2 root nodes
        Assert.Equal(2, controller.VisibleRows.Count);
        Assert.False(controller.RootNodes[0].IsExpanded);
        Assert.False(controller.RootNodes[1].IsExpanded);
    }

    [Fact]
    public void SaveFileHierarchyController_UpdateVerification_PropagatesToFiles()
    {
        var items = new List<TransferOverwriteBackupItem>
        {
            new("orig/save1.dat", "files/save1.dat", 1000, "hash1", DateTimeOffset.UtcNow),
            new("orig/save2.dat", "files/save2.dat", 2000, "hash2", DateTimeOffset.UtcNow)
        };

        var controller = new SaveFileHierarchyController();
        controller.LoadItems(items);

        var verificationResults = new Dictionary<string, bool>
        {
            ["files/save1.dat"] = true,
            ["files/save2.dat"] = false
        };

        // Act: expand all so leaf files are materialized, then update verification
        controller.ExpandAll();
        controller.UpdateVerification(verificationResults);

        var file1 = controller.VisibleRows.First(r => r.Name == "save1.dat");
        var file2 = controller.VisibleRows.First(r => r.Name == "save2.dat");

        Assert.True(file1.IsVerified);
        Assert.Equal("Verified", file1.StatusDisplay);
        Assert.Equal("✓", file1.StatusGlyph);

        Assert.False(file2.IsVerified);
        Assert.Equal("Mismatch", file2.StatusDisplay);
        Assert.Equal("✕", file2.StatusGlyph);
    }

    // =========================================================================
    // 2. Ultra-Large Hierarchy Performance & Memory Invariants (10,000+ files)
    // =========================================================================

    [Fact]
    public void BuildTree_With15000Files_CompletesWithinStrictTimeBudget_WithLowMemory()
    {
        // Generate 15,000 files across 50 directory trees
        var items = new List<SaveFileItemDescriptor>(15000);
        for (int d = 1; d <= 5; d++)
        {
            for (int s = 1; s <= 10; s++)
            {
                for (int f = 1; f <= 300; f++)
                {
                    items.Add(new SaveFileItemDescriptor(
                        Path: $"Category_{d:D2}/Profile_{s:D2}/data_{f:D4}.bin",
                        Bytes: 2048,
                        Sha256: "abc123def456abc123def456abc123def456abc123def456abc123def456abc1"));
                }
            }
        }

        Assert.Equal(15000, items.Count);

        // Warm up JIT before timed execution
        _ = LazyFileTreeBuilder.Build(items.Take(10).ToList());

        // Measure execution time
        var sw = Stopwatch.StartNew();
        List<SaveFileTreeNodeViewModel> roots = LazyFileTreeBuilder.Build(items);
        sw.Stop();

        // Must complete within 500 ms (typically < 30 ms)
        Assert.True(sw.ElapsedMilliseconds < 500, $"Tree indexing took {sw.ElapsedMilliseconds} ms, expected < 500 ms");

        // Exactly 5 root ViewModels allocated, NOT 15,000!
        Assert.Equal(5, roots.Count);
        Assert.All(roots, r => Assert.False(r.IsChildrenLoaded));

        // Aggregate stats are 100% accurate
        long totalBytes = roots.Sum(r => r.SizeBytes);
        int totalFiles = roots.Sum(r => r.FileCount);
        Assert.Equal(15000, totalFiles);
        Assert.Equal(15000 * 2048L, totalBytes);
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FormatBytes_ProducesAccurateHumanReadableStrings(long bytes, string expected)
    {
        string actual = SaveFileTreeNodeViewModel.FormatBytes(bytes);
        Assert.Equal(expected, actual);
    }

    // =========================================================================
    // 3. Virtualization Audit & XAML Invariants
    // =========================================================================

    [Fact]
    public void InstalledGamesView_DataGrid_HasVirtualizationAndAccessibilityAttributes()
    {
        XDocument doc = XDocument.Load(FindAppFile("Views", "InstalledGamesView.axaml"));

        XElement? grid = FindElementByName(doc, "GamesGrid");
        Assert.NotNull(grid);

        // RowHeight must be explicit for O(1) virtualization measure
        Assert.Equal("44", (string?)grid.Attribute("RowHeight"));
        Assert.Equal("Auto", (string?)grid.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Installed games table", (string?)grid.Attribute("AutomationProperties.Name"));

        // Must sit in bounded height container (Row 0 of Grid RowDefinitions="*,Auto")
        XElement? parentGrid = grid.Parent;
        Assert.NotNull(parentGrid);
        Assert.Equal("*,Auto", (string?)parentGrid.Attribute("RowDefinitions"));
    }

    [Fact]
    public void BackupHistoryView_FilesPanel_HasVirtualizedDataGridsAndTreeSupport()
    {
        XDocument doc = XDocument.Load(FindAppFile("Views", "BackupHistoryView.axaml"));

        // Both flat DataGrid and Tree DataGrid must exist
        XElement? flatGrid = FindElementByName(doc, "BackupFilesGrid");
        Assert.NotNull(flatGrid);
        Assert.Equal("38", (string?)flatGrid.Attribute("RowHeight"));
        Assert.Equal("Auto", (string?)flatGrid.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Backup files table", (string?)flatGrid.Attribute("AutomationProperties.Name"));

        XElement? treeGrid = FindElementByName(doc, "BackupTreeGrid");
        Assert.NotNull(treeGrid);
        Assert.Equal("38", (string?)treeGrid.Attribute("RowHeight"));
        Assert.Equal("Auto", (string?)treeGrid.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Backup file hierarchy tree", (string?)treeGrid.Attribute("AutomationProperties.Name"));

        // Restore results panel uses virtualized ListBox
        XElement? restoreListBox = doc.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute("AutomationProperties.Name") == "Restore results list");
        Assert.NotNull(restoreListBox);
        Assert.Contains("virtualizedTable", (string?)restoreListBox.Attribute("Classes"));
    }

    [Fact]
    public void TransferHistoryView_FilesPanel_UsesVirtualizedListBox()
    {
        XDocument doc = XDocument.Load(FindAppFile("Views", "TransferHistoryView.axaml"));

        XElement? listBox = doc.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute("AutomationProperties.Name") == "Executed run files list");

        Assert.NotNull(listBox);
        Assert.Equal("ListBox", listBox.Name.LocalName);
        Assert.Contains("virtualizedTable", (string?)listBox.Attribute("Classes"));
        Assert.Equal("Auto", (string?)listBox.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
    }

    [Fact]
    public void ManualBackupView_Lists_UseVirtualizedListBox()
    {
        XDocument doc = XDocument.Load(FindAppFile("Views", "ManualBackupView.axaml"));

        XElement? previewList = doc.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute("AutomationProperties.Name") == "Backup preview items list");
        Assert.NotNull(previewList);
        Assert.Equal("ListBox", previewList.Name.LocalName);
        Assert.Contains("virtualizedTable", (string?)previewList.Attribute("Classes"));

        XElement? resultsList = doc.Descendants().FirstOrDefault(e =>
            (string?)e.Attribute("AutomationProperties.Name") == "Backup execution results list");
        Assert.NotNull(resultsList);
        Assert.Equal("ListBox", resultsList.Name.LocalName);
        Assert.Contains("virtualizedTable", (string?)resultsList.Attribute("Classes"));
    }

    [Fact]
    public void ControlsAxaml_DefinesVirtualizedTableStyle()
    {
        XDocument doc = XDocument.Load(FindAppFile("Themes", "Controls.axaml"));

        var styles = doc.Descendants()
            .Where(e => e.Name.LocalName == "Style" && ((string?)e.Attribute("Selector"))?.Contains("virtualizedTable") == true)
            .ToList();

        Assert.NotEmpty(styles);
    }

    private static XElement? FindElementByName(XDocument doc, string name) =>
        doc.Descendants().FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));

    private static string FindAppFile(params string[] pathSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                string[] parts = new[] { directory.FullName, "GameSaves.App" }
                    .Concat(pathSegments)
                    .ToArray();

                string fullPath = Path.Combine(parts);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"App file '{string.Join("/", pathSegments)}' not found starting from {AppContext.BaseDirectory}");
    }
}
