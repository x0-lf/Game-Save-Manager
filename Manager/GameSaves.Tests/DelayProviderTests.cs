using System.Diagnostics;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

/// <summary>
/// Milestone X Task 2. The delay seam exists so bounded retry backoff can be
/// tested without spending the delay. It lands before any retry logic, because
/// backoff added without it would make every retry test sleep in real time.
///
/// Nothing calls <see cref="IDelayProvider"/> yet, which is the point: this
/// task adds a seam and changes no behaviour.
/// </summary>
public sealed class DelayProviderTests
{
    [Fact]
    public async Task TheSystemDelay_ActuallyWaits()
    {
        var delay = new SystemDelayProvider();
        var stopwatch = Stopwatch.StartNew();

        await delay.DelayAsync(TimeSpan.FromMilliseconds(80));

        stopwatch.Stop();

        // Generous lower bound: Task.Delay never returns early, but timer
        // granularity makes an exact figure a flake waiting to happen.
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 50,
            "the production delay returned before it had waited");
    }

    [Fact]
    public async Task TheSystemDelay_ReturnsPromptlyWhenCancelledDuringTheWait()
    {
        var delay = new SystemDelayProvider();
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();

        Task waiting = delay.DelayAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        stopwatch.Stop();

        // A user cancelling a sync during a backoff must not wait it out. Thirty
        // seconds requested, and the wait is abandoned in well under one.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 5_000,
            "a cancelled delay slept out its remaining duration");
    }

    [Fact]
    public async Task TheSystemDelay_RefusesAnAlreadyCancelledToken()
    {
        var delay = new SystemDelayProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.DelayAsync(TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public void TheCompositionRoot_ResolvesTheProductionDelay()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddGameSavesInfrastructure()
            .BuildServiceProvider();

        IDelayProvider delay = provider.GetRequiredService<IDelayProvider>();

        Assert.IsType<SystemDelayProvider>(delay);
        Assert.Same(delay, provider.GetRequiredService<IDelayProvider>());
    }

    [Fact]
    public async Task TheRecordingDelay_RecordsWhatWasRequestedWithoutSpendingIt()
    {
        var delay = new RecordingDelayProvider();
        var stopwatch = Stopwatch.StartNew();

        await delay.DelayAsync(TimeSpan.FromMinutes(5));
        await delay.DelayAsync(TimeSpan.FromSeconds(30));

        stopwatch.Stop();

        Assert.Equal(
            new[] { TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30) },
            delay.Requested);

        // Five and a half minutes were requested and none of it was spent,
        // which is the whole reason the seam exists.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 1_000,
            "the recording delay actually waited");
    }

    [Fact]
    public async Task TheRecordingDelay_StillHonoursCancellation()
    {
        var delay = new RecordingDelayProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.DelayAsync(TimeSpan.FromSeconds(1), cancellation.Token));

        // A double that ignored cancellation would let a retry test pass while
        // the production path hung.
        Assert.Empty(delay.Requested);
    }

    [Fact]
    public void TheSeam_IsUsedOnlyWhereRetryIsComposed()
    {
        // Task 2 added the seam and Task 3 started using it, so this test was
        // rewritten rather than deleted: it now pins where the seam is reached
        // instead of pinning that it is unused. Waiting stays confined to the
        // retry decorator and the two places that hand it its dependency.
        string[] sources =
        [
            .. Directory.EnumerateFiles(
                ProjectDirectory("GameSaves.Core"), "*.cs", SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(
                ProjectDirectory("GameSaves.Infrastructure"), "*.cs", SearchOption.AllDirectories)
        ];

        string[] mentions = sources
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("IDelayProvider", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        // The declaration, the implementation, the registration, the one
        // decorator that waits, and the one factory that supplies it.
        Assert.Equal(
            new[]
            {
                "GoogleDriveRemoteFileSystem.cs",
                "IDelayProvider.cs",
                "RetryingRemoteFileSystem.cs",
                "ServiceCollectionExtensions.cs",
                "SystemDelayProvider.cs"
            },
            mentions);
    }

    private static string ProjectDirectory(string projectName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return Path.Combine(directory.FullName, projectName);

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}

/// <summary>
/// Records what was requested and returns at once. Cancellation is still
/// honoured, because a double that ignored it would let a retry test pass while
/// the production path hung.
/// </summary>
internal sealed class RecordingDelayProvider : IDelayProvider
{
    public List<TimeSpan> Requested { get; } = [];

    public TimeSpan Total => Requested.Aggregate(TimeSpan.Zero, (sum, next) => sum + next);

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requested.Add(duration);
        return Task.CompletedTask;
    }
}
