using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.IO.Compression;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// OBS-005 hardened the archive <em>entry name</em> channel. The manifest travelling
/// inside the same archive is the second untrusted channel: its relative payload
/// paths are combined with the run root and its recorded absolute backup file is
/// read back at restore time. These tests assert that a manifest cannot address a
/// file outside the run it belongs to, and that the guard does not block the
/// ordinary nested-payload case.
/// </summary>
public sealed class ManifestPathContainmentTests
{
    [Theory]
    [InlineData("files/save.sav")]
    [InlineData("files/C/Steam/userdata/1/2/save.dat")]
    [InlineData(@"files\sub\save.sav")]
    public void IsSafeRelativePayloadPath_AcceptsContainedRelativePaths(string relativePath)
    {
        Assert.True(TransferOverwriteBackupItem.IsSafeRelativePayloadPath(relativePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("../../escape.txt")]
    [InlineData(@"files\..\..\escape.txt")]
    [InlineData("files/./save.sav")]
    [InlineData("files//save.sav")]
    [InlineData("files/save.sav.")]
    [InlineData("files/save.sav ")]
    [InlineData("files/save:stream.sav")]
    public void IsSafeRelativePayloadPath_RejectsEscapingOrAmbiguousPaths(string? relativePath)
    {
        Assert.False(TransferOverwriteBackupItem.IsSafeRelativePayloadPath(relativePath));
    }

    [Fact]
    public async Task ImportArchive_WithDriveRootedManifestPath_IsRefusedAndCommitsNothing()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("hostile_manifest_rooted.zip");

        // Every archive entry name is legal; only the manifest lies.
        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = ManifestJson(("C:/Windows/win.ini", @"C:\Windows\win.ini")),
            ["files/save.sav"] = "legit save data"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_WithTraversalManifestPath_IsRefusedAndCommitsNothing()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("hostile_manifest_traversal.zip");

        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = ManifestJson(
                ("files/../../../../Windows/win.ini", @"C:\BackupRun\files\save.sav")),
            ["files/save.sav"] = "legit save data"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetDirectories(basePath, ".staging_*"));
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ImportArchive_WithNestedRelativePayload_StillSucceeds()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("benign_nested.zip");

        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["manifest.json"] = ManifestJson(
                ("files/C/Games/Slot 1/save.sav", @"C:\BackupRun\files\C\Games\Slot 1\save.sav")),
            ["files/C/Games/Slot 1/save.sav"] = "nested save data"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.RunPath);

        TransferBackupManifest imported = JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(Path.Combine(result.RunPath!, "manifest.json")))!;

        string backupFile = Assert.Single(imported.Items).BackupFile;
        Assert.StartsWith(result.RunPath!, backupFile, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(backupFile));
    }

    [Fact]
    public async Task ImportArchive_WithManifestOnlyBelowTheRoot_IsRefused()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = history.GetBackupBasePath();
        Directory.CreateDirectory(basePath);

        string zipPath = temp.GetPath("nested_manifest.zip");

        // Matching the manifest by file name at any depth let an archive present one
        // description while carrying another at its root.
        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["decoy/manifest.json"] = ManifestJson(("files/save.sav", @"C:\BackupRun\files\save.sav")),
            ["files/save.sav"] = "legit save data"
        });

        var service = new BackupArchiveService(history);

        BackupArchiveImportResult result = await service.ImportArchiveAsync(zipPath);

        Assert.False(result.Success);
        Assert.Empty(await history.GetRunsAsync());
    }

    [Fact]
    public async Task ReadManifest_OnImport_IgnoresAnUnauthenticatedSidecarBesideTheArchive()
    {
        using var temp = new TemporaryDirectory();
        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        Directory.CreateDirectory(history.GetBackupBasePath());

        string runRoot = temp.GetPath("source-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            runRoot, temp.GetPath("source-game", "slot1.sav"), "real payload");

        var service = new BackupArchiveService(history);
        BackupArchiveExportResult export = await service.ExportRunAsync(
            run,
            temp.GetPath("exports"),
            BackupContainerFormat.SevenZip,
            BackupCompressionPreset.Fast);

        Assert.True(export.Success, export.Message);

        // Anyone who can drop a file next to the archive can write this.
        File.WriteAllText(
            export.ArchivePath + ".manifest.json",
            ManifestJson(("files/save.sav", @"C:\BackupRun\files\save.sav"))
                .Replace("Containment Game", "Impersonated Game"));

        var reader = new BackupMetadataReader();

        Assert.True(reader.TryReadManifest(
            export.ArchivePath!, out TransferBackupManifest? viaSidecar, out _));
        Assert.Equal("Impersonated Game", viaSidecar!.Game);

        Assert.True(reader.TryReadManifest(
            export.ArchivePath!, out TransferBackupManifest? viaArchive, out _, allowSidecar: false));
        Assert.Equal(run.Manifest.Game, viaArchive!.Game);
    }

    [Fact]
    public void ResolveBackupFile_IgnoresRecordedAbsolutePathOutsideTheRunRoot()
    {
        using var temp = new TemporaryDirectory();

        string runRoot = temp.GetPath("run");
        string payload = Path.Combine(runRoot, "files", "save.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.WriteAllText(payload, "payload");

        // A file that exists, is absolute, and has nothing to do with this run.
        string outsider = temp.GetPath("outside", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsider)!);
        File.WriteAllText(outsider, "secret");

        var item = new TransferOverwriteBackupItem(
            OriginalFile: @"C:\Games\save.sav",
            BackupFile: outsider,
            Bytes: 6,
            Sha256: "0",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "files/save.sav");

        Assert.Equal(payload, item.ResolveBackupFile(runRoot));
    }

    [Fact]
    public void ResolveBackupFile_StillPrefersTheRecordedPathInsideTheRunRoot()
    {
        using var temp = new TemporaryDirectory();

        string runRoot = temp.GetPath("run");
        string payload = Path.Combine(runRoot, "files", "save.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.WriteAllText(payload, "payload");

        var item = new TransferOverwriteBackupItem(
            OriginalFile: @"C:\Games\save.sav",
            BackupFile: payload,
            Bytes: 7,
            Sha256: "0",
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "files/save.sav");

        Assert.Equal(payload, item.ResolveBackupFile(runRoot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/save.sav")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"C:\Games\..\Windows\win.ini")]
    [InlineData(@"C:\Games\save.sav.")]
    [InlineData(@"C:\Games\\save.sav")]
    public void IsAcceptableRestoreTarget_RejectsNonCanonicalOrUnsafeDestinations(string? target)
    {
        Assert.False(BackupRestoreService.IsAcceptableRestoreTarget(target, out string? rejection));
        Assert.False(string.IsNullOrWhiteSpace(rejection));
    }

    [Theory]
    [InlineData(@"C:\Games\Slot 1\save.sav")]
    [InlineData(@"D:\Users\Player\AppData\Roaming\Game\save.dat")]
    public void IsAcceptableRestoreTarget_AcceptsOrdinaryAbsoluteDestinations(string target)
    {
        Assert.True(BackupRestoreService.IsAcceptableRestoreTarget(target, out string? rejection));
        Assert.Null(rejection);
    }

    // -------------------------------------------------------------------
    // Test Helpers
    // -------------------------------------------------------------------

    private static void CreateZip(string zipPath, IDictionary<string, string> entries)
    {
        string? dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach ((string entryName, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static string ManifestJson(params (string RelativePath, string BackupFile)[] items)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<TransferOverwriteBackupItem> manifestItems = items
            .Select(i => new TransferOverwriteBackupItem(
                OriginalFile: @"C:\Saves\save.sav",
                BackupFile: i.BackupFile,
                Bytes: 100,
                Sha256: "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                BackedUpUtc: now,
                RelativePath: i.RelativePath))
            .ToList();

        var manifest = new TransferBackupManifest(
            SchemaVersion: 2,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "Containment Game",
            SteamAppId: "99999",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: now,
            CompletedUtc: now,
            FileCount: manifestItems.Count,
            TotalBytes: manifestItems.Sum(i => i.Bytes),
            Items: manifestItems,
            Format: BackupContainerFormat.Zip.ToString(),
            Compression: "Deflate",
            Notes: "Containment test manifest");

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }
}
