using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// Payload verification must only claim PayloadVerified when every byte in the
/// container is accounted for by the manifest, must not read unbounded data from a
/// hostile archive, and must not report tampering for a file it simply could not read.
/// </summary>
public sealed class PayloadVerificationHardeningTests
{
    [Fact]
    public async Task Folder_WithFileTheManifestDoesNotList_IsPayloadMismatch()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, temp.GetPath("save.dat"), "listed");
        File.WriteAllText(Path.Combine(runRoot, "files", "added.dll"), "not in the manifest");

        VerificationStrengthResult result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(run);

        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
        Assert.Contains("added.dll", result.Error);
    }

    [Fact]
    public async Task Zip_WithEntryTheManifestDoesNotList_IsPayloadMismatch()
    {
        using var temp = new TemporaryDirectory();
        TransferBackupRunInfo zipRun = await ExportZipAsync(temp, "listed");

        using (ZipArchive archive = ZipFile.Open(zipRun.BackupRootPath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("files/added.dll").Open());
            writer.Write("not in the manifest");
        }

        VerificationStrengthResult result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(zipRun);

        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
        Assert.Contains("files/added.dll", result.Error);
    }

    [Fact]
    public async Task EmptyManifest_IsNotReportedAsVerified()
    {
        using var temp = new TemporaryDirectory();
        TransferBackupRunInfo run = TestData.CreateBackupRun(temp.GetPath("run"), temp.GetPath("save.dat"), "data");
        TransferBackupRunInfo empty = run with { Manifest = run.Manifest with { Items = [] } };

        VerificationStrengthResult result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(empty);

        Assert.Equal(VerificationStrength.None, result.Strength);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DuplicateManifestItems_AreCountedOnce()
    {
        using var temp = new TemporaryDirectory();
        TransferBackupRunInfo run = TestData.CreateBackupRun(temp.GetPath("run"), temp.GetPath("save.dat"), "data");
        TransferOverwriteBackupItem item = run.Manifest.Items[0];
        TransferBackupRunInfo doubled = run with { Manifest = run.Manifest with { Items = [item, item] } };

        VerificationStrengthResult result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(doubled);

        Assert.Equal(VerificationStrength.PayloadVerified, result.Strength);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(1, result.TotalFiles);
    }

    [Fact]
    public async Task Zip_EntryThatUnderDeclaresItsSize_IsNotVerified()
    {
        using var temp = new TemporaryDirectory();
        string zipPath = temp.GetPath("bomb.zip");
        byte[] payload = Encoding.ASCII.GetBytes(new string('A', 4096));

        // The manifest carries the hash of the full content, so reading past the
        // declared size is the only way this archive could verify.
        var item = new TransferOverwriteBackupItem(
            OriginalFile: temp.GetPath("save.dat"),
            BackupFile: "files/payload.sav",
            Bytes: payload.Length,
            Sha256: Convert.ToHexString(SHA256.HashData(payload)),
            BackedUpUtc: DateTimeOffset.UtcNow,
            RelativePath: "files/payload.sav");

        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (Stream stream = archive.CreateEntry("files/payload.sav", CompressionLevel.NoCompression).Open())
                stream.Write(payload);

            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(JsonSerializer.Serialize(NewManifest(item)));
        }

        DeclareUncompressedSize(zipPath, "files/payload.sav", 16);

        var reader = new BackupMetadataReader();
        Assert.True(reader.TryBuildRunInfo(zipPath, out TransferBackupRunInfo? zipRun, out string? error), error);

        VerificationStrengthResult result = await reader.VerifyPayloadIntegrityAsync(zipRun!);

        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
    }

    [Fact]
    public void HashBounded_StopsOnceTheStreamPassesItsLimit()
    {
        using var stream = new MemoryStream(new byte[1024]);

        Assert.Null(Sha256Hasher.HashBounded(stream, 512, CancellationToken.None));

        stream.Position = 0;
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(new byte[1024])),
            Sha256Hasher.HashBounded(stream, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Folder_PayloadHeldOpenByAnotherProgram_IsUnverifiedNotMismatch()
    {
        using var temp = new TemporaryDirectory();
        TransferBackupRunInfo run = TestData.CreateBackupRun(temp.GetPath("run"), temp.GetPath("save.dat"), "data");

        VerificationStrengthResult result;
        using (new FileStream(run.Manifest.Items[0].BackupFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(run);
        }

        Assert.Equal(VerificationStrength.None, result.Strength);
        Assert.DoesNotContain("tamper", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Folder_ContainingAJunction_IsPayloadMismatch()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run");
        TransferBackupRunInfo run = TestData.CreateBackupRun(runRoot, temp.GetPath("save.dat"), "data");
        string outside = temp.GetPath("outside");
        Directory.CreateDirectory(outside);

        // A junction needs no privilege, unlike a symbolic link.
        using (Process mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{Path.Combine(runRoot, "files", "link")}\" \"{outside}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!)
        {
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }

        VerificationStrengthResult result = await new BackupMetadataReader().VerifyPayloadIntegrityAsync(run);

        Assert.Equal(VerificationStrength.PayloadMismatch, result.Strength);
        Assert.Contains("link", result.Error);
    }

    [Fact]
    public void TryBuildRunInfo_LocalFolderRun_IsUnverified()
    {
        using var temp = new TemporaryDirectory();
        string runRoot = temp.GetPath("run");
        TestData.CreateBackupRun(runRoot, temp.GetPath("save.dat"), "data");

        Assert.True(new BackupMetadataReader().TryBuildRunInfo(runRoot, out TransferBackupRunInfo? run, out string? error), error);

        Assert.Equal(VerificationStrength.None, run!.Verification);
    }

    private static async Task<TransferBackupRunInfo> ExportZipAsync(TemporaryDirectory temp, string content)
    {
        TransferBackupRunInfo run = TestData.CreateBackupRun(temp.GetPath("run"), temp.GetPath("save.dat"), content);
        var history = new BackupHistoryService(new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        BackupArchiveExportResult export = await new BackupArchiveService(history).ExportRunAsync(
            run,
            temp.GetPath("exports"),
            BackupContainerFormat.Zip,
            BackupCompressionPreset.Fast);
        Assert.True(export.Success, export.Message);

        Assert.True(new BackupMetadataReader().TryBuildRunInfo(export.ArchivePath!, out TransferBackupRunInfo? zipRun, out string? error), error);
        return zipRun!;
    }

    private static TransferBackupManifest NewManifest(TransferOverwriteBackupItem item) => new(
        SchemaVersion: 2,
        Kind: OverwriteBackupContext.ManualKind,
        Game: "Test",
        SteamAppId: "100",
        SourceAccountId: "src",
        TargetAccountId: "tgt",
        StartedUtc: DateTimeOffset.UtcNow,
        CompletedUtc: DateTimeOffset.UtcNow,
        FileCount: 1,
        TotalBytes: item.Bytes,
        Items: [item]);

    /// <summary>
    /// Rewrites the uncompressed size recorded for one entry in both its local header
    /// and its central directory record, leaving the stored bytes untouched.
    /// </summary>
    private static void DeclareUncompressedSize(string zipPath, string entryName, uint size)
    {
        byte[] zip = File.ReadAllBytes(zipPath);
        byte[] name = Encoding.ASCII.GetBytes(entryName);

        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            int sizeOffset, nameOffset;
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04)
            {
                sizeOffset = 22;
                nameOffset = 30;
            }
            else if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                sizeOffset = 24;
                nameOffset = 46;
            }
            else
            {
                continue;
            }

            if (zip.AsSpan(i + nameOffset, name.Length).SequenceEqual(name))
                BitConverter.TryWriteBytes(zip.AsSpan(i + sizeOffset, 4), size);
        }

        File.WriteAllBytes(zipPath, zip);
    }
}
