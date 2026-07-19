using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

public sealed class HistoryTests
{
    [Fact]
    public void SqliteHistory_RoundTripsRunsAndItems()
    {
        using var temp = new TemporaryDirectory();
        var repository = new SqliteTransferHistoryRepository(temp.GetPath("data", "gamesave.db"));
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-1);
        DateTimeOffset completed = DateTimeOffset.UtcNow;

        long id = repository.RecordRun(new TransferRunRecord(
            Kind: TransferRunKind.Sync,
            GameName: "(backup sync)",
            SteamAppId: "-",
            SourceAccountId: "device",
            TargetAccountId: "remote",
            DryRun: false,
            OverwriteEnabled: false,
            BackupEnabled: false,
            FilesConsidered: 1,
            FilesCopied: 1,
            FilesSkipped: 0,
            FilesFailed: 0,
            BytesCopied: 42,
            FilesBackedUp: 0,
            BackupRootPath: null,
            BlockedReason: null,
            StartedUtc: started,
            CompletedUtc: completed,
            Items: new[]
            {
                new TransferRunItemRecord(
                    "source", "target", 42, true, "Uploaded", null, null)
            }));

        Assert.Equal(1, repository.CountRuns());

        TransferRunInfo run = Assert.Single(repository.GetRecentRuns(10));
        Assert.Equal(id, run.Id);
        Assert.Equal(TransferRunKind.Sync, run.Kind);
        Assert.Equal(42, run.BytesCopied);

        TransferRunItemRecord item = Assert.Single(repository.GetRunItems(id));
        Assert.True(item.Copied);
        Assert.Equal("Uploaded", item.Status);
    }
}
