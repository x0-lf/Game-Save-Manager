using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.IO.Compression;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class ZipImportHardeningTests
{
    [Fact]
    public async Task ImportArchive_RejectsZipSlipWithTraversalSegments_LeavesNoEscapedFilesOrStaging()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("malicious_slip.zip");
        string escapedTarget = temp.GetPath("escaped_file.txt");

        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "legit save data",
            ["../../escaped_file.txt"] = "malicious payload"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("security policy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(escapedTarget), "Escaped file must not be written to disk.");
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_RejectsZipSlipWithRootedPath_FailsCleanly()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("malicious_rooted.zip");

        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "legit save data",
            ["/rooted_escape.txt"] = "malicious rooted file"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("security policy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_RejectsArchiveExceedingMaxFileEntries_BeforeExtraction()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("too_many_entries.zip");
        var entries = new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav")
        };
        for (int i = 1; i <= 6; i++)
        {
            entries[$"files/save_{i}.sav"] = $"data {i}";
        }
        CreateMaliciousZip(zipPath, entries);

        var bounds = new BackupArchiveSafetyBounds { MaxFileEntries = 5 };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("maximum allowed entries limit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_RejectsArchiveExceedingMaxManifestBytes_Cleanly()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("oversized_manifest.zip");
        string manifestJson = CreateSampleManifestJson("files/save.sav");
        string paddedManifest = manifestJson + new string(' ', 500);

        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = paddedManifest,
            ["files/save.sav"] = "save data"
        });

        var bounds = new BackupArchiveSafetyBounds { MaxManifestBytes = 200 };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("maximum allowed size", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
    }

    [Fact]
    public async Task ImportArchive_RejectsArchiveExceedingTotalUncompressedBytes_DuringStreaming()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("bomb_total.zip");
        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save1.sav", "files/save2.sav"),
            ["files/save1.sav"] = new string('A', 600),
            ["files/save2.sav"] = new string('B', 600)
        });

        var bounds = new BackupArchiveSafetyBounds
        {
            MaxTotalUncompressedBytes = 1500,
            MaxSingleFileBytes = 3000
        };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("Archive uncompressed payload exceeds maximum allowed size", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_RejectsArchiveExceedingSingleFileBytes_DuringStreaming()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("bomb_single.zip");
        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/large.sav"),
            ["files/large.sav"] = new string('X', 1200)
        });

        var bounds = new BackupArchiveSafetyBounds
        {
            MaxSingleFileBytes = 1000
        };
        var service = new BackupArchiveService(history, safetyBounds: bounds);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("exceeds maximum allowed single file size", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_AtomicRollback_WhenManifestPathsMismatch_LeavesNoPartialRun()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("mismatched_manifest.zip");
        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            // Manifest expects files/actual.sav, but archive contains files/different.sav
            ["manifest.json"] = CreateSampleManifestJson("files/actual.sav"),
            ["files/different.sav"] = "some data"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ExportRun_Cancellation_CleansUpPartialArchive_AndThrowsPromptly()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("runs", "run1");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, temp.GetPath("original.sav"), "test data");

        string destination = temp.GetPath("archives");
        Directory.CreateDirectory(destination);

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExportRunAsync(run, destination, cts.Token));

        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
        Assert.Empty(Directory.GetFiles(destination, "*.zip"));
    }

    [Fact]
    public async Task ImportArchive_Cancellation_CleansUpStaging_AndThrowsPromptly()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("valid.zip");
        CreateMaliciousZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = CreateSampleManifestJson("files/save.sav"),
            ["files/save.sav"] = "content"
        });

        var service = new BackupArchiveService(history);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ImportArchiveAsync(zipPath, cts.Token));

        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ZipExportImport_CleanRoundTrip_PreservesIntegrityAndRewritesManifest()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string runRoot = temp.GetPath("source-run");
        TransferBackupRunInfo originalRun = TestData.CreateBackupRun(
            runRoot,
            temp.GetPath("source-game", "slot1.sav"),
            "round-trip save payload");

        var service = new BackupArchiveService(history);

        string exportDir = temp.GetPath("exports");
        BackupArchiveExportResult exportResult = await service.ExportRunAsync(originalRun, exportDir);
        Assert.True(exportResult.Success, exportResult.Message);
        Assert.NotNull(exportResult.ArchivePath);
        Assert.True(File.Exists(exportResult.ArchivePath));

        BackupArchiveImportResult importResult = await service.ImportArchiveAsync(exportResult.ArchivePath);
        Assert.True(importResult.Success, importResult.Message);
        Assert.NotNull(importResult.RunPath);
        Assert.True(Directory.Exists(importResult.RunPath));

        string importedManifestPath = Path.Combine(importResult.RunPath, "manifest.json");
        Assert.True(File.Exists(importedManifestPath));

        TransferBackupManifest importedManifest = JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(importedManifestPath))!;

        Assert.Equal(TransferBackupManifest.CurrentSchemaVersion, importedManifest.SchemaVersion);
        Assert.Single(importedManifest.Items);
        TransferOverwriteBackupItem item = importedManifest.Items[0];
        Assert.True(File.Exists(item.BackupFile));
        Assert.Equal("round-trip save payload", File.ReadAllText(item.BackupFile));

        // Ensure staging directory was cleaned up and exactly 1 run exists
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        IReadOnlyList<TransferBackupRunInfo> runs = await history.GetRunsAsync();
        Assert.Single(runs);
        Assert.Equal(importResult.RunPath, runs[0].BackupRootPath);
    }

    // -------------------------------------------------------------------
    // Test Helpers
    // -------------------------------------------------------------------

    private static void CreateMaliciousZip(string zipPath, IDictionary<string, string> entries)
    {
        string? dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var (entryName, content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
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
            Format: BackupContainerFormat.Zip.ToString(),
            Compression: "Deflate",
            Notes: "Hardening test manifest");

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }
}
