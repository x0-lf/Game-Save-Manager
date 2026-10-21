using GameSaves.App.Models;
using GameSaves.App.ViewModels;
using GameSaves.Core.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// DIFF-001: the Backups page compares the selected run with a baseline run
/// through the manifest comparison service. Everything is in memory; the
/// restore, cleanup and archive services are fakes that fail if touched.
/// </summary>
public sealed class BackupDifferenceViewerTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Compare_ShowsEveryClassInWords_HidesIdenticalUntilAsked_AndTouchesNoService()
    {
        var restore = new ViewModelStartupTests.RecordingBackupRestoreService();
        var cleanup = new ViewModelStartupTests.RecordingBackupCleanupService();
        var archive = new ViewModelStartupTests.RecordingBackupArchiveService();
        BackupHistoryViewModel viewModel = await ViewModelAsync(restore, cleanup, archive,
            Run("selected", Item("files/same.sav", "AAAA", 10), Item("files/changed.sav", "BBBB", 25), Item("files/added.sav", "CCCC", 40)),
            Run("baseline", Item("files/same.sav", "AAAA", 10), Item("files/changed.sav", "AAAA", 20), Item("files/removed.sav", "DDDD", 30)));

        viewModel.SelectedRun = viewModel.Runs[0];
        viewModel.ComparisonBaseline = viewModel.Runs[1];
        await viewModel.CompareRunsCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Added", "Changed", "Removed"],
            viewModel.ComparisonRows.Select(row => row.KindText));
        Assert.Equal("Added 1 (40 B), removed 1 (30 B), changed 1 (25 B), identical 1 (10 B).", viewModel.ComparisonSummary);

        BackupDifferenceRowViewModel changed = viewModel.ComparisonRows[1];
        Assert.Equal("files/changed.sav", changed.RelativePath);
        Assert.Equal("20 B", changed.BaselineSizeDisplay);
        Assert.Equal("25 B", changed.SelectedSizeDisplay);
        Assert.Equal("Changed: files/changed.sav. Baseline 20 B, selected run 25 B.", changed.AccessibleName);
        Assert.Equal("—", viewModel.ComparisonRows[0].BaselineSizeDisplay);

        viewModel.ShowIdentical = true;
        Assert.Equal(4, viewModel.ComparisonRows.Count);
        Assert.Contains(viewModel.ComparisonRows, row => row.KindText == "Identical" && row.RelativePath == "files/same.sav");

        viewModel.ShowAdded = false;
        viewModel.ShowChanged = false;
        Assert.Equal(["Removed", "Identical"], viewModel.ComparisonRows.Select(row => row.KindText));

        // Comparing reads manifests only.
        Assert.False(restore.RestoreWasCalled);
        Assert.False(cleanup.CleanupWasCalled);
        Assert.False(archive.ExportWasCalled);
    }

    [Fact]
    public async Task Compare_NeedsASelectedRun_AndADifferentBaseline()
    {
        BackupHistoryViewModel viewModel = await ViewModelAsync(
            Run("one", Item("files/a.sav", "AAAA", 1)),
            Run("two", Item("files/a.sav", "AAAA", 1)));

        await viewModel.CompareRunsCommand.ExecuteAsync(null);
        Assert.Contains("Select a backup run first", viewModel.ComparisonStatusMessage);

        viewModel.SelectedRun = viewModel.Runs[0];
        await viewModel.CompareRunsCommand.ExecuteAsync(null);
        Assert.Contains("Pick a baseline run", viewModel.ComparisonStatusMessage);

        viewModel.ComparisonBaseline = viewModel.Runs[0];
        await viewModel.CompareRunsCommand.ExecuteAsync(null);
        Assert.Contains("Pick a different run", viewModel.ComparisonStatusMessage);
        Assert.Empty(viewModel.ComparisonRows);
    }

    [Fact]
    public async Task Compare_ShowsARefusalWithItsReason_AndNoRows()
    {
        BackupHistoryViewModel viewModel = await ViewModelAsync(
            Run("selected", Item("files/a.sav", "AAAA", 1)),
            Run("baseline", Item("files/../escape.sav", "AAAA", 1)));

        viewModel.SelectedRun = viewModel.Runs[0];
        viewModel.ComparisonBaseline = viewModel.Runs[1];
        await viewModel.CompareRunsCommand.ExecuteAsync(null);

        Assert.StartsWith("Comparison refused: The baseline manifest lists an unsafe payload path", viewModel.ComparisonStatusMessage);
        Assert.Empty(viewModel.ComparisonRows);
        Assert.Equal("", viewModel.ComparisonSummary);
    }

    [Fact]
    public async Task AComparison_IsClearedWhenTheSelectedRunChanges_AndTheBaselineWhenRunsRefresh()
    {
        BackupHistoryViewModel viewModel = await ViewModelAsync(
            Run("one", Item("files/a.sav", "AAAA", 1)),
            Run("two", Item("files/b.sav", "BBBB", 2)),
            Run("three", Item("files/c.sav", "CCCC", 3)));

        viewModel.SelectedRun = viewModel.Runs[0];
        viewModel.ComparisonBaseline = viewModel.Runs[1];
        await viewModel.CompareRunsCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.ComparisonRows.Count);

        viewModel.SelectedRun = viewModel.Runs[2];

        Assert.Empty(viewModel.ComparisonRows);
        Assert.Equal("", viewModel.ComparisonSummary);
        Assert.Contains("Pick a baseline run", viewModel.ComparisonStatusMessage);

        await viewModel.RefreshRunsCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ComparisonBaseline);
    }

    // ---- helpers ----

    private static TransferOverwriteBackupItem Item(string relativePath, string sha256, long bytes) => new(
        OriginalFile: "C:/Saves/" + relativePath,
        BackupFile: "D:/Backups/run/" + relativePath,
        Bytes: bytes,
        Sha256: sha256,
        BackedUpUtc: When,
        RelativePath: relativePath);

    private static TransferBackupRunInfo Run(string name, params TransferOverwriteBackupItem[] items) => new(
        BackupRootPath: $@"D:\Backups\{name}",
        ManifestPath: $@"D:\Backups\{name}\manifest.json",
        Manifest: new TransferBackupManifest(
            SchemaVersion: TransferBackupManifest.CurrentSchemaVersion,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Test Game",
            SteamAppId: "1234",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: When,
            CompletedUtc: When.AddSeconds(1),
            FileCount: items.Length,
            TotalBytes: items.Sum(item => item.Bytes),
            Items: items));

    private static Task<BackupHistoryViewModel> ViewModelAsync(params TransferBackupRunInfo[] runs) =>
        ViewModelAsync(
            new ViewModelStartupTests.RecordingBackupRestoreService(),
            new ViewModelStartupTests.RecordingBackupCleanupService(),
            new ViewModelStartupTests.RecordingBackupArchiveService(),
            runs);

    private static async Task<BackupHistoryViewModel> ViewModelAsync(
        IBackupRestoreService restore,
        IBackupCleanupService cleanup,
        IBackupArchiveService archive,
        params TransferBackupRunInfo[] runs)
    {
        var viewModel = new BackupHistoryViewModel(
            new FixedRunsBackupHistoryService(runs),
            restore,
            cleanup,
            archive,
            new ViewModelStartupTests.NullFolderPicker(),
            new ProfilesViewModel(
                new EmptySteamDiscoveryService(),
                new ViewModelStartupTests.FakeSteamProfileDetector(),
                SyncProviderSelectionTests.NewWorkspaceLayout()),
            SyncProviderSelectionTests.NewWorkspaceLayout());

        await viewModel.InitializeAsync();
        return viewModel;
    }

    private sealed class FixedRunsBackupHistoryService(IReadOnlyList<TransferBackupRunInfo> runs) : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(runs);

        public string GetBackupBasePath() => @"D:\Backups";
    }
}
