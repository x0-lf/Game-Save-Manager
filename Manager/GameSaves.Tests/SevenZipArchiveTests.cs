using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class SevenZipArchiveTests
{
    private static readonly byte[] SevenZipMagicBytes = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];


    [Fact]
    public void DetectContainerFormat_RecognizesSevenZipMagicBytes()
    {
        using var temp = new TemporaryDirectory();
        string testFile = temp.GetPath("unknown_extension.dat");
        byte[] payload = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, 0x00, 0x00];
        File.WriteAllBytes(testFile, payload);

        BackupContainerFormat format = BackupMetadataReader.DetectFormat(testFile);

        Assert.Equal(BackupContainerFormat.SevenZip, format);
    }

    [Fact]
    public void DetectContainerFormat_RecognizesSevenZipExtension()
    {
        BackupContainerFormat format = BackupMetadataReader.DetectFormat("some/path/my_backup.7z");

        Assert.Equal(BackupContainerFormat.SevenZip, format);
    }

    [Fact]
    public async Task ExportRunAsync_WithStorePreset_CreatesValidSevenZipArchive()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", "run-store");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "uncompressed raw store payload");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        string outputDir = temp.GetPath("archives");
        BackupArchiveExportResult exported = await service.ExportRunAsync(
            run,
            outputDir,
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Store);

        Assert.True(exported.Success, exported.Message);
        Assert.NotNull(exported.ArchivePath);
        Assert.True(File.Exists(exported.ArchivePath));
        Assert.EndsWith(".7z", exported.ArchivePath, StringComparison.OrdinalIgnoreCase);

        byte[] header = new byte[6];
        using (var fs = File.OpenRead(exported.ArchivePath))
        {
            int read = fs.Read(header, 0, 6);
            Assert.Equal(6, read);
        }
        Assert.Equal(SevenZipMagicBytes, header);
    }

    [Theory]
    [InlineData(BackupCompressionPreset.Fast)]
    [InlineData(BackupCompressionPreset.Optimal)]
    [InlineData(BackupCompressionPreset.Ultra)]
    public async Task ExportRunAsync_WithCompressedPresets_SucceedsAndProducesValidArchives(BackupCompressionPreset preset)
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", $"run-{preset}");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "repeated compressible text repeated compressible text repeated compressible text");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        string outputDir = temp.GetPath("archives");
        BackupArchiveExportResult exported = await service.ExportRunAsync(
            run,
            outputDir,
            BackupContainerFormat.SevenZip,
            preset);

        Assert.True(exported.Success, exported.Message);
        Assert.NotNull(exported.ArchivePath);
        Assert.True(File.Exists(exported.ArchivePath));

        byte[] header = new byte[6];
        using (var fs = File.OpenRead(exported.ArchivePath))
        {
            int read = fs.Read(header, 0, 6);
            Assert.Equal(6, read);
        }
        Assert.Equal(SevenZipMagicBytes, header);
    }

    [Fact]
    public async Task TryReadManifest_FromSevenZipArchive_ReadsDirectlyWithoutFullExtraction()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", "reader-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "zero extraction metadata test payload");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        string outputDir = temp.GetPath("archives");
        BackupArchiveExportResult exported = await service.ExportRunAsync(
            run,
            outputDir,
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Optimal);

        Assert.True(exported.Success);

        var reader = new BackupMetadataReader();
        bool readSuccess = reader.TryReadManifest(exported.ArchivePath!, out TransferBackupManifest? manifest, out string? error);

        Assert.True(readSuccess, error);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("Test Game", manifest.Game);
        Assert.Equal("1234", manifest.SteamAppId);
        Assert.Single(manifest.Items);
    }

    [Fact]
    public async Task ImportArchiveAsync_ImportsSevenZipRun_RewritesPathsAndRegistersInDatabase()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", "portable-7z-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "portable 7z save payload");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("target-app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        BackupArchiveExportResult exported = await service.ExportRunAsync(
            run,
            temp.GetPath("archives"),
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Optimal);

        Assert.True(exported.Success, exported.Message);

        BackupArchiveImportResult imported = await service.ImportArchiveAsync(exported.ArchivePath!);
        Assert.True(imported.Success, imported.Message);
        Assert.NotNull(imported.RunPath);
        Assert.True(Directory.Exists(imported.RunPath));

        string importedManifestPath = Path.Combine(imported.RunPath, "manifest.json");
        Assert.True(File.Exists(importedManifestPath));

        TransferBackupManifest importedManifest = JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(importedManifestPath))!;

        Assert.Equal(TransferBackupManifest.CurrentSchemaVersion, importedManifest.SchemaVersion);
        Assert.Single(importedManifest.Items);
        TransferOverwriteBackupItem item = importedManifest.Items[0];
        Assert.True(File.Exists(item.BackupFile));
        Assert.Equal("portable 7z save payload", File.ReadAllText(item.BackupFile));

        string basePath = history.GetBackupBasePath();
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));

        IReadOnlyList<TransferBackupRunInfo> runs = await history.GetRunsAsync();
        Assert.Single(runs);
        Assert.Equal(imported.RunPath, runs[0].BackupRootPath);
    }

    [Fact]
    public async Task RoundTrip_ExportSevenZip_Import_RestoreWithHashVerification()
    {
        using var temp = new TemporaryDirectory();
        string originalSave = temp.GetPath("saves", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(originalSave)!);
        File.WriteAllText(originalSave, "roundtrip-7z-verified-content");

        string sourceRunRoot = temp.GetPath("source-runs", "run-roundtrip");
        TransferBackupRunInfo run = TestData.CreateBackupRun(sourceRunRoot, originalSave, "roundtrip-7z-verified-content");

        var targetHistory = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("target-app", "gamesave.db")));
        var archiveService = new BackupArchiveService(targetHistory);

        BackupArchiveExportResult exported = await archiveService.ExportRunAsync(
            run,
            temp.GetPath("archives"),
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Optimal);

        Assert.True(exported.Success);

        BackupArchiveImportResult imported = await archiveService.ImportArchiveAsync(exported.ArchivePath!);
        Assert.True(imported.Success);

        IReadOnlyList<TransferBackupRunInfo> importedRuns = await targetHistory.GetRunsAsync();
        TransferBackupRunInfo importedRun = Assert.Single(importedRuns);

        // Delete original file to test restore
        File.Delete(originalSave);
        Assert.False(File.Exists(originalSave));

        var restoreService = new BackupRestoreService(
            new TransferOverwriteBackupService(
                new TestDatabasePathProvider(temp.GetPath("target-app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult restoreResult = await restoreService.RestoreAsync(
            importedRun,
            new BackupRestoreOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                VerifyHashes = true
            });

        Assert.Equal(1, restoreResult.FilesRestored);
        Assert.True(File.Exists(originalSave));
        Assert.Equal("roundtrip-7z-verified-content", File.ReadAllText(originalSave));
    }

    [Fact]
    public async Task ImportArchive_SevenZip_RejectsZipSlipPathTraversal_LeavesNoEscapedFilesOrStaging()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("malicious_slip.7z");
        string escapedTarget = temp.GetPath("escaped_file.txt");

        CreateCustomSevenZip(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "legit save data",
            ["../../escaped_file.txt"] = "malicious payload"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("security policy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(escapedTarget), "Escaped file must not be written to disk.");
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_SevenZip_RejectsConsecutiveSeparators_FailsCleanly()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("malicious_consecutive.7z");

        CreateCustomSevenZip(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "legit save data",
            ["files//consecutive.txt"] = "malicious consecutive slashes"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("security policy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_SevenZip_RejectsTooManyFileEntries_BeforeExtraction()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("too_many_entries.7z");
        var entries = new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav")
        };
        for (int i = 1; i <= 6; i++)
        {
            entries[$"files/save_{i}.sav"] = $"data {i}";
        }
        CreateCustomSevenZip(archivePath, entries);

        var bounds = new BackupArchiveSafetyBounds { MaxFileEntries = 5 };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("maximum allowed entries limit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_SevenZip_RejectsArchiveExceedingMaxManifestBytes()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("large_manifest.7z");
        string largeManifest = CreateSampleManifestJson("files/save.sav") + new string(' ', 1000);

        CreateCustomSevenZip(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = largeManifest,
            ["files/save.sav"] = "data"
        });

        var bounds = new BackupArchiveSafetyBounds { MaxManifestBytes = 256 };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("exceeds maximum allowed size", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
    }

    [Fact]
    public async Task ImportArchive_SevenZip_RejectsSingleFileExceedingLimit_CleansStaging()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("large_single_file.7z");
        string largeContent = new string('A', 5000);

        CreateCustomSevenZip(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = largeContent
        });

        var bounds = new BackupArchiveSafetyBounds { MaxSingleFileBytes = 2000 };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(archivePath);

        Assert.False(result.Success);
        Assert.Contains("exceeds maximum allowed single file size", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ExportArchive_SevenZip_CancellationCleansUpStagingArtifact()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", "run-cancel");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "cancellation payload");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        string outputDir = temp.GetPath("archives");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ExportRunAsync(
                run,
                outputDir,
                BackupContainerFormat.SevenZip,
                BackupCompressionPreset.Optimal,
                cts.Token);
        });

        if (Directory.Exists(outputDir))
        {
            Assert.Empty(Directory.GetFiles(outputDir, "*.7z"));
            Assert.Empty(Directory.GetFiles(outputDir, "*.tmp"));
        }
    }

    [Fact]
    public async Task ImportArchive_SevenZip_CancellationCleansUpStagingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string archivePath = temp.GetPath("valid.7z");
        CreateCustomSevenZip(archivePath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "save data"
        });

        var service = new BackupArchiveService(history);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ImportArchiveAsync(archivePath, cts.Token);
        });

        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static void CreateCustomSevenZip(string archivePath, IDictionary<string, string> entries)
    {
        string? dir = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var writer = new SevenZipWriter(fs, new SevenZipWriterOptions
        {
            CompressionType = CompressionType.LZMA2
        });

        foreach (var (entryName, content) in entries)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
            using var entryStream = new MemoryStream(bytes);
            writer.Write(entryName, entryStream, null);
        }
    }

    private static string CreateSampleManifestJson(params string[] relativeFiles)
    {
        var items = new List<TransferOverwriteBackupItem>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (string file in relativeFiles)
        {
            items.Add(new TransferOverwriteBackupItem(
                OriginalFile: "C:\\Saves\\save.sav",
                BackupFile: "C:\\BackupRun\\" + file.Replace('/', '\\'),
                Bytes: 100,
                Sha256: "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                BackedUpUtc: now,
                RelativePath: file));
        }

        var manifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Hardening Game",
            SteamAppId: "99999",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: now,
            CompletedUtc: now,
            FileCount: items.Count,
            TotalBytes: items.Sum(i => i.Bytes),
            Items: items,
            Format: BackupContainerFormat.SevenZip.ToString(),
            Compression: "LZMA2",
            Notes: "7-Zip test manifest");

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }
}
