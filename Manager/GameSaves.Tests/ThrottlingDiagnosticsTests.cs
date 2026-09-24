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
            Exception: new InvalidOperationException("HTTP 429 Too Many Requests"),
            IsRateLimited: true);

        notifier.NotifyBackoffStarted(args);
        Assert.Same(args, started);
        Assert.NotNull(started);
        Assert.Equal(1, started.Attempt);
        Assert.Equal(4, started.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), started.Delay);
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
        Assert.False(started.IsRateLimited);
    }

    // Rate limiting comes only from the backend's typed predicate. Without
    // one nothing is rate limited, whatever the message text says.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryingRemoteFileSystem_ReportsRateLimitingOnlyFromThePredicate(bool rateLimited)
    {
        var inner = new RetryingRemoteFileSystemTests.ScriptedRemoteFileSystem { FailuresBeforeSuccess = 1 };
        var notifier = new RetryBackoffNotifier();
        var startedEvents = new List<RetryBackoffEventArgs>();
        notifier.BackoffStarted += (_, e) => startedEvents.Add(e);

        var remote = new RetryingRemoteFileSystem(
            inner,
            new RecordingDelayProvider(),
            isRetryable: ex => ex is RetryingRemoteFileSystemTests.ScriptedFailureException,
            backoffNotifier: notifier,
            isRateLimited: rateLimited ? _ => true : null);

        await remote.ListRunFolderNamesAsync();

        Assert.Equal(rateLimited, Assert.Single(startedEvents).IsRateLimited);
    }

    // Observers only watch: a subscriber that throws must not change the
    // transfer's outcome, and BackoffEnded must still follow BackoffStarted.
    [Fact]
    public async Task RetryingRemoteFileSystem_AThrowingSubscriber_StillEndsTheBackoff()
    {
        var inner = new RetryingRemoteFileSystemTests.ScriptedRemoteFileSystem { FailuresBeforeSuccess = 1 };
        var notifier = new RetryBackoffNotifier();
        int ended = 0;
        notifier.BackoffStarted += (_, _) => throw new InvalidOperationException("subscriber");
        notifier.BackoffEnded += (_, _) => ended++;

        var remote = new RetryingRemoteFileSystem(
            inner,
            new RecordingDelayProvider(),
            isRetryable: ex => ex is RetryingRemoteFileSystemTests.ScriptedFailureException,
            backoffNotifier: notifier);

        Assert.Equal(new[] { "run-one" }, await remote.ListRunFolderNamesAsync());
        Assert.Equal(1, ended);
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
            mountedPath: @"G:\My Drive",
            defaultSyncFolder: @"G:\My Drive\GameSaves");

        SyncViewModel vm = CreateViewModel(notifier, detector);

        Assert.False(vm.IsRetrying);
        Assert.Empty(vm.RetryCountdownText);

        var args = new RetryBackoffEventArgs(
            Attempt: 1,
            MaxAttempts: 4,
            Delay: TimeSpan.FromSeconds(5),
            Exception: new Exception("429 Too Many Requests"),
            IsRateLimited: true);

        notifier.NotifyBackoffStarted(args);

        Assert.True(vm.IsRetrying);
        Assert.True(vm.IsRateLimited);
        // Attempt 1 failed; the countdown is for attempt 2.
        Assert.Contains("Retrying attempt 2/4", vm.RetryCountdownText);
        Assert.Contains("Rate limited", vm.RetryCountdownText);

        notifier.NotifyBackoffEnded(args);

        Assert.False(vm.IsRetrying);
        Assert.Empty(vm.RetryCountdownText);
    }

    [Fact]
    public void SyncViewModel_SwitchToGoogleDriveDesktop_SwitchesToLocalFolderAndSuggestedPath()
    {
        var detector = new StubGoogleDriveDesktopDetector(
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
    [InlineData(@"G:\", false)]
    [InlineData(@"G:\OtherFolder", false)]
    [InlineData(@"C:\Users\Alice\Google Drive\GameSaves", false)]
    [InlineData(@"D:\Backups\NormalFolder", false)]
    public void IsTargetingGoogleDriveDesktop_EvaluatesMountedDrivePaths(string path, bool expected)
    {
        var detector = new StubGoogleDriveDesktopDetector(
            mountedPath: @"G:\My Drive",
            defaultSyncFolder: @"G:\My Drive\GameSaves");

        SyncViewModel vm = CreateViewModel(detector: detector);
        vm.SelectedProviderKind = SyncProviderKind.LocalFolder;
        vm.RemoteRootPath = path;

        Assert.Equal(expected, vm.IsTargetingGoogleDriveDesktop);
    }

    // The banner text is Google-specific and must not follow the user to
    // another provider or survive a profile switch.
    [Fact]
    public void RateLimitBanner_IsClearedWhenTheProviderChanges()
    {
        SyncViewModel vm = CreateViewModel();
        vm.SelectedProviderKind = SyncProviderKind.GoogleDrive;
        vm.IsRateLimited = true;
        Assert.True(vm.ShowGoogleDriveDesktopPromotion);

        vm.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.False(vm.IsRateLimited);
        Assert.False(vm.ShowGoogleDriveDesktopPromotion);
    }

    // Without a detected Drive mount there is nothing to switch to: a plain
    // folder would receive the backups and nothing would upload them.
    [Fact]
    public void SwitchToGoogleDriveDesktop_WithoutDetectedMount_IsDisabledAndChangesNothing()
    {
        SyncViewModel vm = CreateViewModel(
            detector: new StubGoogleDriveDesktopDetector(mountedPath: null, defaultSyncFolder: null));
        vm.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        Assert.False(vm.CanSwitchToGoogleDriveDesktop);

        vm.SwitchToGoogleDriveDesktop();

        Assert.Equal(SyncProviderKind.GoogleDrive, vm.SelectedProviderKind);
        Assert.Contains("not detected", vm.StatusMessage);
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
        string? mountedPath,
        string? defaultSyncFolder)
        : IGoogleDriveDesktopDetector
    {
        public string? MountedDrivePath { get; } = mountedPath;
        public string? DefaultSyncFolderPath { get; } = defaultSyncFolder;
    }

}
