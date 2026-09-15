using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class ContainerSyncTests
{
    [Fact]
    public async Task Upload_WithArchiveSyncSevenZip_UploadsAtMostTwoFiles_AndCreatesSidecarManifest()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-sevenzip");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "container sync payload");

        var remote = new ArchiveRecordingRemoteFileSystem();
        var history = new RecordingHistoryRepository();
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            history);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions
        {
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.SevenZip
        });

        Assert.Equal(SyncItemAction.UploadToRemote, Assert.Single(plan.Items).Action);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                ArchiveSync = true,
                ArchiveFormat = BackupContainerFormat.SevenZip
            });

        Assert.Equal(1, result.Uploaded);
        // Upload request count <= 2 (1 archive container + 1 sidecar manifest)
        Assert.Equal(2, remote.UploadedFiles.Count);
        Assert.Equal("run-sevenzip.7z", remote.UploadedFiles[0]);
        Assert.Equal("run-sevenzip.7z.manifest.json", remote.UploadedFiles[1]);

        // Verify sidecar manifest content
        Assert.True(remote.TextFiles.ContainsKey("run-sevenzip.7z.manifest.json"));
        var manifest = JsonSerializer.Deserialize<TransferBackupManifest>(remote.TextFiles["run-sevenzip.7z.manifest.json"]);
        Assert.NotNull(manifest);
        Assert.Equal(run.Manifest.Game, manifest.Game);
    }

    [Fact]
    public async Task Upload_WithArchiveSyncZip_UploadsAtMostTwoFiles_AndCreatesSidecarManifest()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-zip");
        TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "zip container payload");

        var remote = new ArchiveRecordingRemoteFileSystem();
        var history = new RecordingHistoryRepository();
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            history);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions
        {
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.Zip
        });

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                ArchiveSync = true,
                ArchiveFormat = BackupContainerFormat.Zip
            });

        Assert.Equal(1, result.Uploaded);
        Assert.Equal(2, remote.UploadedFiles.Count);
        Assert.Equal("run-zip.zip", remote.UploadedFiles[0]);
        Assert.Equal("run-zip.zip.manifest.json", remote.UploadedFiles[1]);
    }

    [Fact]
    public async Task Upload_ByDefault_UploadsFolderRunFileByFile()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-folder");
        TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "folder payload");

        var remote = new ArchiveRecordingRemoteFileSystem();
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true });

        Assert.Equal(1, result.Uploaded);
        // By default, folder upload is used: payload file + manifest.json
        Assert.Contains("run-folder/files/payload.sav", remote.UploadedFiles);
        Assert.Contains("run-folder/manifest.json", remote.UploadedFiles);
    }

    [Fact]
    public async Task Download_ContainerRun_DownloadsSingleArchiveFile_AndExtractsLocally()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);

        // Prepare an actual .7z container run to serve from remote
        string sourceRunDir = temp.GetPath("source-run");
        TransferBackupRunInfo sourceRun = TestData.CreateBackupRun(
            sourceRunDir,
            temp.GetPath("orig.sav"),
            "archive download payload");

        var archiveService = new BackupArchiveService(backupHistory);
        string exportDir = temp.GetPath("exported-archives");
        BackupArchiveExportResult exportResult = await archiveService.ExportRunAsync(
            sourceRun,
            exportDir,
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Fast);
        Assert.True(exportResult.Success, exportResult.Message);

        var remote = new ArchiveRecordingRemoteFileSystem();
        string archiveFileName = Path.GetFileName(exportResult.ArchivePath!);
        remote.BinaryFiles[archiveFileName] = await File.ReadAllBytesAsync(exportResult.ArchivePath!);
        remote.ArchiveNames.Add(archiveFileName);
        remote.TextFiles[$"{archiveFileName}.manifest.json"] = JsonSerializer.Serialize(
            sourceRun.Manifest,
            new JsonSerializerOptions { WriteIndented = true });

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository(),
            archiveService);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal(SyncItemAction.DownloadToLocal, item.Action);
        Assert.Equal("source-run", item.RunName);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Download = true
            });

        Assert.Equal(1, result.Downloaded);
        // Download request count = 1 (single container download)
        Assert.Single(remote.DownloadedFiles);
        Assert.Equal(archiveFileName, remote.DownloadedFiles[0]);

        // Verifies imported folder structure in local backup base
        string localRunDir = Path.Combine(backupHistory.GetBackupBasePath(), "source-run");
        Assert.True(Directory.Exists(localRunDir));
        Assert.True(File.Exists(Path.Combine(localRunDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(localRunDir, "files", "payload.sav")));
        Assert.Equal("archive download payload", await File.ReadAllTextAsync(Path.Combine(localRunDir, "files", "payload.sav")));

        // No lingering .download_* temp entries
        Assert.Empty(Directory.GetFileSystemEntries(backupHistory.GetBackupBasePath(), ".download_*"));
    }

    [Fact]
    public async Task Preview_ZeroDownloadPreview_ReadsOnlySidecarDescriptor()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));

        var remote = new ArchiveRecordingRemoteFileSystem();
        string archiveName = "remote-run-2026.7z";
        remote.ArchiveNames.Add(archiveName);
        // Put binary bytes (payload) in remote
        remote.BinaryFiles[archiveName] = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00 };

        var sidecarManifest = new TransferBackupManifest(
            SchemaVersion: 1,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Preview Game",
            SteamAppId: "999",
            SourceAccountId: "src",
            TargetAccountId: "tgt",
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: DateTimeOffset.UtcNow.AddSeconds(1),
            FileCount: 2,
            TotalBytes: 54321,
            Items: []);

        remote.TextFiles[$"{archiveName}.manifest.json"] = JsonSerializer.Serialize(sidecarManifest);

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        // Zero payload bytes downloaded during preview
        Assert.Empty(remote.DownloadedFiles);
        // Sidecar manifest was read
        Assert.Contains($"{archiveName}.manifest.json", remote.TextFileReads);

        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal("remote-run-2026", item.RunName);
        Assert.Equal("Preview Game", item.GameName);
        Assert.Equal(SyncItemAction.DownloadToLocal, item.Action);
    }

    [Fact]
    public async Task Preview_LocalFolderDirectHeaderInspection_WhenSidecarMissing()
    {
        using var temp = new TemporaryDirectory();
        string localBackupDir = temp.GetPath("local-backup");
        string remoteFolderDir = temp.GetPath("remote-folder");
        Directory.CreateDirectory(localBackupDir);
        Directory.CreateDirectory(remoteFolderDir);

        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(backupHistory);

        // Create and export a real .7z archive to remoteFolderDir WITHOUT a sidecar .manifest.json
        string runDir = temp.GetPath("header-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runDir, temp.GetPath("orig.sav"), "header payload");
        BackupArchiveExportResult exportResult = await archiveService.ExportRunAsync(
            run,
            remoteFolderDir,
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Store);
        Assert.True(exportResult.Success, exportResult.Message);

        // Ensure no sidecar exists
        string sidecar = $"{exportResult.ArchivePath}.manifest.json";
        if (File.Exists(sidecar))
            File.Delete(sidecar);

        var remote = new LocalFolderRemoteFileSystem(remoteFolderDir, localBackupDir);
        var engine = new SyncEngine(
            remote,
            "LocalRemote",
            remoteFolderDir,
            backupHistory,
            new RecordingHistoryRepository(),
            archiveService,
            new BackupMetadataReader());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        SyncItem item = Assert.Single(plan.Items);
        Assert.Equal("header-run", item.RunName);
        Assert.Equal(run.Manifest.Game, item.GameName);
        Assert.Equal(SyncItemAction.DownloadToLocal, item.Action);
    }

    [Fact]
    public async Task PreviewAndSync_TransparentCoexistence_FoldersAndContainers()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(backupHistory);

        // 1. Prepare folder run in remote
        var remote = new ArchiveRecordingRemoteFileSystem();
        string folderRunName = "run-folder-alpha";
        remote.RunFolderNamesList.Add(folderRunName);
        var folderManifest = new TransferBackupManifest(
            1, OverwriteBackupContext.ManualKind, "Game Folder", "1", "s", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 12, []);
        remote.TextFiles[$"{folderRunName}/manifest.json"] = JsonSerializer.Serialize(folderManifest);
        remote.BinaryFiles[$"{folderRunName}/files/save.dat"] = "folder data"u8.ToArray();

        // 2. Prepare container run in remote
        string containerSourceDir = temp.GetPath("container-source");
        TransferBackupRunInfo containerRun = TestData.CreateBackupRun(
            containerSourceDir, temp.GetPath("orig.sav"), "container data");
        BackupArchiveExportResult exportResult = await archiveService.ExportRunAsync(
            containerRun, temp.GetPath("exp"), BackupContainerFormat.SevenZip, BackupCompressionPreset.Store);
        Assert.True(exportResult.Success);

        string containerName = "container-source.7z";
        remote.ArchiveNames.Add(containerName);
        remote.BinaryFiles[containerName] = await File.ReadAllBytesAsync(exportResult.ArchivePath!);
        remote.TextFiles[$"{containerName}.manifest.json"] = JsonSerializer.Serialize(containerRun.Manifest);

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository(),
            archiveService);

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        // Both runs are discovered in the plan
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, item => item.RunName == folderRunName && item.Action == SyncItemAction.DownloadToLocal);
        Assert.Contains(plan.Items, item => item.RunName == "container-source" && item.Action == SyncItemAction.DownloadToLocal);

        // Execute download of both
        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Download = true });

        Assert.Equal(2, result.Downloaded);
        Assert.True(Directory.Exists(Path.Combine(backupHistory.GetBackupBasePath(), folderRunName)));
        Assert.True(Directory.Exists(Path.Combine(backupHistory.GetBackupBasePath(), "container-source")));
    }

    [Fact]
    public async Task Download_CorruptArchive_RollsBack_CleansTempFiles_AndLeavesNoTargetDir()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));

        var remote = new ArchiveRecordingRemoteFileSystem();
        string archiveName = "corrupt-run.7z";
        remote.ArchiveNames.Add(archiveName);
        // Write bogus bytes (corrupt archive)
        remote.BinaryFiles[archiveName] = [0x37, 0x7A, 0xBC, 0xAF, 0x00, 0x00, 0xFF, 0xFE];

        var manifest = new TransferBackupManifest(
            1, OverwriteBackupContext.ManualKind, "Corrupt Game", "1", "s", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 8, []);
        remote.TextFiles[$"{archiveName}.manifest.json"] = JsonSerializer.Serialize(manifest);

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Download = true });

        SyncItemResult itemResult = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.Failed, itemResult.Status);
        Assert.Contains("Archive container import failed", itemResult.Error);

        // Target directory must NOT exist
        string targetDir = Path.Combine(backupHistory.GetBackupBasePath(), "corrupt-run");
        Assert.False(Directory.Exists(targetDir));

        // Ephemeral temp files must be completely cleaned up
        Assert.Empty(Directory.GetFileSystemEntries(backupHistory.GetBackupBasePath(), ".download_*"));
    }

    [Fact]
    public async Task Download_Cancellation_CleansUpTempFiles_AndLeavesNoTargetDir()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));

        var remote = new ArchiveRecordingRemoteFileSystem();
        string archiveName = "cancel-run.7z";
        remote.ArchiveNames.Add(archiveName);
        remote.BinaryFiles[archiveName] = new byte[1024];

        var manifest = new TransferBackupManifest(
            1, OverwriteBackupContext.ManualKind, "Cancel Game", "1", "s", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1024, []);
        remote.TextFiles[$"{archiveName}.manifest.json"] = JsonSerializer.Serialize(manifest);

        using var cts = new CancellationTokenSource();
        remote.OnDownload = () => cts.Cancel();

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Download = true });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Download = true },
            cts.Token));

        // Temp directory cleaned up
        Assert.Empty(Directory.GetFileSystemEntries(backupHistory.GetBackupBasePath(), ".download_*"));
        // Run directory never created
        Assert.False(Directory.Exists(Path.Combine(backupHistory.GetBackupBasePath(), "cancel-run")));
    }

    [Fact]
    public async Task SafetyInvariant_CreateOnly_NoOverwrite_WhenLocalTargetExists()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string localRunDir = Path.Combine(backupHistory.GetBackupBasePath(), "existing-run");
        Directory.CreateDirectory(localRunDir);
        string sentinelFile = Path.Combine(localRunDir, "do_not_touch.txt");
        await File.WriteAllTextAsync(sentinelFile, "pre-existing content");

        var remote = new ArchiveRecordingRemoteFileSystem();
        string archiveName = "existing-run.7z";
        remote.ArchiveNames.Add(archiveName);
        remote.BinaryFiles[archiveName] = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
        var manifest = new TransferBackupManifest(
            1, OverwriteBackupContext.ManualKind, "Existing Game", "1", "s", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 6, []);
        remote.TextFiles[$"{archiveName}.manifest.json"] = JsonSerializer.Serialize(manifest);

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        // Manually create item to download to existing target
        var item = new SyncItem(
            RunName: "existing-run",
            Action: SyncItemAction.DownloadToLocal,
            ExistsLocally: false,
            ExistsRemotely: true,
            LocalPath: localRunDir,
            RemotePath: $"test://remote/{archiveName}",
            GameName: "Existing Game",
            FileCount: 1,
            TotalBytes: 6,
            StatusText: "Download");

        var plan = new SyncPlan("Remote", "test://remote", [item], [], true, 0, 1, 0, 0, 0, 6);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true, Download = true });

        SyncItemResult itemResult = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.SkippedAlreadyExists, itemResult.Status);
        Assert.Equal("pre-existing content", await File.ReadAllTextAsync(sentinelFile));
    }

    [Fact]
    public async Task SafetyInvariant_CreateOnly_NoOverwrite_WhenRemoteArchiveExists()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "no-overwrite-remote");
        TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "local data");

        var remote = new ArchiveRecordingRemoteFileSystem();
        // Remote already has the archive
        remote.BinaryFiles["no-overwrite-remote.7z"] = "remote data"u8.ToArray();
        remote.ArchiveNames.Add("no-overwrite-remote.7z");

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        var item = new SyncItem(
            RunName: "no-overwrite-remote",
            Action: SyncItemAction.UploadToRemote,
            ExistsLocally: true,
            ExistsRemotely: false,
            LocalPath: runRoot,
            RemotePath: "test://remote/no-overwrite-remote.7z",
            GameName: "Test Game",
            FileCount: 1,
            TotalBytes: 100,
            StatusText: "Upload");

        var plan = new SyncPlan("Remote", "test://remote", [item], [], true, 1, 0, 0, 0, 100, 0);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                ArchiveSync = true,
                ArchiveFormat = BackupContainerFormat.SevenZip
            });

        SyncItemResult itemResult = Assert.Single(result.Items);
        Assert.Equal(SyncItemStatus.SkippedAlreadyExists, itemResult.Status);
        Assert.Equal("remote data", System.Text.Encoding.UTF8.GetString(remote.BinaryFiles["no-overwrite-remote.7z"]));
    }

    [Fact]
    public async Task RoundTrip_LocalToRemoteToSecondLocal_FullPayloadAndHashVerification()
    {
        using var temp = new TemporaryDirectory();
        string local1Path = temp.GetPath("local1");
        string remotePath = temp.GetPath("remote");
        string local2Path = temp.GetPath("local2");
        Directory.CreateDirectory(local1Path);
        Directory.CreateDirectory(remotePath);
        Directory.CreateDirectory(local2Path);

        var history1 = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app1", "gamesave.db")));
        var history2 = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app2", "gamesave.db")));

        // Create source run in local1 with nested files
        string runRoot = Path.Combine(history1.GetBackupBasePath(), "trip-run");
        string file1 = Path.Combine(runRoot, "files", "save1.dat");
        string file2 = Path.Combine(runRoot, "files", "sub", "save2.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(file2)!);
        await File.WriteAllTextAsync(file1, "Save data 1 content");
        await File.WriteAllBytesAsync(file2, [1, 2, 3, 4, 5, 6, 7, 8]);

        string hash1 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file1)));
        string hash2 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file2)));

        var manifest = new TransferBackupManifest(
            1, OverwriteBackupContext.ManualKind, "Trip Game", "456", "s", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1), 2,
            new FileInfo(file1).Length + new FileInfo(file2).Length,
            [
                new TransferOverwriteBackupItem(temp.GetPath("orig1.sav"), file1, new FileInfo(file1).Length, hash1, DateTimeOffset.UtcNow),
                new TransferOverwriteBackupItem(temp.GetPath("orig2.sav"), file2, new FileInfo(file2).Length, hash2, DateTimeOffset.UtcNow)
            ]);
        await File.WriteAllTextAsync(Path.Combine(runRoot, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        // 1. Upload from local1 to remote as .7z archive container
        var remote1 = new LocalFolderRemoteFileSystem(remotePath, history1.GetBackupBasePath());
        var engine1 = new SyncEngine(remote1, "LocalRemote", remotePath, history1, new RecordingHistoryRepository());
        SyncPlan plan1 = await engine1.CreatePreviewAsync(new SyncOptions
        {
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.SevenZip
        });

        SyncResult uploadResult = await engine1.ExecuteAsync(plan1, new SyncOptions
        {
            DryRun = false,
            ConfirmExecution = true,
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.SevenZip
        });

        Assert.Equal(1, uploadResult.Uploaded);
        Assert.True(File.Exists(Path.Combine(remotePath, "trip-run.7z")));
        Assert.True(File.Exists(Path.Combine(remotePath, "trip-run.7z.manifest.json")));

        // 2. Download from remote to local2
        var remote2 = new LocalFolderRemoteFileSystem(remotePath, history2.GetBackupBasePath());
        var engine2 = new SyncEngine(remote2, "LocalRemote", remotePath, history2, new RecordingHistoryRepository());

        SyncPlan plan2 = await engine2.CreatePreviewAsync(new SyncOptions { Download = true });
        Assert.Single(plan2.Items);

        SyncResult downloadResult = await engine2.ExecuteAsync(plan2, new SyncOptions
        {
            DryRun = false,
            ConfirmExecution = true,
            Download = true
        });

        Assert.Equal(1, downloadResult.Downloaded);

        // 3. Verify extracted contents and hash verification in local2
        string restoredRunDir = Path.Combine(history2.GetBackupBasePath(), "trip-run");
        Assert.True(Directory.Exists(restoredRunDir));

        string restoredFile1 = Path.Combine(restoredRunDir, "files", "save1.dat");
        string restoredFile2 = Path.Combine(restoredRunDir, "files", "sub", "save2.bin");
        Assert.True(File.Exists(restoredFile1));
        Assert.True(File.Exists(restoredFile2));

        Assert.Equal(hash1, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(restoredFile1))));
        Assert.Equal(hash2, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(restoredFile2))));
        Assert.Equal("Save data 1 content", await File.ReadAllTextAsync(restoredFile1));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await File.ReadAllBytesAsync(restoredFile2));

        // Verify restored manifest
        Assert.True(File.Exists(Path.Combine(restoredRunDir, "manifest.json")));
        var restoredManifest = JsonSerializer.Deserialize<TransferBackupManifest>(await File.ReadAllTextAsync(Path.Combine(restoredRunDir, "manifest.json")));
        Assert.NotNull(restoredManifest);
        Assert.Equal("Trip Game", restoredManifest.Game);
        Assert.Equal(2, restoredManifest.FileCount);
    }

    [Fact]
    public async Task Upload_WithArchiveSync_OnRemoteThatCannotListContainers_FallsBackToFolderAndWarns()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string runRoot = Path.Combine(backupHistory.GetBackupBasePath(), "run-nocontainers");
        TestData.CreateBackupRun(runRoot, temp.GetPath("orig.sav"), "fallback payload");

        // Google Drive is exactly this backend: it implements neither
        // ListRunArchiveNamesAsync nor FileExistsAsync, so a container uploaded
        // there would be invisible to every later preview.
        var remote = new ArchiveRecordingRemoteFileSystem { SupportsArchiveContainers = false };
        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        var options = new SyncOptions
        {
            Upload = true,
            ArchiveSync = true,
            ArchiveFormat = BackupContainerFormat.SevenZip
        };

        SyncPlan plan = await engine.CreatePreviewAsync(options);

        Assert.Contains(plan.Warnings, w => w.Code == "ArchiveSyncUnsupported");
        Assert.EndsWith("run-nocontainers", Assert.Single(plan.Items).RemotePath);

        SyncResult result = await engine.ExecuteAsync(
            plan,
            new SyncOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                Upload = true,
                ArchiveSync = true,
                ArchiveFormat = BackupContainerFormat.SevenZip
            });

        Assert.Equal(1, result.Uploaded);
        Assert.All(remote.UploadedFiles, name => Assert.StartsWith("run-nocontainers/", name));
        Assert.DoesNotContain(remote.UploadedFiles, name => name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_WhenAFolderRunAndASameStemContainerBothExistLocally_WarnsInsteadOfThrowing()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);
        string basePath = backupHistory.GetBackupBasePath();
        string runRoot = Path.Combine(basePath, "collide");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            runRoot, temp.GetPath("orig.sav"), "collision payload");

        // Exporting the run beside itself produces "collide.zip" next to "collide".
        var archiveService = new BackupArchiveService(backupHistory);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(run, basePath);
        Assert.True(export.Success, export.Message);

        var engine = new SyncEngine(
            new ArchiveRecordingRemoteFileSystem(),
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions { Upload = true });

        Assert.Contains(plan.Warnings, w => w.Code == "LocalRunNameCollision");
        Assert.Equal("collide", Assert.Single(plan.Items).RunName);
    }

    [Fact]
    public async Task Preview_WhenARemoteContainerHasNoSidecarManifest_ReportsItInsteadOfSkippingSilently()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        var backupHistory = new BackupHistoryService(pathProvider);

        // An upload interrupted between the container and its manifest leaves
        // exactly this: a payload nothing can identify.
        var remote = new ArchiveRecordingRemoteFileSystem();
        remote.ArchiveNames.Add("interrupted-run.7z");

        var engine = new SyncEngine(
            remote,
            "Remote",
            "test://remote",
            backupHistory,
            new RecordingHistoryRepository());

        SyncPlan plan = await engine.CreatePreviewAsync(
            new SyncOptions { Upload = true, Download = true });

        TransferPreviewWarning warning = Assert.Single(
            plan.Warnings,
            w => w.Code == "RemoteContainerIncomplete");

        Assert.Contains("interrupted-run.7z", warning.Message, StringComparison.Ordinal);
        Assert.Empty(plan.Items);
    }

    private sealed class ArchiveRecordingRemoteFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, string> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BinaryFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UploadedFiles { get; } = new();
        public List<string> DownloadedFiles { get; } = new();
        public List<string> TextFileReads { get; } = new();
        public List<string> ArchiveNames { get; } = new();
        public List<string> RunFolderNamesList { get; } = new();
        public Action? OnDownload { get; set; }

        public string DisplayRoot => "test://remote";

        // A backend only receives containers once it declares that it can list them
        // back. Google Drive does not implement either member and must stay false.
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

        public Task<bool> FolderExistsAsync(string relativeFolder, CancellationToken cancellationToken = default)
        {
            string prefix = relativeFolder.TrimEnd('/') + "/";
            bool exists = RunFolderNamesList.Contains(relativeFolder) ||
                          TextFiles.Keys.Concat(BinaryFiles.Keys).Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(exists);
        }

        public Task<bool> FileExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(TextFiles.ContainsKey(relativePath) || BinaryFiles.ContainsKey(relativePath));

        public Task<string?> ReadTextFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            TextFileReads.Add(relativePath);
            TextFiles.TryGetValue(relativePath, out string? content);
            return Task.FromResult(content);
        }

        public Task CreateTextFileIfMissingAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            if (!TextFiles.TryAdd(relativePath, content))
                throw new IOException($"Remote file '{relativePath}' already exists.");

            UploadedFiles.Add(relativePath);
            return Task.CompletedTask;
        }

        public Task<string?> ReadProviderMetadataAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            TextFiles.TryGetValue(relativePath, out string? content);
            return Task.FromResult(content);
        }

        public Task ReplaceProviderMetadataAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            TextFiles[relativePath] = content;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeFolder, CancellationToken cancellationToken = default)
        {
            string prefix = relativeFolder.TrimEnd('/') + "/";
            IReadOnlyList<string> files = TextFiles.Keys.Concat(BinaryFiles.Keys)
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[prefix.Length..])
                .ToList();
            return Task.FromResult(files);
        }

        public async Task<long> UploadFileAsync(string localFilePath, string relativeRemotePath, CancellationToken cancellationToken = default)
        {
            if (TextFiles.ContainsKey(relativeRemotePath) || BinaryFiles.ContainsKey(relativeRemotePath))
                throw new IOException($"Remote file '{relativeRemotePath}' already exists.");

            byte[] bytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
            BinaryFiles.Add(relativeRemotePath, bytes);
            UploadedFiles.Add(relativeRemotePath);
            return bytes.LongLength;
        }

        public async Task<long> DownloadFileAsync(string relativeRemotePath, string localFilePath, CancellationToken cancellationToken = default)
        {
            OnDownload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes;
            if (BinaryFiles.TryGetValue(relativeRemotePath, out byte[]? binBytes))
            {
                bytes = binBytes;
            }
            else if (TextFiles.TryGetValue(relativeRemotePath, out string? text))
            {
                bytes = System.Text.Encoding.UTF8.GetBytes(text);
            }
            else
            {
                throw new FileNotFoundException($"Remote file '{relativeRemotePath}' not found.");
            }

            DownloadedFiles.Add(relativeRemotePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            await File.WriteAllBytesAsync(localFilePath, bytes, cancellationToken);
            return bytes.LongLength;
        }
    }
}
