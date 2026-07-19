using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class BackupRestoreArchiveTests
{
    [Fact]
    public void BackupSession_WritesOneSha256ManifestEntryWithoutOverwriting()
    {
        using var temp = new TemporaryDirectory();
        string original = temp.GetPath("saves", "slot.sav");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "save data");

        var service = new TransferOverwriteBackupService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));

        using ITransferOverwriteBackupSession session = service.BeginSession(
            new OverwriteBackupContext(
                OverwriteBackupContext.ManualKind,
                "Test Game",
                "1234",
                "source",
                "target"));

        TransferOverwriteBackupItem first = session.BackUpFile(original);
        TransferOverwriteBackupItem second = session.BackUpFile(original);
        session.Complete();

        Assert.Equal(first, second);
        Assert.Equal(1, session.FilesBackedUp);
        Assert.Equal("save data", File.ReadAllText(first.BackupFile));

        TransferBackupManifest manifest = JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(System.IO.Path.Combine(session.BackupRootPath, "manifest.json")))!;

        TransferOverwriteBackupItem item = Assert.Single(manifest.Items);
        string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.BackupFile)));
        Assert.Equal(expectedHash, item.Sha256);
    }

    [Fact]
    public async Task RestoreAsync_RejectsATamperedBackup()
    {
        using var temp = new TemporaryDirectory();
        string original = temp.GetPath("original.sav");
        string runRoot = temp.GetPath("run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, original, "trusted save");
        File.WriteAllText(run.Manifest.Items[0].BackupFile, "tampered save");

        var service = new BackupRestoreService(
            new TransferOverwriteBackupService(
                new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db"))),
            new RecordingHistoryRepository(),
            new EmptyMappingRepository(),
            new EmptySteamDiscoveryService(),
            new WindowsPlatformProvider());

        BackupRestoreResult result = await service.RestoreAsync(
            run,
            new BackupRestoreOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                VerifyHashes = true
            });

        Assert.False(File.Exists(original));
        Assert.Equal(0, result.FilesRestored);
        Assert.Equal(BackupRestoreItemStatus.SkippedHashMismatch, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task ZipExportImport_RewritesPathsAndNeverOverwritesAnExistingRun()
    {
        using var temp = new TemporaryDirectory();
        string sourceRoot = temp.GetPath("source-runs", "portable-run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(
            sourceRoot,
            temp.GetPath("original.sav"),
            "portable save");

        var history = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("target-app", "gamesave.db")));
        var service = new BackupArchiveService(history);

        BackupArchiveExportResult exported = await service.ExportRunAsync(
            run,
            temp.GetPath("archives"));

        Assert.True(exported.Success, exported.Message);

        BackupArchiveImportResult imported = await service.ImportArchiveAsync(exported.ArchivePath!);
        Assert.True(imported.Success, imported.Message);
        Assert.NotNull(imported.RunPath);

        TransferBackupManifest importedManifest = JsonSerializer.Deserialize<TransferBackupManifest>(
            File.ReadAllText(System.IO.Path.Combine(imported.RunPath!, "manifest.json")))!;

        Assert.All(importedManifest.Items, item =>
        {
            Assert.StartsWith(
                System.IO.Path.GetFullPath(imported.RunPath!) + System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.GetFullPath(item.BackupFile),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(item.BackupFile));
        });

        BackupArchiveImportResult secondImport = await service.ImportArchiveAsync(exported.ArchivePath!);
        Assert.False(secondImport.Success);
        Assert.Equal("portable save", File.ReadAllText(importedManifest.Items[0].BackupFile));
    }
}
