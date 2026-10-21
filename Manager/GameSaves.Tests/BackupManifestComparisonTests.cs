using GameSaves.Core.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// BACKUP-009: two backup runs compared by their manifests alone. Pure and
/// offline; nothing here touches a file.
/// </summary>
public sealed class BackupManifestComparisonTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private const string HashA = "AAAA";
    private const string HashB = "BBBB";

    [Fact]
    public void Compare_ReportsEveryClass_InPathOrder_WithCountsAndBytes()
    {
        TransferBackupManifest baseline = Manifest(
            Item("files/same.sav", HashA, 10),
            Item("files/changed.sav", HashA, 20),
            Item("files/removed.sav", HashA, 30));

        TransferBackupManifest target = Manifest(
            Item("files/same.sav", HashA, 10),
            Item("files/changed.sav", HashB, 25),
            Item("files/added.sav", HashB, 40));

        BackupManifestComparisonResult result = BackupManifestComparer.Compare(baseline, target);

        Assert.False(result.IsRefused);
        Assert.Equal(
            ["files/added.sav", "files/changed.sav", "files/removed.sav", "files/same.sav"],
            result.Files.Select(file => file.RelativePath));
        Assert.Equal(
            [
                BackupManifestDifferenceKind.Added,
                BackupManifestDifferenceKind.Changed,
                BackupManifestDifferenceKind.Removed,
                BackupManifestDifferenceKind.Identical
            ],
            result.Files.Select(file => file.Kind));

        BackupManifestFileDifference changed = result.Files[1];
        Assert.Equal((20L, HashA, 25L, HashB), (changed.BaselineBytes, changed.BaselineSha256, changed.TargetBytes, changed.TargetSha256));
        Assert.Null(result.Files[0].BaselineBytes);
        Assert.Null(result.Files[2].TargetSha256);

        Assert.Equal((1, 1, 1, 1), (result.AddedCount, result.ChangedCount, result.RemovedCount, result.IdenticalCount));
        Assert.Equal((40L, 25L, 30L, 10L), (result.AddedBytes, result.ChangedBytes, result.RemovedBytes, result.IdenticalBytes));
    }

    [Fact]
    public void Compare_TreatsCaseOnlyPathDifferences_AndMetadataOnlyDifferences_AsTheSameFile()
    {
        TransferBackupManifest baseline = Manifest(Item("files/Save/Slot1.sav", HashA, 10, When));
        TransferBackupManifest target = Manifest(Item("files/save/slot1.SAV", "aaaa", 10, When.AddDays(3)));

        BackupManifestComparisonResult result = BackupManifestComparer.Compare(baseline, target);

        BackupManifestFileDifference file = Assert.Single(result.Files);
        Assert.Equal(BackupManifestDifferenceKind.Identical, file.Kind);
        Assert.Equal("files/Save/Slot1.sav", file.RelativePath);
    }

    [Fact]
    public void Compare_ReadsASchemaV1Manifest_AndASchemaV2Manifest_AsTheSameFiles()
    {
        var v1Item = new TransferOverwriteBackupItem(
            @"C:\Saves\slot1.sav", @"D:\Backups\run\files\C\Saves\slot1.sav", 10, HashA, When);
        TransferBackupManifest v1 = Manifest(v1Item) with { SchemaVersion = TransferBackupManifest.LegacySchemaVersion };
        TransferBackupManifest v2 = Manifest(Item("files/C/Saves/slot1.sav", HashA, 10));

        BackupManifestComparisonResult result = BackupManifestComparer.Compare(v1, v2);

        Assert.Equal(BackupManifestDifferenceKind.Identical, Assert.Single(result.Files).Kind);
    }

    [Fact]
    public void Compare_OfTwoEmptyManifests_IsEmpty_NotRefused()
    {
        BackupManifestComparisonResult result = BackupManifestComparer.Compare(Manifest(), Manifest());

        Assert.False(result.IsRefused);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.AddedCount + result.RemovedCount + result.ChangedCount + result.IdenticalCount);
    }

    [Theory]
    [InlineData("missing", "baseline manifest is missing")]
    [InlineData("null items", "baseline manifest is incomplete")]
    [InlineData("null hash", "target manifest is incomplete")]
    [InlineData("schema", "Unsupported manifest schema version 0")]
    [InlineData("unsafe", "unsafe payload path: files/../escape.sav")]
    [InlineData("duplicate", "lists files/TWICE.sav twice")]
    [InlineData("corrupt", "same SHA-256 but a different size")]
    public void Compare_RefusesAManifestItCannotTrust_WithTheReason(string problem, string reason)
    {
        TransferBackupManifest good = Manifest(Item("files/ok.sav", HashA, 10));
        TransferBackupManifest? baseline = good;
        TransferBackupManifest? target = good;

        switch (problem)
        {
            case "missing":
                baseline = null;
                break;
            case "null items":
                baseline = good with { Items = null! };
                break;
            case "null hash":
                target = Manifest(Item("files/ok.sav", null!, 10));
                break;
            case "schema":
                baseline = good with { SchemaVersion = 0 };
                break;
            case "unsafe":
                target = Manifest(Item("files/../escape.sav", HashA, 10));
                break;
            case "duplicate":
                baseline = Manifest(Item("files/twice.sav", HashA, 10), Item("files/TWICE.sav", HashB, 11));
                break;
            case "corrupt":
                target = Manifest(Item("files/ok.sav", HashA, 11));
                break;
        }

        BackupManifestComparisonResult result = BackupManifestComparer.Compare(baseline, target);

        Assert.True(result.IsRefused);
        Assert.Contains(reason, result.Refusal);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Compare_StopsOnCancellation_AndNeverReturnsAPartialResult()
    {
        TransferBackupManifest manifest = Manifest(Item("files/a.sav", HashA, 1), Item("files/b.sav", HashA, 1));
        using var cancellation = new CancellationTokenSource();
        var seen = new List<int>();
        var progress = new SynchronousProgress(count =>
        {
            seen.Add(count);
            cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            BackupManifestComparer.Compare(manifest, manifest, progress, cancellation.Token));

        Assert.Equal([1], seen);
    }

    [Fact]
    public void Compare_ReportsProgress_OncePerFileCompared()
    {
        TransferBackupManifest baseline = Manifest(Item("files/a.sav", HashA, 1), Item("files/b.sav", HashA, 1));
        TransferBackupManifest target = Manifest(Item("files/b.sav", HashB, 1), Item("files/c.sav", HashA, 1));
        var seen = new List<int>();

        BackupManifestComparisonResult result = BackupManifestComparer.Compare(
            baseline, target, new SynchronousProgress(seen.Add));

        Assert.Equal([1, 2, 3], seen);
        Assert.Equal(3, result.Files.Count);
    }

    [Fact]
    public void IsUsable_IsTheSyncEnginesRule_ForManifestsWithNullMembers()
    {
        TransferBackupManifest good = Manifest(Item("files/ok.sav", HashA, 10));

        Assert.True(BackupManifestComparer.IsUsable(good));
        Assert.False(BackupManifestComparer.IsUsable(null));
        Assert.False(BackupManifestComparer.IsUsable(good with { Kind = null! }));
        Assert.False(BackupManifestComparer.IsUsable(good with { Items = [null!] }));
        Assert.False(BackupManifestComparer.IsUsable(Manifest(Item("files/ok.sav", null!, 10))));
    }

    // ---- helpers ----

    private static TransferOverwriteBackupItem Item(
        string relativePath,
        string sha256,
        long bytes,
        DateTimeOffset? backedUpUtc = null) =>
        new(
            OriginalFile: "C:/Saves/" + relativePath,
            BackupFile: "D:/Backups/run/" + relativePath,
            Bytes: bytes,
            Sha256: sha256,
            BackedUpUtc: backedUpUtc ?? When,
            RelativePath: relativePath);

    private static TransferBackupManifest Manifest(params TransferOverwriteBackupItem[] items) => new(
        SchemaVersion: TransferBackupManifest.CurrentSchemaVersion,
        Kind: OverwriteBackupContext.ManualKind,
        Game: "Test Game",
        SteamAppId: "1234",
        SourceAccountId: "source",
        TargetAccountId: "target",
        StartedUtc: When,
        CompletedUtc: When.AddSeconds(1),
        FileCount: items.Length,
        TotalBytes: items.Sum(item => item.Bytes),
        Items: items);

    // Progress<T> posts to a synchronization context; the test needs the
    // report on the calling thread, in order.
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
