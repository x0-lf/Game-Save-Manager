using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

public sealed class TransferSafetyTests
{
    [Fact]
    public async Task ExecuteAsync_DoesNotOverwriteAnExistingTargetByDefault()
    {
        using var temp = new TemporaryDirectory();
        string source = temp.GetPath("source.sav");
        string target = temp.GetPath("target.sav");
        File.WriteAllText(source, "new save");
        File.WriteAllText(target, "existing save");

        var backupService = new TransferOverwriteBackupService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var history = new RecordingHistoryRepository();
        var service = new SaveTransferService(backupService, history);

        SaveTransferResult result = await service.ExecuteAsync(
            TestData.CreateTransferPlan(source, target),
            new SaveTransferOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                OverwriteExisting = false
            });

        Assert.Equal("existing save", File.ReadAllText(target));
        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(SaveTransferItemStatus.SkippedTargetExists, Assert.Single(result.Items).Status);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task ExecuteAsync_BacksUpBeforeAnOptedInOverwrite()
    {
        using var temp = new TemporaryDirectory();
        string source = temp.GetPath("source.sav");
        string target = temp.GetPath("target.sav");
        File.WriteAllText(source, "new save");
        File.WriteAllText(target, "existing save");

        var backupService = new TransferOverwriteBackupService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        var service = new SaveTransferService(backupService, new ThrowingHistoryRepository());

        SaveTransferResult result = await service.ExecuteAsync(
            TestData.CreateTransferPlan(source, target),
            new SaveTransferOptions
            {
                DryRun = false,
                ConfirmExecution = true,
                OverwriteExisting = true,
                BackupBeforeOverwrite = true
            });

        Assert.Equal("new save", File.ReadAllText(target));
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesBackedUp);
        Assert.NotNull(result.BackupRootPath);

        SaveTransferItemResult item = Assert.Single(result.Items);
        Assert.NotNull(item.BackupFile);
        Assert.Equal("existing save", File.ReadAllText(item.BackupFile!));
        Assert.True(File.Exists(System.IO.Path.Combine(result.BackupRootPath!, "manifest.json")));
    }
}
