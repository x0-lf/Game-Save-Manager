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
    public async Task Preview_WhenLocalRunIsContainer_AndRemoteCannotStoreContainers_WarnsAndLeavesItOut()
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

        // Warned about, and not planned: an upload execution must refuse
        // would be counted, enable Sync and fail on every run.
        Assert.Contains(plan.Warnings, w => w.Code == "LocalContainerUnsupported");
        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.UploadCount);
        Assert.False(plan.CanExecute);
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
        string staleStaging = Path.Combine(basePath, WorkingName(".staging_"));
        string staleExport = Path.Combine(basePath, WorkingName(".export_"));
        string staleDownload = Path.Combine(basePath, WorkingName(".download_"));
        string staleExportFile = Path.Combine(basePath, WorkingName(".export_") + ".tmp");

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
        string freshStaging = Path.Combine(basePath, WorkingName(".staging_"));
        string freshDownload = Path.Combine(basePath, WorkingName(".download_"));
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
    public void PurgeStaleWorkingDirectories_KeepsOldDirectoryWithRecentNestedWrite()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();

        // A long import: the directories were created hours ago and stopped changing
        // once their children existed, but a file deep inside is still being written.
        string staging = Path.Combine(basePath, WorkingName(".staging_"));
        string nested = Path.Combine(staging, "files", "C", "Game");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "part.sav"), "in progress");

        DateTime oldTime = DateTime.UtcNow.AddHours(-3);
        Directory.SetLastWriteTimeUtc(nested, oldTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(staging, "files", "C"), oldTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(staging, "files"), oldTime);
        Directory.SetLastWriteTimeUtc(staging, oldTime);

        history.PurgeStaleWorkingDirectories(TimeSpan.FromHours(1));

        Assert.True(File.Exists(Path.Combine(nested, "part.sav")));
    }

    [Fact]
    public void PurgeStaleWorkingDirectories_NeverTouchesNamesTheWritersDoNotGenerate()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();

        // An imported run keeps its archive's name, and nothing stops that name
        // from starting with a working prefix.
        string importedRun = Path.Combine(basePath, ".staging_backup");
        TestData.CreateBackupRun(importedRun, temp.GetPath("orig.sav"), "user data");
        string lookalikeFile = Path.Combine(basePath, ".export_notes.tmp");
        File.WriteAllText(lookalikeFile, "user file");

        DateTime oldTime = DateTime.UtcNow.AddHours(-3);
        foreach (string file in Directory.EnumerateFiles(importedRun, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, oldTime);
        foreach (string dir in Directory.EnumerateDirectories(importedRun, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(dir, oldTime);
        Directory.SetLastWriteTimeUtc(importedRun, oldTime);
        File.SetLastWriteTimeUtc(lookalikeFile, oldTime);

        history.PurgeStaleWorkingDirectories(TimeSpan.FromHours(1));

        Assert.True(File.Exists(Path.Combine(importedRun, "manifest.json")));
        Assert.True(File.Exists(lookalikeFile));
    }

    [Fact]
    public async Task GetRunsAsync_PurgesStaleWorkingDirectoriesOncePerInstance()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        DateTime oldTime = DateTime.UtcNow.AddHours(-3);

        string first = Path.Combine(basePath, WorkingName(".download_"));
        Directory.CreateDirectory(first);
        Directory.SetLastWriteTimeUtc(first, oldTime);

        await history.GetRunsAsync();
        Assert.False(Directory.Exists(first));

        string second = Path.Combine(basePath, WorkingName(".download_"));
        Directory.CreateDirectory(second);
        Directory.SetLastWriteTimeUtc(second, oldTime);

        // A history refresh is a read; the purge already ran for this instance.
        await history.GetRunsAsync();
        Assert.True(Directory.Exists(second));
    }

    private static string WorkingName(string prefix) => prefix + Guid.NewGuid().ToString("N");

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
