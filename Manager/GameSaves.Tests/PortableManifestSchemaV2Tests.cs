using GameSaves.App.Models;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class PortableManifestSchemaV2Tests
{
    [Fact]
    public void SchemaV2_ManifestGeneration_WritesRelativePayloadPathsAndVersion2()
    {
        using var temp = new TemporaryDirectory();
        string original = temp.GetPath("saves", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "save payload content");

        var service = new TransferOverwriteBackupService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));

        using ITransferOverwriteBackupSession session = service.BeginSession(
            new OverwriteBackupContext(
                OverwriteBackupContext.ManualKind,
                "The Witcher 3",
                "292030",
                "sourceAccount",
                "targetAccount"));

        TransferOverwriteBackupItem item = session.BackUpFile(original);
        session.Complete();

        Assert.NotNull(item.RelativePath);
        Assert.StartsWith("files/", item.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(item.RelativePath, item.GetRelativePayloadPath());

        string manifestFile = Path.Combine(session.BackupRootPath, "manifest.json");
        Assert.True(File.Exists(manifestFile));

        string json = File.ReadAllText(manifestFile);
        TransferBackupManifest manifest = JsonSerializer.Deserialize<TransferBackupManifest>(json)!;

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("folder", manifest.Format);
        Assert.Equal("The Witcher 3", manifest.Game);
        Assert.Single(manifest.Items);

        TransferOverwriteBackupItem recordedItem = manifest.Items[0];
        Assert.NotNull(recordedItem.RelativePath);
        Assert.Equal(item.RelativePath, recordedItem.RelativePath);
        Assert.True(manifest.IsSupportedSchemaVersion);
        Assert.True(manifest.TryValidate(out string? error), error);
    }

    [Fact]
    public void SchemaV1_LegacyManifest_MaintainsBackwardCompatibility_AndUpgradesToV2()
    {
        // Legacy Schema v1 json without RelativePath or Format
        string legacyJson = """
        {
          "SchemaVersion": 1,
          "Kind": "manual",
          "Game": "Cyberpunk 2077",
          "SteamAppId": "1091500",
          "SourceAccountId": "12345",
          "TargetAccountId": "67890",
          "StartedUtc": "2026-09-14T10:00:00+00:00",
          "CompletedUtc": "2026-09-14T10:00:01+00:00",
          "FileCount": 1,
          "TotalBytes": 100,
          "Items": [
            {
              "OriginalFile": "C:\\Users\\Mike\\Saved Games\\save.dat",
              "BackupFile": "D:\\Backups\\Run1\\files\\C\\Users\\Mike\\Saved Games\\save.dat",
              "Bytes": 100,
              "Sha256": "ABCDEF123456",
              "BackedUpUtc": "2026-09-14T10:00:00+00:00"
            }
          ]
        }
        """;

        TransferBackupManifest manifest = JsonSerializer.Deserialize<TransferBackupManifest>(legacyJson)!;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Null(manifest.Format);
        Assert.Null(manifest.Items[0].RelativePath);

        // Relative path extraction works seamlessly
        string extractedRel = manifest.Items[0].GetRelativePayloadPath();
        Assert.Equal("files/C/Users/Mike/Saved Games/save.dat", extractedRel);

        // ToSchemaV2 creates an upgraded instance with SchemaVersion = 2 and RelativePath populated
        TransferBackupManifest upgraded = manifest.ToSchemaV2();
        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal("files/C/Users/Mike/Saved Games/save.dat", upgraded.Items[0].RelativePath);
    }

    [Fact]
    public async Task SchemaV2_Portability_ResolveBackupFileAcrossDrivesAndMachines()
    {
        using var temp = new TemporaryDirectory();

        // Simulate a backup that was created at "X:\MachineA\OldBackup"
        string originalTarget = temp.GetPath("target", "save.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(originalTarget)!);

        // Relocated run on Machine B at a different path
        string relocatedRunRoot = temp.GetPath("relocated_run");
        string actualPayloadPath = Path.Combine(relocatedRunRoot, "files", "C", "save.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(actualPayloadPath)!);
        File.WriteAllText(actualPayloadPath, "relocated save content");

        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actualPayloadPath)));

        var item = new TransferOverwriteBackupItem(
            OriginalFile: originalTarget,
            BackupFile: @"X:\MachineA\NonExistentDrive\Run1\files\C\save.dat", // doesn't exist here!
            Bytes: new FileInfo(actualPayloadPath).Length,
            Sha256: sha256,
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "files/C/save.dat");

        // ResolveBackupFile resolves to the relocated payload file
        string resolved = item.ResolveBackupFile(relocatedRunRoot);
        Assert.Equal(actualPayloadPath, resolved);
        Assert.True(File.Exists(resolved));

        var manifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Portability Test",
            SteamAppId: "999",
            SourceAccountId: "src",
            TargetAccountId: "tgt",
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: DateTimeOffset.UtcNow,
            FileCount: 1,
            TotalBytes: item.Bytes,
            Items: new[] { item },
            Format: "folder");

        var run = new TransferBackupRunInfo(
            BackupRootPath: relocatedRunRoot,
            ManifestPath: Path.Combine(relocatedRunRoot, "manifest.json"),
            Manifest: manifest);

        var restoreService = new BackupRestoreService(
            new TransferOverwriteBackupService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult restoreResult = await restoreService.RestoreAsync(
            run,
            new BackupRestoreOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                VerifyHashes = true
            });

        Assert.Equal(1, restoreResult.FilesRestored);
        Assert.True(File.Exists(originalTarget));
        Assert.Equal("relocated save content", File.ReadAllText(originalTarget));
    }

    [Fact]
    public async Task ZeroExtraction_ZipArchivePreview_ReadsManifestWithoutExtractingPayload()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("runs", "source_run");
        string original = temp.GetPath("original.sav");
        TransferBackupRunInfo originalRun = TestData.CreateBackupRun(runRoot, original, "payload data to zip");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(history);

        string archiveFolder = temp.GetPath("archives");
        BackupArchiveExportResult exportResult = await archiveService.ExportRunAsync(originalRun, archiveFolder);
        Assert.True(exportResult.Success, exportResult.Message);
        string zipPath = exportResult.ArchivePath!;

        // Delete the uncompressed run folder so only the ZIP file exists
        Directory.Delete(runRoot, recursive: true);

        // Probe the ZIP archive using IBackupMetadataReader
        var reader = new BackupMetadataReader();
        BackupContainerFormat format = reader.DetectContainerFormat(zipPath);
        Assert.Equal(BackupContainerFormat.Zip, format);

        bool readSuccess = reader.TryBuildRunInfo(zipPath, out TransferBackupRunInfo? runInfo, out string? error);
        Assert.True(readSuccess, error);
        Assert.NotNull(runInfo);

        Assert.True(runInfo.IsZip);
        Assert.True(runInfo.IsArchive);
        Assert.False(runInfo.IsFolder);
        Assert.Equal(BackupContainerFormat.Zip, runInfo.ContainerFormat);
        Assert.Equal("Test Game", runInfo.Manifest.Game);
        Assert.Equal(1, runInfo.Manifest.FileCount);

        // Verify that no unzipped folder or extracted files were created in the archive directory
        string[] filesInDir = Directory.GetFiles(archiveFolder);
        Assert.Single(filesInDir); // only the .zip archive itself exists
        Assert.Empty(Directory.GetDirectories(archiveFolder)); // zero directories created
    }

    [Fact]
    public void ContainerFormat_Detection_IdentifiesFolderZipAndSevenZipMagicHeaders()
    {
        using var temp = new TemporaryDirectory();
        var reader = new BackupMetadataReader();

        // 1. Directory -> Folder
        string dir = temp.GetPath("folder_container");
        Directory.CreateDirectory(dir);
        Assert.Equal(BackupContainerFormat.Folder, reader.DetectContainerFormat(dir));

        // 2. ZIP file with standard PK header
        string zipFile = temp.GetPath("archive.zip");
        using (ZipArchive zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }
        Assert.Equal(BackupContainerFormat.Zip, reader.DetectContainerFormat(zipFile));

        // 3. 7z file with standard 7z signature: 37 7A BC AF 27 1C
        string sevenZipFile = temp.GetPath("archive.7z");
        byte[] sevenZipHeader = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04];
        File.WriteAllBytes(sevenZipFile, sevenZipHeader);
        Assert.Equal(BackupContainerFormat.SevenZip, reader.DetectContainerFormat(sevenZipFile));

        // 4. Fallback extension without magic bytes
        string fakeSevenZip = temp.GetPath("test_extension.7z");
        File.WriteAllText(fakeSevenZip, "dummy non-binary");
        Assert.Equal(BackupContainerFormat.SevenZip, reader.DetectContainerFormat(fakeSevenZip));
    }

    [Fact]
    public void ManifestValidation_RejectsCorruptOrUnsupportedFutureSchemaVersions()
    {
        var reader = new BackupMetadataReader();

        // Schema version 0 (invalid)
        var v0 = new TransferBackupManifest(
            SchemaVersion: 0,
            Kind: "manual",
            Game: "Game",
            SteamAppId: "123",
            SourceAccountId: "src",
            TargetAccountId: "tgt",
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: DateTimeOffset.UtcNow,
            FileCount: 0,
            TotalBytes: 0,
            Items: Array.Empty<TransferOverwriteBackupItem>());

        Assert.False(v0.IsSupportedSchemaVersion);
        Assert.False(v0.TryValidate(out string? err0));
        Assert.Contains("Unsupported manifest schema version 0", err0);

        // Schema version 3 (future unsupported version)
        var v3 = v0 with { SchemaVersion = 3 };
        Assert.False(v3.IsSupportedSchemaVersion);
        Assert.False(v3.TryValidate(out string? err3));
        Assert.Contains("Unsupported manifest schema version 3", err3);

        // Missing game name
        var noGame = v0 with { SchemaVersion = 2, Game = "" };
        Assert.False(noGame.TryValidate(out string? errGame));
        Assert.Contains("missing required Game name", errGame);

        // Invalid JSON file
        using var temp = new TemporaryDirectory();
        string corruptedFolder = temp.GetPath("corrupt_run");
        Directory.CreateDirectory(corruptedFolder);
        File.WriteAllText(Path.Combine(corruptedFolder, "manifest.json"), "{ broken json");

        Assert.False(reader.TryReadManifest(corruptedFolder, out _, out string? readErr));
        Assert.NotNull(readErr);
    }

    [Fact]
    public async Task BackupHistory_DiscoversBothFoldersAndArchives_SharingUnifiedCatalogModel()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        string basePath = TransferBackupLocations.GetBackupBasePath(pathProvider);
        Directory.CreateDirectory(basePath);

        // 1. Create a folder run
        string folderRunPath = Path.Combine(basePath, "run_folder");
        TestData.CreateBackupRun(folderRunPath, temp.GetPath("orig1.sav"), "folder content");

        // 2. Create a ZIP archive run
        string stagingFolder = temp.GetPath("staging");
        TransferBackupRunInfo stagedRun = TestData.CreateBackupRun(stagingFolder, temp.GetPath("orig2.sav"), "zip content");

        var history = new BackupHistoryService(pathProvider);
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult exportResult = await archiveService.ExportRunAsync(stagedRun, basePath);
        Assert.True(exportResult.Success, exportResult.Message);

        // Discover runs through BackupHistoryService
        IReadOnlyList<TransferBackupRunInfo> discoveredRuns = await history.GetRunsAsync();

        Assert.Equal(2, discoveredRuns.Count);
        Assert.Contains(discoveredRuns, r => r.IsFolder && r.ContainerFormat == BackupContainerFormat.Folder);
        Assert.Contains(discoveredRuns, r => r.IsZip && r.ContainerFormat == BackupContainerFormat.Zip);

        TransferBackupRunInfo zipRun = discoveredRuns.First(r => r.IsZip);
        var zipRowVm = new BackupRunRowViewModel(zipRun);
        Assert.Equal("ZIP Archive", zipRowVm.FormatDisplay);
        Assert.Equal(BackupContainerFormat.Zip, zipRowVm.ContainerFormat);

        TransferBackupRunInfo folderRun = discoveredRuns.First(r => r.IsFolder);
        var folderRowVm = new BackupRunRowViewModel(folderRun);
        Assert.Equal("Folder", folderRowVm.FormatDisplay);
        Assert.Equal(BackupContainerFormat.Folder, folderRowVm.ContainerFormat);
    }

    [Fact]
    public async Task BackupCleanup_DeletesArchiveContainersSafely()
    {
        using var temp = new TemporaryDirectory();
        var pathProvider = new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"));
        string basePath = TransferBackupLocations.GetBackupBasePath(pathProvider);
        Directory.CreateDirectory(basePath);

        // Create and export an archive into basePath
        string stagingFolder = temp.GetPath("staging");
        TransferBackupRunInfo stagedRun = TestData.CreateBackupRun(stagingFolder, temp.GetPath("orig.sav"), "cleanable zip");

        var history = new BackupHistoryService(pathProvider);
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(stagedRun, basePath);
        Assert.True(export.Success);
        string archiveFile = export.ArchivePath!;
        Assert.True(File.Exists(archiveFile));

        IReadOnlyList<TransferBackupRunInfo> runs = await history.GetRunsAsync();
        TransferBackupRunInfo archiveRun = Assert.Single(runs);
        Assert.True(archiveRun.IsArchive);

        var cleanup = new BackupCleanupService(history, new RecordingHistoryRepository());

        // Dry run
        BackupCleanupResult dryRunResult = await cleanup.CleanupAsync(new BackupCleanupOptions
        {
            DryRun = true,
            ConfirmExecution = true,
            KeepNewestRuns = 0
        });

        Assert.Equal(1, dryRunResult.RunsConsidered);
        Assert.Equal(0, dryRunResult.RunsDeleted);
        Assert.True(File.Exists(archiveFile));

        // Actual execution
        BackupCleanupResult actualResult = await cleanup.DeleteRunAsync(archiveRun, confirmExecution: true);

        Assert.False(actualResult.HasErrors);
        Assert.Equal(1, actualResult.RunsDeleted);
        Assert.False(File.Exists(archiveFile));
    }

    [Fact]
    public async Task BackupRestore_RejectsDirectRestoreOfArchiveWithoutImport()
    {
        using var temp = new TemporaryDirectory();
        string staging = temp.GetPath("staging");
        TransferBackupRunInfo staged = TestData.CreateBackupRun(staging, temp.GetPath("orig.sav"), "archive content");

        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var archiveService = new BackupArchiveService(history);
        BackupArchiveExportResult export = await archiveService.ExportRunAsync(staged, temp.GetPath("out"));
        Assert.True(export.Success);

        var reader = new BackupMetadataReader();
        Assert.True(reader.TryBuildRunInfo(export.ArchivePath!, out TransferBackupRunInfo? archiveRun, out _));
        Assert.NotNull(archiveRun);
        Assert.True(archiveRun.IsArchive);

        var restoreService = new BackupRestoreService(
            new TransferOverwriteBackupService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult result = await restoreService.RestoreAsync(
            archiveRun,
            new BackupRestoreOptions
            {
                DryRun = false,
                ConfirmExecution = true
            });

        Assert.Equal(0, result.FilesRestored);
        Assert.Contains(result.Warnings, w => w.Code == "ArchiveMustBeImported");
    }

    [Fact]
    public void BackupManifestPathRewriter_HandlesSchemaV2DirectlyAndUpgradesSchemaV1()
    {
        using var temp = new TemporaryDirectory();
        string targetRoot = temp.GetPath("new_target");
        string targetFile = Path.Combine(targetRoot, "files", "C", "save.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        File.WriteAllText(targetFile, "target save content");

        // Schema v2 test: uses RelativePath directly
        var itemV2 = new TransferOverwriteBackupItem(
            OriginalFile: "C:\\Original\\save.dat",
            BackupFile: "D:\\OldRoot\\files\\C\\save.dat",
            Bytes: 10,
            Sha256: "HASH",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "files/C/save.dat");

        var manifestV2 = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: "manual",
            Game: "Game",
            SteamAppId: "123",
            SourceAccountId: "src",
            TargetAccountId: "tgt",
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: DateTimeOffset.UtcNow,
            FileCount: 1,
            TotalBytes: 10,
            Items: new[] { itemV2 },
            Format: "folder");

        bool v2Rewritten = BackupManifestPathRewriter.TryRewrite(manifestV2, targetRoot, out TransferBackupManifest rewrittenV2);
        Assert.True(v2Rewritten);
        Assert.Equal(2, rewrittenV2.SchemaVersion);
        Assert.Equal(targetFile, rewrittenV2.Items[0].BackupFile);

        // Schema v1 test: uses prefix matching and upgrades to Schema v2
        var itemV1 = new TransferOverwriteBackupItem(
            OriginalFile: "C:\\Original\\save.dat",
            BackupFile: "D:\\OldRoot\\files\\C\\save.dat",
            Bytes: 10,
            Sha256: "HASH",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: null);

        var manifestV1 = manifestV2 with
        {
            SchemaVersion = 1,
            Items = new[] { itemV1 }
        };

        bool v1Rewritten = BackupManifestPathRewriter.TryRewrite(manifestV1, targetRoot, out TransferBackupManifest rewrittenV1);
        Assert.True(v1Rewritten);
        Assert.Equal(2, rewrittenV1.SchemaVersion);
        Assert.Equal(targetFile, rewrittenV1.Items[0].BackupFile);
        Assert.Equal("files/C/save.dat", rewrittenV1.Items[0].RelativePath);
    }
}
