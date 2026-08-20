using GameSaves.App.Models;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone X Task 5. The engine's behaviour when a run stops partway was
/// already correct and already covered: payloads are kept, no manifest is
/// written, nothing is repaired, and the run is never offered for download.
/// What was missing was telling the user, and telling them the right thing.
///
/// Before this task a partial run reported <c>Failed</c> with zero bytes, so a
/// user could not tell "this was interrupted and retrying is safe" from "this
/// failed", and was told nothing had been copied while a partial run sat on the
/// far side.
/// </summary>
public sealed class IncompleteTransferReportingTests
{
    private const string PartialRun = "2026-08-20_14-00-00_manual";

    [Fact]
    public async Task AnInterruptedUpload_IsReportedAsIncompleteWithTheBytesItCopied()
    {
        using var workspace = new PartialUploadWorkspace(failOn: "second.dat");

        SyncResult result = await workspace.RunAsync();

        SyncItemResult partial = Assert.Single(result.Items);

        // The distinction Task 5 exists to make.
        Assert.Equal(SyncItemStatus.Incomplete, partial.Status);
        Assert.NotEqual(SyncItemStatus.Failed, partial.Status);

        // And the honest byte count: one file did land.
        Assert.True(partial.Bytes > 0, "an incomplete run reported no bytes at all");
        Assert.Single(workspace.Remote.Uploaded);

        // A user can act on this without reading the code.
        Assert.Contains("stopped partway", partial.Error);
        Assert.Contains("Nothing was deleted or replaced", partial.Error);
        Assert.Contains("running the sync again is safe", partial.Error);
    }

    [Fact]
    public async Task ARunThatCopiedNothing_IsStillReportedAsFailed()
    {
        // Non-vacuity for the test above: Incomplete is a real distinction, not
        // a rename of Failed. A run that transferred nothing keeps the old
        // status, because retrying it is a different situation from resuming a
        // partial one.
        using var workspace = new PartialUploadWorkspace(failOn: "first.dat");

        SyncResult result = await workspace.RunAsync();

        SyncItemResult failed = Assert.Single(result.Items);

        Assert.Equal(SyncItemStatus.Failed, failed.Status);
        Assert.Equal(0, failed.Bytes);
        Assert.Empty(workspace.Remote.Uploaded);
    }

    [Fact]
    public async Task AnIncompleteRun_IsNotACleanResult()
    {
        using var workspace = new PartialUploadWorkspace(failOn: "second.dat");

        SyncResult result = await workspace.RunAsync();

        // Incomplete had to join HasErrors. A partial run destroyed nothing,
        // but calling it clean would hide it from every caller that checks.
        Assert.True(result.HasErrors);
        Assert.Equal(0, result.Uploaded);
    }

    [Fact]
    public async Task AnIncompleteRun_IsLeftExactlyAsItWas()
    {
        using var workspace = new PartialUploadWorkspace(failOn: "second.dat");

        await workspace.RunAsync();

        // 5b: nothing is deleted, nothing is repaired, and in particular no
        // manifest is invented for the partial run.
        string uploaded = Assert.Single(workspace.Remote.Uploaded);
        Assert.Equal($"{PartialRun}/first.dat", uploaded);
        Assert.DoesNotContain(
            workspace.Remote.Uploaded,
            path => path.EndsWith("manifest.json", StringComparison.Ordinal));
        Assert.Empty(workspace.Remote.Deleted);
    }

    [Fact]
    public void TheUiRow_ShowsIncompleteAsItsOwnStatus()
    {
        var item = new SyncItem(
            RunName: PartialRun,
            Action: SyncItemAction.UploadToRemote,
            ExistsLocally: true,
            ExistsRemotely: false,
            LocalPath: "local",
            RemotePath: "remote",
            GameName: "Test Game",
            FileCount: 2,
            TotalBytes: 8,
            StatusText: "Copy to remote");

        var row = new SyncItemResultRowViewModel(
            new SyncItemResult(item, 4, SyncItemStatus.Incomplete, "stopped partway"));

        // The row binds Status.ToString(), so the new value reaches the UI with
        // no view change at all. Pinning it here means a rename of the enum
        // member is a visible decision rather than a silent one.
        Assert.Equal("Incomplete", row.Status);
        Assert.Equal("stopped partway", row.Error);
    }

    // ---- helpers ----

    /// <summary>
    /// A local run of two files against a remote that fails on a chosen file
    /// name, so the run stops after copying some of it or none of it.
    /// </summary>
    private sealed class PartialUploadWorkspace : IDisposable
    {
        private readonly TemporaryDirectory _root = new();

        public PartialUploadWorkspace(string failOn)
        {
            Remote = new FailingRemoteFileSystem(failOn);

            string runRoot = Path.Combine(_root.Path, "backups", PartialRun);
            Directory.CreateDirectory(runRoot);
            File.WriteAllText(Path.Combine(runRoot, "first.dat"), "1111");
            File.WriteAllText(Path.Combine(runRoot, "second.dat"), "2222");
            File.WriteAllText(Path.Combine(runRoot, "manifest.json"), "{}");

            History = new SingleRunHistoryService(
                Path.Combine(_root.Path, "backups"), runRoot);
        }

        public FailingRemoteFileSystem Remote { get; }

        private SingleRunHistoryService History { get; }

        public async Task<SyncResult> RunAsync()
        {
            var engine = new SyncEngine(
                Remote,
                "Failing remote",
                Remote.DisplayRoot,
                History,
                new RecordingHistoryRepository());

            SyncPlan plan = await engine.CreatePreviewAsync(new SyncOptions());

            return await engine.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true });
        }

        public void Dispose() => _root.Dispose();
    }

    /// <summary>
    /// An empty remote that records every upload and throws on one chosen file
    /// name. Deletions are recorded too, so a test can prove none happened.
    /// </summary>
    private sealed class FailingRemoteFileSystem(string failOn) : IRemoteFileSystem
    {
        public List<string> Uploaded { get; } = [];

        public List<string> Deleted { get; } = [];

        public string DisplayRoot => "Failing remote";

        public string GetDisplayPath(string relativePath) =>
            $"Failing remote/{relativePath}";

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            if (relativeRemotePath.EndsWith(failOn, StringComparison.Ordinal))
                throw new IOException("The synthetic remote refused this file.");

            Uploaded.Add(relativeRemotePath);
            return Task.FromResult(4L);
        }

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }

    private sealed class SingleRunHistoryService(string basePath, string runRoot)
        : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            var manifest = new TransferBackupManifest(
                SchemaVersion: 1,
                Kind: OverwriteBackupContext.ManualKind,
                Game: "Test Game",
                SteamAppId: "4321",
                SourceAccountId: "source",
                TargetAccountId: "target",
                StartedUtc: DateTimeOffset.Parse("2026-08-20T14:00:00Z"),
                CompletedUtc: DateTimeOffset.Parse("2026-08-20T14:00:01Z"),
                FileCount: 2,
                TotalBytes: 8,
                Items: []);

            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
            [
                new TransferBackupRunInfo(
                    runRoot, Path.Combine(runRoot, "manifest.json"), manifest)
            ]);
        }

        public string GetBackupBasePath() => basePath;
    }
}
