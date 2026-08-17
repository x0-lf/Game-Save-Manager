using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using System.Text.Json;

namespace GameSaves.Tests;

/// <summary>
/// Milestone T Task 6. The caller's token must reach every remote call the
/// wrapper causes, and cancelling must leave no partial local or remote state.
/// </summary>
public sealed class GoogleDriveSyncProviderCancellationTests
{
    private const string RunName = "2026-08-17_10-00-00_manual";

    [Fact]
    public async Task Preview_ForwardsTheCallerTokenToEveryRemoteCall()
    {
        var remote = new RecordingProviderRemoteFileSystem
        {
            RunFolderNames = [RunName],
            TextFileContent = JsonSerializer.Serialize(Manifest())
        };
        using ISyncProvider provider = Provider(remote);
        using var cancellation = new CancellationTokenSource();

        await provider.CreatePreviewAsync(new SyncOptions(), cancellation.Token);

        AssertEveryCallCarried(remote, cancellation.Token);
    }

    [Fact]
    public async Task SyncLog_ForwardsTheCallerTokenToEveryRemoteCall()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(remote);
        using var cancellation = new CancellationTokenSource();

        await provider.GetSyncLogAsync(cancellation.Token);

        AssertEveryCallCarried(remote, cancellation.Token);
    }

    [Fact]
    public async Task Execute_ForwardsTheCallerTokenToEveryRemoteCall()
    {
        using var local = new TemporaryDirectory();
        WriteLocalRun(local.Path);
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(
            remote,
            new StaticLocalRunHistoryService(local.Path));
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        remote.Calls.Clear();
        remote.Tokens.Clear();
        using var cancellation = new CancellationTokenSource();

        await provider.ExecuteAsync(
            plan,
            new SyncOptions { DryRun = false, ConfirmExecution = true },
            cancellation.Token);

        AssertEveryCallCarried(remote, cancellation.Token);
    }

    [Fact]
    public async Task APreCancelledToken_ReachesTheRemoteBeforeAnyWork()
    {
        var remote = new RecordingProviderRemoteFileSystem();
        using ISyncProvider provider = Provider(remote);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CreatePreviewAsync(new SyncOptions(), cancellation.Token));

        // The first remote call observes the cancelled token and stops there.
        Assert.Equal(
            nameof(IRemoteFileSystem.ValidateAsync),
            Assert.Single(remote.Calls));
    }

    [Fact]
    public async Task CancellingDuringPreview_ProducesNoPlan()
    {
        var remote = new RecordingProviderRemoteFileSystem
        {
            RunFolderNames = [RunName],
            TextFileContent = JsonSerializer.Serialize(Manifest())
        };
        using ISyncProvider provider = Provider(remote);
        using var cancellation = new CancellationTokenSource();
        remote.OnCall = member =>
        {
            if (member == nameof(IRemoteFileSystem.ListRunFolderNamesAsync))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CreatePreviewAsync(new SyncOptions(), cancellation.Token));

        Assert.DoesNotContain(
            nameof(IRemoteFileSystem.ReadTextFileAsync),
            remote.Calls);
    }

    [Fact]
    public async Task CancellingDuringAnUpload_CopiesNothingAndRecordsNoRun()
    {
        using var local = new TemporaryDirectory();
        WriteLocalRun(local.Path);
        var remote = new RecordingProviderRemoteFileSystem();
        var historyRepository = new RecordingHistoryRepository();
        using ISyncProvider provider = Provider(
            remote,
            new StaticLocalRunHistoryService(local.Path),
            historyRepository);
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        using var cancellation = new CancellationTokenSource();
        remote.Calls.Clear();
        remote.OnCall = member =>
        {
            if (member == nameof(IRemoteFileSystem.FolderExistsAsync))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true },
                cancellation.Token));

        Assert.DoesNotContain(
            nameof(IRemoteFileSystem.UploadFileAsync),
            remote.Calls);
        Assert.DoesNotContain(
            nameof(IRemoteFileSystem.CreateTextFileIfMissingAsync),
            remote.Calls);
        Assert.Empty(historyRepository.Records);
        Assert.True(
            File.Exists(Path.Combine(local.Path, RunName, "manifest.json")),
            "the local run must be untouched");
    }

    [Fact]
    public async Task CancellingDuringADownload_WritesNothingLocally()
    {
        using var local = new TemporaryDirectory();
        var remote = new RecordingProviderRemoteFileSystem
        {
            RunFolderNames = [RunName],
            TextFileContent = JsonSerializer.Serialize(Manifest())
        };
        var historyRepository = new RecordingHistoryRepository();
        using ISyncProvider provider = Provider(
            remote,
            new EmptyRunHistoryService(local.Path),
            historyRepository);
        SyncPlan plan = await provider.CreatePreviewAsync(new SyncOptions());
        using var cancellation = new CancellationTokenSource();
        remote.OnCall = member =>
        {
            if (member == nameof(IRemoteFileSystem.ListFilesAsync))
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true },
                cancellation.Token));

        Assert.DoesNotContain(
            nameof(IRemoteFileSystem.DownloadFileAsync),
            remote.Calls);
        Assert.Empty(historyRepository.Records);
        Assert.Empty(Directory.GetFileSystemEntries(local.Path));
    }

    private static void AssertEveryCallCarried(
        RecordingProviderRemoteFileSystem remote,
        CancellationToken expected)
    {
        Assert.NotEmpty(remote.Tokens);
        Assert.All(remote.Tokens, token => Assert.Equal(expected, token));
    }

    private static void WriteLocalRun(string basePath)
    {
        string runRoot = Path.Combine(basePath, RunName);
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(
            Path.Combine(runRoot, "manifest.json"),
            JsonSerializer.Serialize(Manifest()));
    }

    private static ISyncProvider Provider(
        IRemoteFileSystem remote,
        IBackupHistoryService? history = null,
        ITransferHistoryRepository? historyRepository = null) =>
        new GoogleDriveSyncProvider(
            remote,
            history ?? new EmptyBackupHistoryService(),
            historyRepository ?? new RecordingHistoryRepository());

    private static TransferBackupManifest Manifest() =>
        new(
            SchemaVersion: 1,
            Kind: "manual",
            Game: "Example Game",
            SteamAppId: "424242",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            CompletedUtc: DateTimeOffset.Parse("2026-08-17T10:01:00Z"),
            FileCount: 0,
            TotalBytes: 0,
            Items: []);

    private sealed class StaticLocalRunHistoryService(string basePath)
        : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runRoot = Path.Combine(basePath, RunName);
            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
            [
                new TransferBackupRunInfo(
                    runRoot,
                    Path.Combine(runRoot, "manifest.json"),
                    Manifest())
            ]);
        }

        public string GetBackupBasePath() => basePath;
    }

    private sealed class EmptyRunHistoryService(string basePath)
        : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>([]);
        }

        public string GetBackupBasePath() => basePath;
    }
}
