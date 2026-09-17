using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class ThrottlingDiagnosticsTests
{
    [Fact]
    public void RetryBackoffNotifier_BroadcastsStartedAndEndedEvents()
    {
        var notifier = new RetryBackoffNotifier();
        RetryBackoffEventArgs? started = null;
        RetryBackoffEventArgs? ended = null;

        notifier.BackoffStarted += (_, e) => started = e;
        notifier.BackoffEnded += (_, e) => ended = e;

        var args = new RetryBackoffEventArgs(
            Attempt: 1,
            MaxAttempts: 4,
            Delay: TimeSpan.FromSeconds(2),
            IsServerInstructed: true,
            Exception: new InvalidOperationException("HTTP 429 Too Many Requests"),
            IsRateLimited: true);

        notifier.NotifyBackoffStarted(args);
        Assert.Same(args, started);
        Assert.NotNull(started);
        Assert.Equal(1, started.Attempt);
        Assert.Equal(4, started.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), started.Delay);
        Assert.True(started.IsServerInstructed);
        Assert.True(started.IsRateLimited);

        notifier.NotifyBackoffEnded(args);
        Assert.Same(args, ended);
        Assert.NotNull(ended);
    }

    [Fact]
    public async Task RetryingRemoteFileSystem_FiresBackoffStartedAndEndedEvents_OnRetry()
    {
        var inner = new RetryingRemoteFileSystemTests.ScriptedRemoteFileSystem { FailuresBeforeSuccess = 1 };
        var delay = new RecordingDelayProvider();
        var notifier = new RetryBackoffNotifier();

        var startedEvents = new List<RetryBackoffEventArgs>();
        var endedEvents = new List<RetryBackoffEventArgs>();

        notifier.BackoffStarted += (_, e) => startedEvents.Add(e);
        notifier.BackoffEnded += (_, e) => endedEvents.Add(e);

        var remote = new RetryingRemoteFileSystem(
            inner,
            delay,
            isRetryable: ex => ex is RetryingRemoteFileSystemTests.ScriptedFailureException,
            backoffNotifier: notifier);

        IReadOnlyList<string> result = await remote.ListRunFolderNamesAsync();

        Assert.Equal(new[] { "run-one" }, result);
        Assert.Single(startedEvents);
        Assert.Single(endedEvents);

        RetryBackoffEventArgs started = startedEvents[0];
        Assert.Equal(1, started.Attempt);
        Assert.Equal(RetryingRemoteFileSystem.DefaultMaxAttempts, started.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), started.Delay);
        Assert.False(started.IsServerInstructed);
        Assert.False(started.IsRateLimited);
    }

    [Fact]
    public async Task RetryingRemoteFileSystem_DetectsRateLimitingOnRateLimitExceptions()
    {
        var inner = new RetryingRemoteFileSystemTests.ScriptedRemoteFileSystem
        {
            FailuresBeforeSuccess = 1,
            ExceptionFactory = _ => new ScriptedRateLimitException("Google.GoogleApiException: rateLimitExceeded (429)")
        };
        var delay = new RecordingDelayProvider();
        var notifier = new RetryBackoffNotifier();

        var startedEvents = new List<RetryBackoffEventArgs>();
        notifier.BackoffStarted += (_, e) => startedEvents.Add(e);

        var remote = new RetryingRemoteFileSystem(
            inner,
            delay,
            isRetryable: ex => ex is ScriptedRateLimitException,
            backoffNotifier: notifier);

        await remote.ListRunFolderNamesAsync();

        Assert.Single(startedEvents);
        Assert.True(startedEvents[0].IsRateLimited);
    }

    [Fact]
    public void IsRateLimitException_IdentifiesRateLimitingPatterns()
    {
        Assert.True(SyncViewModel.IsRateLimitException(new Exception("429 Too Many Requests")));
        Assert.True(SyncViewModel.IsRateLimitException(new Exception("userRateLimitExceeded")));
        Assert.True(SyncViewModel.IsRateLimitException(new Exception("rateLimitExceeded")));
        Assert.True(SyncViewModel.IsRateLimitException(new RateLimitCarrierException(TimeSpan.FromSeconds(10))));
        Assert.True(SyncViewModel.IsRateLimitException(new Exception("Wrapper", new Exception("Status 429"))));

        Assert.False(SyncViewModel.IsRateLimitException(new FileNotFoundException("File not found")));
        Assert.False(SyncViewModel.IsRateLimitException(new InvalidOperationException("Generic error")));
        Assert.False(SyncViewModel.IsRateLimitException(null));
    }

    [Fact]
    public void FormatRateLimitDiagnostic_ContainsClearGuidanceAndDesktopPromotion()
    {
        string message = SyncViewModel.FormatRateLimitDiagnostic();

        Assert.Contains("Google Drive API rate limit reached", message);
        Assert.Contains("HTTP 429", message);
        Assert.Contains("Google Drive for Desktop", message);
        Assert.Contains("Local Folder", message);
        Assert.Contains("mounted drive", message);
    }

    [Fact]
    public void SyncViewModel_BackoffStartedAndEnded_UpdatesRetryingAndCountdownText()
    {
        var notifier = new RetryBackoffNotifier();
        var detector = new StubGoogleDriveDesktopDetector(
            isInstalled: true,
            mountedPath: @"G:\My Drive",
            defaultSyncFolder: @"G:\My Drive\GameSaves");

        SyncViewModel vm = CreateViewModel(notifier, detector);

        Assert.False(vm.IsRetrying);
        Assert.Empty(vm.RetryCountdownText);

        var args = new RetryBackoffEventArgs(
            Attempt: 1,
            MaxAttempts: 4,
            Delay: TimeSpan.FromSeconds(5),
            IsServerInstructed: false,
            Exception: new Exception("429 Too Many Requests"),
            IsRateLimited: true);

        notifier.NotifyBackoffStarted(args);

        Assert.True(vm.IsRetrying);
        Assert.True(vm.IsRateLimited);
        Assert.Contains("Retrying attempt 1/4", vm.RetryCountdownText);
        Assert.Contains("Rate limited", vm.RetryCountdownText);

        notifier.NotifyBackoffEnded(args);

        Assert.False(vm.IsRetrying);
        Assert.Empty(vm.RetryCountdownText);
    }

    [Fact]
    public void SyncViewModel_SwitchToGoogleDriveDesktop_SwitchesToLocalFolderAndSuggestedPath()
    {
        var detector = new StubGoogleDriveDesktopDetector(
            isInstalled: true,
            mountedPath: @"G:\My Drive",
            defaultSyncFolder: @"G:\My Drive\GameSaves");

        SyncViewModel vm = CreateViewModel(detector: detector);
        vm.SelectedProviderKind = SyncProviderKind.GoogleDrive;
        vm.IsRateLimited = true;

        Assert.True(vm.ShowGoogleDriveDesktopPromotion);

        vm.SwitchToGoogleDriveDesktop();

        Assert.Equal(SyncProviderKind.LocalFolder, vm.SelectedProviderKind);
        Assert.Equal(@"G:\My Drive\GameSaves", vm.RemoteRootPath);
        Assert.True(vm.IsTargetingGoogleDriveDesktop);
        Assert.False(vm.IsRateLimited);
        Assert.Contains("Google Drive for Desktop", vm.StatusMessage);
    }

    [Theory]
    [InlineData(@"G:\My Drive\GameSaves", true)]
    [InlineData(@"G:\", true)]
    [InlineData(@"G:\OtherFolder", true)]
    [InlineData(@"C:\Users\Alice\Google Drive\GameSaves", true)]
    [InlineData(@"D:\Backups\NormalFolder", false)]
    public void IsTargetingGoogleDriveDesktop_EvaluatesMountedDrivePaths(string path, bool expected)
    {
        var detector = new StubGoogleDriveDesktopDetector(
            isInstalled: true,
            mountedPath: @"G:\My Drive",
            defaultSyncFolder: @"G:\My Drive\GameSaves");

        SyncViewModel vm = CreateViewModel(detector: detector);
        vm.SelectedProviderKind = SyncProviderKind.LocalFolder;
        vm.RemoteRootPath = path;

        Assert.Equal(expected, vm.IsTargetingGoogleDriveDesktop);
    }

    private static SyncViewModel CreateViewModel(
        IRetryBackoffNotifier? notifier = null,
        IGoogleDriveDesktopDetector? detector = null)
    {
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        var settings = SyncUiSettings.Default;
        var repository = new InMemorySyncRemoteProfileRepository();

        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout(),
            retryBackoffNotifier: notifier,
            googleDriveDesktopDetector: detector);
    }

    private sealed class StubGoogleDriveDesktopDetector(
        bool isInstalled,
        string? mountedPath,
        string? defaultSyncFolder)
        : IGoogleDriveDesktopDetector
    {
        public bool IsInstalled { get; } = isInstalled;
        public string? MountedDrivePath { get; } = mountedPath;
        public string? DefaultSyncFolderPath { get; } = defaultSyncFolder;
    }

    private sealed class ScriptedRateLimitException(string message) : Exception(message);

    private sealed class RateLimitCarrierException(TimeSpan retryAfterDelay)
        : Exception("Rate limited with carrier"), IRetryDelayCarrier
    {
        public TimeSpan? RetryAfterDelay { get; } = retryAfterDelay;
    }
}
