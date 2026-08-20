using System.Diagnostics;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone X Task 6. Cancellation during a backoff wait.
///
/// Most of this is already covered and is cited rather than restated.
/// <c>RetryingRemoteFileSystemTests.CancellationDuringABackoff_IsHonouredAndStopsTheWork</c>
/// proves the decorator passes the caller's token into the wait and attempts
/// nothing afterwards, and
/// <c>ACancelledOperation_IsNeverTreatedAsARetryableFailure</c> proves a
/// cancellation is never mistaken for a retryable failure.
/// <c>DelayProviderTests.TheSystemDelay_ReturnsPromptlyWhenCancelledDuringTheWait</c>
/// proves the production delay abandons a thirty-second wait. Seven further
/// cancellation facts live in <c>GoogleDriveSyncProviderCancellationTests</c>.
///
/// Two things those do not cover, and this file does. The composition of the
/// real delay with the real decorator has never been measured, so "the wait is
/// abandoned rather than slept through" was inferable but not pinned. And no
/// test has driven a cancelled retry through <see cref="SyncEngine"/> to show
/// that the run copies nothing and records nothing.
/// </summary>
public sealed class RetryCancellationTests
{
    [Fact]
    public async Task ARealBackoff_IsAbandonedRatherThanSleptThrough()
    {
        var inner = new AlwaysRetryableRemoteFileSystem();
        using var cancellation = new CancellationTokenSource();

        // The real delay, and a backoff long enough that sleeping through even
        // the first wait would be unmistakable.
        IRemoteFileSystem remote = new RetryingRemoteFileSystem(
            inner,
            new SystemDelayProvider(),
            exception => exception is InvalidOperationException,
            baseDelay: TimeSpan.FromSeconds(20));

        var stopwatch = Stopwatch.StartNew();
        Task work = remote.ListRunFolderNamesAsync(cancellation.Token);

        // Cancel once the first attempt has failed and the wait has begun.
        await inner.FirstAttemptFailed.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 5_000,
            "a cancelled backoff slept through its wait");

        // And it stopped: no second attempt was made after the cancellation.
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ACancelledRetryDuringASync_CopiesNothingAndRecordsNoRun()
    {
        using var workspace = new CancelledRetryWorkspace();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workspace.RunAsync());

        // Proved positively: the remote was reached and did fail retryably, so
        // the run really was in the middle of a retry when it was cancelled.
        Assert.True(workspace.Remote.UploadAttempts > 0);
        Assert.True(workspace.Delay.Requested.Count > 0);

        // Nothing copied, and no history row for a run that never completed.
        Assert.Empty(workspace.Remote.Uploaded);
        Assert.Equal(0, workspace.History.CountRuns());
    }

    // ---- helpers ----

    private sealed class CancelledRetryWorkspace : IDisposable
    {
        private const string RunName = "2026-08-20_15-00-00_manual";

        private readonly TemporaryDirectory _root = new();
        private readonly CancellationTokenSource _cancellation = new();

        public CancelledRetryWorkspace()
        {
            Remote = new AlwaysRetryableRemoteFileSystem { FailListing = false };
            Delay = new CancellingRecordingDelayProvider(_cancellation);

            string runRoot = Path.Combine(_root.Path, "backups", RunName);
            Directory.CreateDirectory(runRoot);
            File.WriteAllText(Path.Combine(runRoot, "payload.dat"), "1234");
            File.WriteAllText(Path.Combine(runRoot, "manifest.json"), "{}");
            _runRoot = runRoot;
        }

        private readonly string _runRoot;

        public AlwaysRetryableRemoteFileSystem Remote { get; }

        public CancellingRecordingDelayProvider Delay { get; }

        public RecordingHistoryRepository History { get; } = new();

        public Task RunAsync()
        {
            IRemoteFileSystem remote = new RetryingRemoteFileSystem(
                Remote,
                Delay,
                exception => exception is InvalidOperationException);

            var engine = new SyncEngine(
                remote,
                "Cancelled remote",
                remote.DisplayRoot,
                new OneRunHistoryService(
                    Path.Combine(_root.Path, "backups"), _runRoot, RunName),
                History);

            return ExecuteAsync(engine);
        }

        private async Task ExecuteAsync(SyncEngine engine)
        {
            SyncPlan plan = await engine.CreatePreviewAsync(
                new SyncOptions(), _cancellation.Token);

            await engine.ExecuteAsync(
                plan,
                new SyncOptions { DryRun = false, ConfirmExecution = true },
                _cancellation.Token);
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _root.Dispose();
        }
    }

    /// <summary>
    /// Records the requested delay and then cancels, which is how a user
    /// pressing cancel during a backoff actually arrives.
    /// </summary>
    internal sealed class CancellingRecordingDelayProvider(
        CancellationTokenSource source) : IDelayProvider
    {
        public List<TimeSpan> Requested { get; } = [];

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            Requested.Add(duration);
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fails every upload with a retryable failure, and signals when the first
    /// attempt has failed so a test can cancel at exactly that point. Reads
    /// succeed so a plan can be built.
    /// </summary>
    internal sealed class AlwaysRetryableRemoteFileSystem : IRemoteFileSystem
    {
        public TaskCompletionSource FirstAttemptFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts { get; private set; }

        public int UploadAttempts { get; private set; }

        public List<string> Uploaded { get; } = [];

        public string DisplayRoot => "Cancelled remote";

        public string GetDisplayPath(string relativePath) =>
            $"Cancelled remote/{relativePath}";

        /// <summary>
        /// When false the listing succeeds, so a plan can be built and the
        /// cancellation can be made to land during an upload retry rather than
        /// during the preview. The first draft failed reads as well, and the
        /// run was therefore cancelled before it ever reached an upload.
        /// </summary>
        public bool FailListing { get; set; } = true;

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            if (!FailListing)
                return Task.FromResult<IReadOnlyList<string>>([]);

            Attempts++;
            FirstAttemptFailed.TrySetResult();
            throw new InvalidOperationException("The synthetic remote is unavailable.");
        }

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            UploadAttempts++;
            throw new InvalidOperationException("The synthetic remote is unavailable.");
        }

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferPreviewWarning?>(null);

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

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

    private sealed class OneRunHistoryService(
        string basePath, string runRoot, string runName) : IBackupHistoryService
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
                StartedUtc: DateTimeOffset.Parse("2026-08-20T15:00:00Z"),
                CompletedUtc: DateTimeOffset.Parse("2026-08-20T15:00:01Z"),
                FileCount: 1,
                TotalBytes: 4,
                Items: []);

            _ = runName;

            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(
            [
                new TransferBackupRunInfo(
                    runRoot, Path.Combine(runRoot, "manifest.json"), manifest)
            ]);
        }

        public string GetBackupBasePath() => basePath;
    }
}
