using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Platform;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// AUDIT-001 tests independently verifying and hardening security audit remediations:
/// 1. Google Drive archive container capability alignment and explicit downgrade UX.
/// 2. Restore destination confinement against sensitive system, startup, root, and Program Files directories.
/// 3. Purging of stale temporary working directories (.staging, .export, .download).
/// </summary>
public sealed class AuditRemediationTests
{
    [Fact]
    public async Task Preview_WhenLocalRunIsContainer_AndRemoteCannotStoreContainers_WarnsAndMarksItem()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string basePath = backupHistory.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string runRoot = Path.Combine(basePath, "container-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "data");

        // Export as container and delete folder so it's a standalone archive run
        var archiveService = new BackupArchiveService(backupHistory);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(run, basePath);
        Assert.True(export.Success, export.Message);
        Directory.Delete(runRoot, recursive: true);

        // Remote that does NOT support archive containers (like Google Drive)
        var remote = new TestArchiveRemoteFileSystem { SupportsArchiveContainers = false };
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Upload = true });

        Assert.Contains(plan.Warnings, w => w.Code == "LocalContainerUnsupported");
        SyncItem item = Assert.Single(plan.Items);
        Assert.Contains("Cannot upload: remote location does not support archive containers", item.StatusText);

        // Executing upload should fail safely with clear guidance
        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Upload = true });

        SyncItemResult itemResult = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, itemResult.Status);
        Assert.Contains("cannot store containers", itemResult.Error);
    }

    [Fact]
    public async Task Download_WhenRemoteRunIsContainer_AndRemoteCannotStoreContainers_FailsSafelyWithGuidance()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        var remote = new TestArchiveRemoteFileSystem { SupportsArchiveContainers = false };
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        // A container item scheduled for download on a non-container remote
        var containerItem = new SyncItem(
            RunName: "remote-archive",
            Action: SyncItemAction.DownloadToLocal,
            ExistsLocally: false,
            ExistsRemotely: true,
            LocalPath: Path.Combine(backupHistory.GetBackupBasePath(), "remote-archive"),
            RemotePath: "remote-archive.7z",
            GameName: "Test Game",
            FileCount: 1,
            TotalBytes: 100,
            StatusText: "Download container");

        var plan = new SyncPlan(
            ProviderName: "Remote",
            RemoteRoot: "test://remote",
            Items: new[] { containerItem },
            Warnings: Array.Empty<TransferPreviewWarning>(),
            CanExecute: true,
            UploadCount: 0,
            DownloadCount: 1,
            InSyncCount: 0,
            ConflictCount: 0,
            BytesToUpload: 0,
            BytesToDownload: 100);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Download = true });

        SyncItemResult itemResult = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, itemResult.Status);
        Assert.Contains("does not support container downloads", itemResult.Error);
    }

    [Fact]
    public async Task RestoreAsync_WhenManifestTargetsWindowsSystemDirectory_RejectsItem()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run");

        // Create a backup run whose manifest declares a target path in Windows System32
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            runRoot,
            @"C:\Windows\System32\evil.dll",
            "payload");

        var service = new BackupRestoreService(
            new TransferOverwriteBackupService(
                new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult result = await service.RestoreAsync(
            run,
            new BackupRestoreOptions { DryRun = false, ConfirmExecution = true, VerifyHashes = true });

        Assert.Equal(0, result.FilesRestored);
        BackupRestoreItemResult item = Assert.Single(result.Items);
        Assert.Equal(BackupRestoreItemStatus.Failed, item.Status);
        Assert.Contains("Windows system directory", item.Error);
    }

    [Fact]
    public async Task RestoreAsync_WhenManifestTargetsStartupFolder_RejectsItem()
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startup))
            return;

        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run");

        TransferBackupRunInfo run = TestData.CreateBackupRun(
            runRoot,
            Path.Combine(startup, "payload.bat"),
            "payload");

        var service = new BackupRestoreService(
            new TransferOverwriteBackupService(
                new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult result = await service.RestoreAsync(
            run,
            new BackupRestoreOptions { DryRun = false, ConfirmExecution = true, VerifyHashes = true });

        Assert.Equal(0, result.FilesRestored);
        BackupRestoreItemResult item = Assert.Single(result.Items);
        Assert.Equal(BackupRestoreItemStatus.Failed, item.Status);
        Assert.Contains("Startup", item.Error);
    }

    [Fact]
    public void PurgeStaleWorkingDirectories_PurgesOldOrphansAndPreservesRecentOrphansAndRuns()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var history = new BackupHistoryService(pathProvider);
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        // 1. Stale temporary directories (> 1 hour old)
        string staleStaging = Path.Combine(basePath, ".staging_stale1");
        string staleExport = Path.Combine(basePath, ".export_stale2");
        string staleDownload = Path.Combine(basePath, ".download_stale3");
        string staleExportFile = Path.Combine(basePath, ".export_stale4.tmp");

        Directory.CreateDirectory(staleStaging);
        Directory.CreateDirectory(staleExport);
        Directory.CreateDirectory(staleDownload);
        File.WriteAllText(staleExportFile, "stale tmp");

        DateTime oldTime = DateTime.UtcNow.AddHours(-3);
        Directory.SetLastWriteTimeUtc(staleStaging, oldTime);
        Directory.SetLastWriteTimeUtc(staleExport, oldTime);
        Directory.SetLastWriteTimeUtc(staleDownload, oldTime);
        File.SetLastWriteTimeUtc(staleExportFile, oldTime);

        // 2. Fresh temporary directories (< 5 minutes old)
        string freshStaging = Path.Combine(basePath, ".staging_fresh1");
        string freshDownload = Path.Combine(basePath, ".download_fresh2");
        Directory.CreateDirectory(freshStaging);
        Directory.CreateDirectory(freshDownload);

        // 3. Legitimate backup run folder
        string validRunDir = Path.Combine(basePath, "2026-09-15_12-00-00_manual");
        TestData.CreateBackupRun(validRunDir, temp.GetPath("orig.sav"), "legit save data");

        // Run purge with 1 hour age
        history.PurgeStaleWorkingDirectories(TimeSpan.FromHours(1));

        // Stale entries should be deleted
        Assert.False(Directory.Exists(staleStaging));
        Assert.False(Directory.Exists(staleExport));
        Assert.False(Directory.Exists(staleDownload));
        Assert.False(File.Exists(staleExportFile));

        // Fresh entries and legitimate runs must be preserved
        Assert.True(Directory.Exists(freshStaging));
        Assert.True(Directory.Exists(freshDownload));
        Assert.True(Directory.Exists(validRunDir));
    }

    [Fact]
    public void SyncViewModel_ExposesArchiveSyncCapabilitiesAndNotice()
    {
        var settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.LocalFolder
        };
        var repository = new InMemorySyncRemoteProfileRepository();
        var vm = new SyncViewModel(
            new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.UtcNow),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());

        // Default LocalFolder supports archive containers
        vm.SelectedProviderKind = SyncProviderKind.LocalFolder;
        Assert.True(vm.SelectedProviderSupportsArchiveContainers);
        Assert.False(vm.ShowArchiveSyncNotice);
        Assert.Null(vm.ArchiveSyncNotice);
        Assert.Contains(".7z archive container", vm.ArchiveSyncTooltip);

        // Google Drive operates on loose-file sync
        vm.SelectedProviderKind = SyncProviderKind.GoogleDrive;
        Assert.False(vm.SelectedProviderSupportsArchiveContainers);
        Assert.Contains("loose-file", vm.ArchiveSyncTooltip);

        vm.ArchiveSync = false;
        Assert.False(vm.ShowArchiveSyncNotice);

        vm.ArchiveSync = true;
        Assert.True(vm.ShowArchiveSyncNotice);
        Assert.NotNull(vm.ArchiveSyncNotice);
        Assert.Contains("loose-file sync", vm.ArchiveSyncNotice);
    }

    private sealed class TestArchiveRemoteFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, string> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BinaryFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadedFiles { get; } = new();
        public List<string> DownloadedFiles { get; } = new();
        public List<string> ArchiveNames { get; } = new();
        public List<string> RunFolderNamesList { get; } = new();

        public string DisplayRoot => "test://remote";

        public bool SupportsArchiveContainers { get; set; } = true;

        public string GetDisplayPath(string relativePath) => $"{DisplayRoot}/{relativePath}";

        public Task<TransferPreviewWarning?> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(RunFolderNamesList);

        public Task<IReadOnlyList<string>> ListRunArchiveNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(ArchiveNames);

        public Task<bool> FolderExistsAsync(string relativeFolder, CancellationToken cancellationToken = default) =>
            Task.FromResult(RunFolderNamesList.Contains(relativeFolder));

        public Task<bool> FileExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(BinaryFiles.ContainsKey(relativePath) || TextFiles.ContainsKey(relativePath));

        public Task<string?> ReadTextFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(TextFiles.TryGetValue(relativePath, out string? text) ? text : null);

        public Task CreateTextFileIfMissingAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            TextFiles[relativePath] = content;
            return Task.CompletedTask;
        }

        public Task<string?> ReadProviderMetadataAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task ReplaceProviderMetadataAsync(string relativePath, string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeFolder, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<long> UploadFileAsync(string localFilePath, string relativeRemotePath, CancellationToken cancellationToken = default)
        {
            UploadedFiles.Add(relativeRemotePath);
            byte[] bytes = File.ReadAllBytes(localFilePath);
            BinaryFiles[relativeRemotePath] = bytes;
            return Task.FromResult((long)bytes.Length);
        }

        public Task<long> DownloadFileAsync(string relativeRemotePath, string localFilePath, CancellationToken cancellationToken = default)
        {
            DownloadedFiles.Add(relativeRemotePath);
            if (BinaryFiles.TryGetValue(relativeRemotePath, out byte[]? bytes))
            {
                File.WriteAllBytes(localFilePath, bytes);
                return Task.FromResult((long)bytes.Length);
            }
            throw new FileNotFoundException("Remote file not found: " + relativeRemotePath);
        }
    }
}
