using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GameSaves.Tests;

public sealed class TransferAndHistoryPaginationTests
{
    // =========================================================================
    // 1. SyncViewModel Pagination & Aggregate Counters
    // =========================================================================

    [Fact]
    public void SyncViewModel_Pagination_HasExpectedDefaults()
    {
        var vm = CreateSyncViewModel(new TestSyncProvider([]));

        Assert.NotNull(vm.Pagination);
        Assert.Equal("run", vm.Pagination.ItemName);
        Assert.Equal("runs", vm.Pagination.PluralItemName);
        Assert.Equal(20, vm.Pagination.PageSize);
        Assert.Equal("20", vm.Pagination.SelectedPageSizeOption);
        Assert.Empty(vm.Pagination.CurrentPageItems);
        Assert.Equal(0, vm.Pagination.TotalItemCount);
        Assert.Equal(1, vm.Pagination.TotalPages);
        Assert.Equal("Showing 0 of 0 runs", vm.Pagination.PageSummaryText);
    }

    [Fact]
    public async Task SyncViewModel_PlanPagination_PaginatesItemsCorrectly()
    {
        var items = Enumerable.Range(1, 25).Select(i => new SyncItem(
            RunName: $"run-{i:D2}",
            Action: SyncItemAction.UploadToRemote,
            ExistsLocally: true,
            ExistsRemotely: false,
            LocalPath: $"local/run-{i:D2}",
            RemotePath: $"remote/run-{i:D2}",
            GameName: $"Game {i}",
            FileCount: 2,
            TotalBytes: 1000,
            StatusText: "Ready")).ToArray();

        var provider = new TestSyncProvider(items);
        var vm = CreateSyncViewModel(provider);

        await vm.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(25, vm.Items.Count);
        Assert.Equal(25, vm.Pagination.TotalItemCount);
        Assert.Equal(2, vm.Pagination.TotalPages); // Default page size 20: 25 / 20 = 2 pages
        Assert.Equal(20, vm.Pagination.CurrentPageItems.Count);

        // Change page size to 10: 25 items -> 3 pages (10, 10, 5)
        vm.Pagination.SetPageSize(10);
        Assert.Equal(3, vm.Pagination.TotalPages);
        Assert.Equal(10, vm.Pagination.CurrentPageItems.Count);
        Assert.Equal("run-01", vm.Pagination.CurrentPageItems[0].RunName);
        Assert.Equal("run-10", vm.Pagination.CurrentPageItems[9].RunName);
        Assert.Equal("Showing 1–10 of 25 runs", vm.Pagination.PageSummaryText);

        // Page 2
        vm.Pagination.NextPage();
        Assert.Equal(2, vm.Pagination.CurrentPage);
        Assert.Equal(10, vm.Pagination.CurrentPageItems.Count);
        Assert.Equal("run-11", vm.Pagination.CurrentPageItems[0].RunName);
        Assert.Equal("run-20", vm.Pagination.CurrentPageItems[9].RunName);
        Assert.Equal("Showing 11–20 of 25 runs", vm.Pagination.PageSummaryText);

        // Page 3
        vm.Pagination.NextPage();
        Assert.Equal(3, vm.Pagination.CurrentPage);
        Assert.Equal(5, vm.Pagination.CurrentPageItems.Count);
        Assert.Equal("run-21", vm.Pagination.CurrentPageItems[0].RunName);
        Assert.Equal("run-25", vm.Pagination.CurrentPageItems[4].RunName);
        Assert.Equal("Showing 21–25 of 25 runs", vm.Pagination.PageSummaryText);
    }

    [Fact]
    public async Task SyncViewModel_AggregateCounters_ReflectEntirePlanNotJustVisiblePage()
    {
        // 25 items, each 1,000 bytes = 25,000 bytes total
        var items = Enumerable.Range(1, 25).Select(i => new SyncItem(
            RunName: $"run-{i:D2}",
            Action: SyncItemAction.UploadToRemote,
            ExistsLocally: true,
            ExistsRemotely: false,
            LocalPath: $"local/run-{i:D2}",
            RemotePath: $"remote/run-{i:D2}",
            GameName: $"Game {i}",
            FileCount: 1,
            TotalBytes: 1000,
            StatusText: "Upload")).ToArray();

        var provider = new TestSyncProvider(items);
        var vm = CreateSyncViewModel(provider);

        await vm.PreviewSyncCommand.ExecuteAsync(null);

        // Set page size to 5 so we have 5 pages
        vm.Pagination.SetPageSize(5);
        Assert.Equal(5, vm.Pagination.TotalPages);

        // Aggregate counters MUST reflect all 25 items, not just the 5 on current page!
        Assert.Contains("Upload: 25 run(s)", vm.SummaryDisplay);
        Assert.Contains("Selected for sync: 25 of 25 run(s)", vm.SelectedSummaryDisplay);

        // Switch to page 2 and uncheck 1 item
        vm.Pagination.GoToPage(2);
        Assert.Equal(5, vm.Pagination.CurrentPageItems.Count);
        vm.Pagination.CurrentPageItems[0].IncludeInSync = false;

        // Counter must immediately update to 24 of 25 runs
        Assert.Contains("Selected for sync: 24 of 25 run(s)", vm.SelectedSummaryDisplay);

        // Navigate back to page 1: counter must STILL reflect 24 of 25 runs
        vm.Pagination.GoToPage(1);
        Assert.Contains("Selected for sync: 24 of 25 run(s)", vm.SelectedSummaryDisplay);

        // Select All updates all items across all pages
        vm.SelectAllRunsCommand.Execute(null);
        Assert.Contains("Selected for sync: 25 of 25 run(s)", vm.SelectedSummaryDisplay);

        // Deselect All updates all items across all pages
        vm.DeselectAllRunsCommand.Execute(null);
        Assert.False(vm.HasSelectedRuns);
    }

    [Fact]
    public async Task SyncViewModel_SelectionStatePreservedAcrossPageSwitches()
    {
        var items = Enumerable.Range(1, 20).Select(i => new SyncItem(
            RunName: $"run-{i:D2}",
            Action: SyncItemAction.UploadToRemote,
            ExistsLocally: true,
            ExistsRemotely: false,
            LocalPath: $"local/run-{i:D2}",
            RemotePath: $"remote/run-{i:D2}",
            GameName: $"Game {i}",
            FileCount: 1,
            TotalBytes: 500,
            StatusText: "Upload")).ToArray();

        var provider = new TestSyncProvider(items);
        var vm = CreateSyncViewModel(provider);

        await vm.PreviewSyncCommand.ExecuteAsync(null);
        vm.Pagination.SetPageSize(5); // 4 pages

        // Page 1: uncheck item 0 and item 2
        vm.Pagination.CurrentPageItems[0].IncludeInSync = false;
        vm.Pagination.CurrentPageItems[2].IncludeInSync = false;

        // Navigate away to page 2, 3, 4
        vm.Pagination.GoToPage(2);
        vm.Pagination.GoToPage(3);
        vm.Pagination.GoToPage(4);

        // Navigate back to page 1
        vm.Pagination.GoToPage(1);
        Assert.False(vm.Pagination.CurrentPageItems[0].IncludeInSync);
        Assert.True(vm.Pagination.CurrentPageItems[1].IncludeInSync);
        Assert.False(vm.Pagination.CurrentPageItems[2].IncludeInSync);
        Assert.True(vm.Pagination.CurrentPageItems[3].IncludeInSync);
        Assert.True(vm.Pagination.CurrentPageItems[4].IncludeInSync);
    }

    // =========================================================================
    // 2. BackupHistoryViewModel Pagination & Selection Preservation
    // =========================================================================

    [Fact]
    public void BackupHistoryViewModel_Pagination_HasExpectedDefaults()
    {
        var vm = CreateBackupHistoryViewModel(new FakeBackupHistoryService([]));

        Assert.NotNull(vm.Pagination);
        Assert.Equal("backup", vm.Pagination.ItemName);
        Assert.Equal("backups", vm.Pagination.PluralItemName);
        Assert.Equal(20, vm.Pagination.PageSize);
        Assert.Equal(0, vm.Pagination.TotalItemCount);
        Assert.Equal(1, vm.Pagination.TotalPages);
        Assert.Equal("Showing 0 of 0 backups", vm.Pagination.PageSummaryText);
    }

    [Fact]
    public async Task BackupHistoryViewModel_Pagination_PaginatesRunsAndPreservesSelection()
    {
        var runs = Enumerable.Range(1, 35).Select(i => CreateBackupRun($"run-{i:D2}", $"Game {i}")).ToList();
        var historyService = new FakeBackupHistoryService(runs);
        var vm = CreateBackupHistoryViewModel(historyService);

        await vm.InitializeAsync();

        Assert.Equal(35, vm.Runs.Count);
        Assert.Equal(35, vm.Pagination.TotalItemCount);

        // Set page size to 10 -> 4 pages (10, 10, 10, 5)
        vm.Pagination.SetPageSize(10);
        Assert.Equal(4, vm.Pagination.TotalPages);
        Assert.Equal(10, vm.Pagination.CurrentPageItems.Count);

        // Select a run on page 1
        var selectedOnPage1 = vm.Pagination.CurrentPageItems[3]; // run-04
        vm.SelectedRun = selectedOnPage1;

        Assert.Same(selectedOnPage1, vm.SelectedRun);
        Assert.NotEmpty(vm.RunItems);

        // Switch to page 2: the item is no longer in CurrentPageItems, but SelectedRun MUST NOT be coerced to null
        vm.Pagination.NextPage();
        Assert.Equal(2, vm.Pagination.CurrentPage);
        Assert.DoesNotContain(selectedOnPage1, vm.Pagination.CurrentPageItems);
        Assert.Same(selectedOnPage1, vm.SelectedRun);
        Assert.NotEmpty(vm.RunItems);

        // Switch to page 3, 4
        vm.Pagination.NextPage();
        Assert.Same(selectedOnPage1, vm.SelectedRun);
        vm.Pagination.NextPage();
        Assert.Same(selectedOnPage1, vm.SelectedRun);

        // Switch back to page 1
        vm.Pagination.FirstPage();
        Assert.Equal(1, vm.Pagination.CurrentPage);
        Assert.Contains(selectedOnPage1, vm.Pagination.CurrentPageItems);
        Assert.Same(selectedOnPage1, vm.SelectedRun);
        Assert.NotEmpty(vm.RunItems);
    }

    [Fact]
    public async Task BackupHistoryViewModel_CustomAndPresetPageSizes_WorkCorrectly()
    {
        var runs = Enumerable.Range(1, 25).Select(i => CreateBackupRun($"run-{i:D2}", $"Game {i}")).ToList();
        var historyService = new FakeBackupHistoryService(runs);
        var vm = CreateBackupHistoryViewModel(historyService);

        await vm.InitializeAsync();

        // Preset sizes check
        foreach (string preset in PaginationController<BackupRunRowViewModel>.PresetPageSizeOptions)
        {
            Assert.Contains(preset, vm.Pagination.PageSizeOptions);
        }

        // Custom page size
        vm.Pagination.SelectedPageSizeOption = "Custom";
        Assert.True(vm.Pagination.IsCustomPageSize);

        vm.Pagination.CustomPageSizeText = "7";
        Assert.Equal(7, vm.Pagination.PageSize);
        Assert.Equal(4, vm.Pagination.TotalPages); // 25 / 7 = 4 pages (7, 7, 7, 4)
        Assert.Equal(7, vm.Pagination.CurrentPageItems.Count);

        // Switch back to preset 15
        vm.Pagination.SelectedPageSizeOption = "15";
        Assert.False(vm.Pagination.IsCustomPageSize);
        Assert.Equal(15, vm.Pagination.PageSize);
        Assert.Equal(2, vm.Pagination.TotalPages);
    }

    // =========================================================================
    // 3. TransferHistoryViewModel Pagination & Selection Preservation
    // =========================================================================

    [Fact]
    public void TransferHistoryViewModel_Pagination_HasExpectedDefaults()
    {
        var vm = CreateTransferHistoryViewModel(new FakeTransferHistoryRepository([]));

        Assert.NotNull(vm.Pagination);
        Assert.Equal("run", vm.Pagination.ItemName);
        Assert.Equal("runs", vm.Pagination.PluralItemName);
        Assert.Equal(20, vm.Pagination.PageSize);
        Assert.Equal(0, vm.Pagination.TotalItemCount);
        Assert.Equal(1, vm.Pagination.TotalPages);
        Assert.Equal("Showing 0 of 0 runs", vm.Pagination.PageSummaryText);
    }

    [Fact]
    public async Task TransferHistoryViewModel_Pagination_PaginatesRunsAndPreservesSelection()
    {
        var runs = Enumerable.Range(1, 45).Select(i => CreateTransferRun(i, $"Game {i}")).ToList();
        var repo = new FakeTransferHistoryRepository(runs);
        var vm = CreateTransferHistoryViewModel(repo);

        await vm.InitializeAsync();

        Assert.Equal(45, vm.Runs.Count);
        Assert.Equal(45, vm.Pagination.TotalItemCount);

        // Page size 15 -> 3 pages
        vm.Pagination.SetPageSize(15);
        Assert.Equal(3, vm.Pagination.TotalPages);
        Assert.Equal(15, vm.Pagination.CurrentPageItems.Count);

        // Select a run on page 1
        var selectedOnPage1 = vm.Pagination.CurrentPageItems[5];
        vm.SelectedRun = selectedOnPage1;
        Assert.Same(selectedOnPage1, vm.SelectedRun);

        // Switch to page 2
        vm.Pagination.NextPage();
        Assert.Equal(2, vm.Pagination.CurrentPage);
        Assert.DoesNotContain(selectedOnPage1, vm.Pagination.CurrentPageItems);
        Assert.Same(selectedOnPage1, vm.SelectedRun); // Preserved!

        // Switch to page 3
        vm.Pagination.NextPage();
        Assert.Equal(3, vm.Pagination.CurrentPage);
        Assert.Same(selectedOnPage1, vm.SelectedRun); // Preserved!

        // Switch back to page 1
        vm.Pagination.FirstPage();
        Assert.Equal(1, vm.Pagination.CurrentPage);
        Assert.Same(selectedOnPage1, vm.SelectedRun);
    }

    // =========================================================================
    // 4. XAML Structural Verification
    // =========================================================================

    [Theory]
    [InlineData("Manager/GameSaves.App/Views/SyncView.axaml")]
    [InlineData("Manager/GameSaves.App/Views/BackupHistoryView.axaml")]
    [InlineData("Manager/GameSaves.App/Views/TransferHistoryView.axaml")]
    public void Views_ContainPaginationBindings_WithoutDirectHeightOverrides(string relativePath)
    {
        string fullPath = Path.Combine(GetSolutionRoot(), relativePath);
        Assert.True(File.Exists(fullPath), $"File not found: {fullPath}");

        string content = File.ReadAllText(fullPath);

        // Must bind items to Pagination.CurrentPageItems
        Assert.Contains("ItemsSource=\"{Binding Pagination.CurrentPageItems}\"", content);

        // Must contain pagination controls
        Assert.Contains("{Binding Pagination.PageSummaryText}", content);
        Assert.Contains("{Binding Pagination.PageSizeOptions}", content);
        Assert.Contains("{Binding Pagination.SelectedPageSizeOption}", content);
        Assert.Contains("{Binding Pagination.FirstPageCommand}", content);
        Assert.Contains("{Binding Pagination.PreviousPageCommand}", content);
        Assert.Contains("{Binding Pagination.PageNumberText}", content);
        Assert.Contains("{Binding Pagination.NextPageCommand}", content);
        Assert.Contains("{Binding Pagination.LastPageCommand}", content);

        // Must not contain explicit Height="32" on the pagination controls
        Assert.DoesNotContain("Command=\"{Binding Pagination.FirstPageCommand}\"\r\n                                            Height=", content);
        Assert.DoesNotContain("Command=\"{Binding Pagination.FirstPageCommand}\"\n                                            Height=", content);
    }

    // =========================================================================
    // Helper Methods & Test Doubles
    // =========================================================================

    private static string GetSolutionRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Manager.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "Manager", "Manager.sln")))
            {
                return File.Exists(Path.Combine(dir.FullName, "Manager.sln"))
                    ? dir.Parent!.FullName
                    : dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static SyncViewModel CreateSyncViewModel(ISyncProvider provider)
    {
        var factory = new TestSyncProviderFactory(provider);
        var settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.LocalFolder,
            LocalFolderPath = "C:\\Backups\\LocalTarget",
        };

        var repo = new InMemorySyncRemoteProfileRepository();

        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repo,
            new SyncRemoteProfileService(repo, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.Parse("2026-09-01T12:00:00Z")),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());
    }

    private static BackupHistoryViewModel CreateBackupHistoryViewModel(IBackupHistoryService historyService)
    {
        var restore = new FakeBackupRestoreService();
        var cleanup = new FakeBackupCleanupService();
        var archive = new FakeBackupArchiveService();

        return new BackupHistoryViewModel(
            historyService,
            restore,
            cleanup,
            archive,
            new FakeFolderPickerService(),
            new ProfilesViewModel(new EmptySteamDiscoveryService(), new FakeSteamProfileDetector(), SyncProviderSelectionTests.NewWorkspaceLayout()),
            SyncProviderSelectionTests.NewWorkspaceLayout());
    }

    private static TransferHistoryViewModel CreateTransferHistoryViewModel(ITransferHistoryRepository repo)
    {
        return new TransferHistoryViewModel(repo, SyncProviderSelectionTests.NewWorkspaceLayout());
    }

    private static TransferBackupRunInfo CreateBackupRun(string runName, string gameName)
    {
        var item = new TransferOverwriteBackupItem(
            OriginalFile: "C:\\Saves\\save.dat",
            BackupFile: "save.dat",
            Bytes: 1024,
            Sha256: "abcd",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "save.dat");

        var manifest = new TransferBackupManifest(
            SchemaVersion: 1,
            Kind: OverwriteBackupContext.ManualKind,
            Game: gameName,
            SteamAppId: "12345",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedUtc: DateTimeOffset.UtcNow.AddMinutes(-9),
            FileCount: 1,
            TotalBytes: 1024,
            Items: [item]);

        return new TransferBackupRunInfo(
            BackupRootPath: $"C:\\Backups\\{runName}",
            ManifestPath: $"C:\\Backups\\{runName}\\manifest.json",
            Manifest: manifest);
    }

    private static TransferRunInfo CreateTransferRun(long id, string gameName)
    {
        return new TransferRunInfo(
            Id: id,
            Kind: TransferRunKind.TransferCopy,
            GameName: gameName,
            SteamAppId: $"{1000 + id}",
            SourceAccountId: "source-acc",
            TargetAccountId: "target-acc",
            DryRun: false,
            OverwriteEnabled: false,
            BackupEnabled: true,
            FilesConsidered: 1,
            FilesCopied: 1,
            FilesSkipped: 0,
            FilesFailed: 0,
            BytesCopied: 2048,
            FilesBackedUp: 0,
            BackupRootPath: null,
            BlockedReason: null,
            StartedUtc: DateTimeOffset.UtcNow.AddHours(-id),
            CompletedUtc: DateTimeOffset.UtcNow.AddHours(-id).AddSeconds(5));
    }

    private sealed class TestSyncProviderFactory(ISyncProvider provider) : ISyncProviderFactory
    {
        public ISyncProvider CreateLocalFolderProvider(string localRootPath) => provider;
        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) => provider;
        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) => provider;
        public void ForgetSftpHostKey(string host, int port) { }
    }

    private sealed class TestSyncProvider(IReadOnlyList<SyncItem> items) : ISyncProvider
    {
        public string ProviderName => "Test Provider";
        public string RemoteRoot => "C:\\Remote";

        public Task<SyncPlan> CreatePreviewAsync(SyncOptions options, CancellationToken cancellationToken = default)
        {
            long bytes = items.Sum(i => i.TotalBytes);
            int count = items.Count;

            return Task.FromResult(new SyncPlan(
                ProviderName: ProviderName,
                RemoteRoot: RemoteRoot,
                Items: items,
                Warnings: [],
                CanExecute: count > 0,
                UploadCount: count,
                DownloadCount: 0,
                InSyncCount: 0,
                ConflictCount: 0,
                BytesToUpload: bytes,
                BytesToDownload: 0));
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncResult(
                plan,
                DryRun: false,
                Uploaded: 0,
                Downloaded: 0,
                Skipped: 0,
                BytesCopied: 0,
                Items: [],
                Warnings: []));
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>([]);

        public void Dispose() { }
    }

    private sealed class FakeBackupHistoryService(IReadOnlyList<TransferBackupRunInfo> runs) : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(runs);

        public Task<TransferBackupRunInfo?> GetRunAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(runs.FirstOrDefault(r => Path.GetFileName(r.BackupRootPath) == runId));

        public string GetBackupBasePath() => Path.Combine(Path.GetTempPath(), "test-backups");
    }

    private sealed class FakeBackupRestoreService : IBackupRestoreService
    {
        public Task<BackupRestoreResult> RestoreAsync(
            TransferBackupRunInfo run,
            BackupRestoreOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupRestoreResult(run, options.DryRun, 0, 0, 0, 0, [], []));

        public Task<IReadOnlyList<RestoreMappingTargetOption>> GetApprovedMappingTargetsAsync(
            TransferBackupRunInfo run,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RestoreMappingTargetOption>>([]);
    }

    private sealed class FakeBackupCleanupService : IBackupCleanupService
    {
        public Task<BackupCleanupResult> CleanupAsync(
            BackupCleanupOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupCleanupResult(options.DryRun, 0, 0, 0, 0, [], []));

        public Task<BackupCleanupResult> DeleteRunAsync(
            TransferBackupRunInfo run,
            bool confirmExecution,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupCleanupResult(false, 1, 1, 0, 0, [], []));
    }

    private sealed class FakeBackupArchiveService : IBackupArchiveService
    {
        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<BackupArchiveExportResult> ExportRunAsync(
            TransferBackupRunInfo run,
            string destinationFolder,
            BackupContainerFormat format = BackupContainerFormat.Zip,
            BackupCompressionPreset preset = BackupCompressionPreset.Optimal,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BackupArchiveImportResult> ImportArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startLocation = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeSteamProfileDetector : ISteamProfileDetector
    {
        public IReadOnlyList<SteamProfile> DetectProfiles(SteamDiscoveryResult discovery, CancellationToken cancellationToken = default) =>
            [];

        public IReadOnlyList<SteamProfile> DetectProfiles(string steamRoot, CancellationToken cancellationToken = default) =>
            [];
    }

    private sealed class FakeTransferHistoryRepository(IReadOnlyList<TransferRunInfo> runs) : ITransferHistoryRepository
    {
        public long RecordRun(TransferRunRecord record) => 1;

        public IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit) =>
            runs.Take(limit).ToList();

        public IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId) => [];

        public int CountRuns() => runs.Count;

        public void PurgeOlderThan(DateTimeOffset cutoffUtc) { }
    }
}
