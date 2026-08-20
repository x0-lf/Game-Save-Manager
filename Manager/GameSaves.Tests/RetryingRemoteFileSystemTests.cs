using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone X Task 3. Bounded retry for failures the backend has already
/// classified as retryable.
///
/// The decorator sits at <see cref="IRemoteFileSystem"/>, so these tests need
/// no Google type and no network: a fake inner boundary fails a chosen number
/// of times and then succeeds, and a recording delay reports what the backoff
/// asked for without spending it.
/// </summary>
public sealed class RetryingRemoteFileSystemTests
{
    [Fact]
    public async Task ARetryableFailure_IsRetriedUntilItSucceeds()
    {
        var inner = new ScriptedRemoteFileSystem { FailuresBeforeSuccess = 2 };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        IReadOnlyList<string> names = await remote.ListRunFolderNamesAsync();

        // The positive result: the call succeeded, on the third attempt.
        Assert.Equal(new[] { "run-one" }, names);
        Assert.Equal(3, inner.Attempts);

        // Two waits, exponential from the one-second base.
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            delay.Requested);
    }

    [Fact]
    public async Task ANonRetryableFailure_FailsOnTheFirstAttempt()
    {
        var inner = new ScriptedRemoteFileSystem
        {
            FailuresBeforeSuccess = 1,
            Retryable = false
        };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        await Assert.ThrowsAsync<ScriptedFailureException>(
            () => remote.ListRunFolderNamesAsync());

        Assert.Equal(1, inner.Attempts);
        Assert.Empty(delay.Requested);
    }

    [Fact]
    public async Task RetryIsBounded_InAttemptsAndInTotalDelay()
    {
        var inner = new ScriptedRemoteFileSystem { FailuresBeforeSuccess = int.MaxValue };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        await Assert.ThrowsAsync<ScriptedFailureException>(
            () => remote.ListRunFolderNamesAsync());

        // Four attempts, three waits, and never more than the ceiling in total.
        Assert.Equal(RetryingRemoteFileSystem.DefaultMaxAttempts, inner.Attempts);
        Assert.Equal(3, delay.Requested.Count);
        Assert.True(
            delay.Total <= RetryingRemoteFileSystem.MaximumTotalDelay,
            "the total backoff exceeded its ceiling");
    }

    [Fact]
    public async Task AnUnreasonableBaseDelay_StillCannotExceedTheCeiling()
    {
        var inner = new ScriptedRemoteFileSystem { FailuresBeforeSuccess = int.MaxValue };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(
            inner, delay, baseDelay: TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<ScriptedFailureException>(
            () => remote.ListRunFolderNamesAsync());

        // The first wait alone would have been ten minutes. The ceiling is what
        // stops a failing sync from becoming a hang, so it is clamped and the
        // budget is then exhausted.
        Assert.Equal(
            RetryingRemoteFileSystem.MaximumTotalDelay,
            Assert.Single(delay.Requested));
        Assert.Equal(2, inner.Attempts);
    }

    [Fact]
    public async Task CancellationDuringABackoff_IsHonouredAndStopsTheWork()
    {
        var inner = new ScriptedRemoteFileSystem { FailuresBeforeSuccess = int.MaxValue };
        using var cancellation = new CancellationTokenSource();
        var delay = new CancellingDelayProvider(cancellation);
        IRemoteFileSystem remote = Wrap(inner, delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => remote.ListRunFolderNamesAsync(cancellation.Token));

        // One failed attempt, one backoff that was cancelled, and no second
        // attempt afterwards.
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ACancelledOperation_IsNeverTreatedAsARetryableFailure()
    {
        var inner = new ScriptedRemoteFileSystem { ThrowCancellation = true };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => remote.ListRunFolderNamesAsync());

        Assert.Equal(1, inner.Attempts);
        Assert.Empty(delay.Requested);
    }

    // ---- 3c: retry never converts create-only into overwrite ----

    [Fact]
    public async Task ARetriedCreate_StillRefusesAnExistingRemoteObject()
    {
        // The realistic hazard: the first attempt created the object and then
        // failed to report back, so the retry finds it present.
        var inner = new ScriptedRemoteFileSystem
        {
            FailuresBeforeSuccess = 1,
            CreateSucceedsThenReportsExisting = true
        };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => remote.CreateTextFileIfMissingAsync("run/manifest.json", "content"));

        // It was retried, and the retry refused rather than replacing. Failing
        // is the correct outcome; never replacing existing content outranks
        // completing the run.
        Assert.Equal(2, inner.Attempts);
        Assert.Equal("content", inner.CreatedContent);
        Assert.Equal(1, inner.CreateCount);
    }

    [Fact]
    public async Task ARetriedUpload_UploadsTheSameFileOnceItSucceeds()
    {
        var inner = new ScriptedRemoteFileSystem { FailuresBeforeSuccess = 1 };
        var delay = new RecordingDelayProvider();
        IRemoteFileSystem remote = Wrap(inner, delay);

        long bytes = await remote.UploadFileAsync(@"C:\local\save.dat", "run/save.dat");

        Assert.Equal(42, bytes);
        Assert.Equal(2, inner.Attempts);
        Assert.Equal("run/save.dat", inner.LastUploadTarget);
    }

    [Fact]
    public void ThePassThroughMembers_AreNotWrapped()
    {
        var inner = new ScriptedRemoteFileSystem();
        IRemoteFileSystem remote = Wrap(inner, new RecordingDelayProvider());

        // DisplayRoot and GetDisplayPath perform no remote work, so retrying
        // them would be meaningless. They must still report the inner values.
        Assert.Equal(inner.DisplayRoot, remote.DisplayRoot);
        Assert.Equal(inner.GetDisplayPath("a/b"), remote.GetDisplayPath("a/b"));
    }

    [Fact]
    public void TheDecorator_RefusesAnImpossibleConfiguration()
    {
        var inner = new ScriptedRemoteFileSystem();
        var delay = new RecordingDelayProvider();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryingRemoteFileSystem(inner, delay, _ => true, maxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryingRemoteFileSystem(
                inner, delay, _ => true, baseDelay: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(
            () => new RetryingRemoteFileSystem(inner, delay, null!));
    }

    private static IRemoteFileSystem Wrap(
        IRemoteFileSystem inner,
        IDelayProvider delay,
        TimeSpan? baseDelay = null) =>
        new RetryingRemoteFileSystem(
            inner,
            delay,
            exception => exception is ScriptedFailureException { Retryable: true },
            baseDelay: baseDelay);

    private sealed class ScriptedFailureException(bool retryable) : Exception("scripted")
    {
        public bool Retryable { get; } = retryable;
    }

    /// <summary>
    /// Fails a chosen number of times before succeeding, counting attempts.
    /// Only the members these tests exercise do anything; the rest are present
    /// because the interface requires them.
    /// </summary>
    private sealed class ScriptedRemoteFileSystem : IRemoteFileSystem
    {
        public int FailuresBeforeSuccess { get; set; }

        public bool Retryable { get; set; } = true;

        public bool ThrowCancellation { get; set; }

        public bool CreateSucceedsThenReportsExisting { get; set; }

        public int Attempts { get; private set; }

        public int CreateCount { get; private set; }

        public string? CreatedContent { get; private set; }

        public string? LastUploadTarget { get; private set; }

        public string DisplayRoot => "Scripted remote";

        public string GetDisplayPath(string relativePath) =>
            $"Scripted remote/{relativePath}";

        private void Begin()
        {
            Attempts++;

            if (ThrowCancellation)
                throw new OperationCanceledException();

            if (Attempts <= FailuresBeforeSuccess)
                throw new ScriptedFailureException(Retryable);
        }

        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult<IReadOnlyList<string>>(new[] { "run-one" });
        }

        public Task CreateTextFileIfMissingAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            if (CreateSucceedsThenReportsExisting && Attempts == 0)
            {
                // The object really is created, and then the call fails.
                CreateCount++;
                CreatedContent = content;
                Attempts++;
                throw new ScriptedFailureException(Retryable);
            }

            Begin();

            if (CreateSucceedsThenReportsExisting)
            {
                throw new InvalidOperationException(
                    "The remote object already exists.");
            }

            CreateCount++;
            CreatedContent = content;
            return Task.CompletedTask;
        }

        public Task<long> UploadFileAsync(
            string localFilePath,
            string relativeRemotePath,
            CancellationToken cancellationToken = default)
        {
            Begin();
            LastUploadTarget = relativeRemotePath;
            return Task.FromResult(42L);
        }

        public Task<TransferPreviewWarning?> ValidateAsync(
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult<TransferPreviewWarning?>(null);
        }

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult(true);
        }

        public Task<bool> FolderExistsAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult(true);
        }

        public Task<string?> ReadTextFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult<string?>(null);
        }

        public Task<string?> ReadProviderMetadataAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult<string?>(null);
        }

        public Task ReplaceProviderMetadataAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(
            string relativeFolder,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<long> DownloadFileAsync(
            string relativeRemotePath,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            Begin();
            return Task.FromResult(42L);
        }
    }

    /// <summary>
    /// Cancels the token at the moment the backoff is awaited, which is how a
    /// user pressing cancel during a wait actually arrives.
    /// </summary>
    private sealed class CancellingDelayProvider(CancellationTokenSource source)
        : IDelayProvider
    {
        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
