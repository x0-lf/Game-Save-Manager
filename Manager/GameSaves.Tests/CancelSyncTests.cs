using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone Y Task 2. Until this task every layer beneath the view model
/// honoured a cancellation token, and that was thoroughly tested, but
/// <c>SyncViewModel.ExecuteSyncAsync</c> never created one and no control was
/// bound to anything. The token they all respected was always
/// <c>default</c>, so **a running sync could not be cancelled at all**, and
/// Milestone Y's own acceptance item 16, "cancel an active upload", was
/// impossible.
/// </summary>
public sealed class CancelSyncTests
{
    [Fact]
    public async Task TheViewModel_PassesACancellableTokenToTheProvider()
    {
        var factory = new CancellationObservingFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.RemoteRootPath = @"D:\MountedBackups";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Proved, not assumed: the token the provider received can actually be
        // cancelled. Before this task it was always CancellationToken.None,
        // which is indistinguishable from a token nobody ever cancels.
        Assert.True(factory.Provider.ExecuteToken.HasValue);
        Assert.True(
            factory.Provider.ExecuteToken!.Value.CanBeCanceled,
            "the provider was handed a token that can never be cancelled");
    }

    [Fact]
    public async Task CancelSync_IsOfferedOnlyWhileASyncIsRunning()
    {
        var factory = new CancellationObservingFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.RemoteRootPath = @"D:\MountedBackups";

        Assert.False(viewModel.CanCancelSync);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // A built plan is not a running sync.
        Assert.False(viewModel.CanCancelSync);

        // While the provider is inside ExecuteAsync, the control is offered.
        factory.Provider.OnExecute = () =>
            Assert.True(
                viewModel.CanCancelSync,
                "Cancel Sync was not offered while the sync was running");

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // And withdrawn again afterwards.
        Assert.False(viewModel.CanCancelSync);
        Assert.False(viewModel.IsSyncRunning);
        Assert.False(viewModel.IsCancellingSync);
    }

    [Fact]
    public async Task CancellingARun_StopsItAndKeepsWhatWasAlreadyCopied()
    {
        var factory = new CancellationObservingFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.RemoteRootPath = @"D:\MountedBackups";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // Cancel from inside the running sync, which is how a user arrives.
        factory.Provider.OnExecute = () => viewModel.CancelSyncCommand.Execute(null);

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Proved positively: the provider observed the cancellation rather than
        // the view model merely claiming to have asked for it.
        Assert.True(factory.Provider.SawCancellation);

        Assert.Contains("Sync cancelled", viewModel.ExecutionStatusMessage);
        Assert.Contains(
            "Files already copied are kept", viewModel.ExecutionStatusMessage);
        Assert.Contains(
            "running the sync again is safe", viewModel.ExecutionStatusMessage);

        // Nothing is cleaned up, and the view model recovers rather than
        // sticking in a cancelling state.
        Assert.False(viewModel.IsSyncRunning);
        Assert.False(viewModel.IsCancellingSync);
        Assert.False(viewModel.CanCancelSync);
    }

    [Fact]
    public void CancelSync_DoesNothingWhenNothingIsRunning()
    {
        var factory = new CancellationObservingFactory();
        SyncViewModel viewModel = CreateViewModel(factory);

        viewModel.CancelSyncCommand.Execute(null);

        // No exception, no state change, and no misleading message.
        Assert.False(viewModel.IsCancellingSync);
        Assert.Equal("No sync executed.", viewModel.ExecutionStatusMessage);
    }

    [Fact]
    public void TheView_BindsCancelSyncInsideTheRunningSyncPanel()
    {
        string view = ReadSyncView();

        Assert.Contains("CancelSyncCommand", view, StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding CanCancelSync}\"", view, StringComparison.Ordinal);

        // The control must live inside the panel that is only visible while a
        // sync runs, so it cannot be pressed when there is nothing to stop.
        int panel = view.IndexOf(
            "IsVisible=\"{Binding IsSyncRunning}\"", StringComparison.Ordinal);
        int button = view.IndexOf("CancelSyncCommand", StringComparison.Ordinal);
        int nextPanel = view.IndexOf("</StackPanel>", button, StringComparison.Ordinal);

        Assert.True(panel >= 0 && button > panel && nextPanel > button);
    }

    // ---- helpers ----

    private static SyncViewModel CreateViewModel(CancellationObservingFactory factory)
    {
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.LocalFolder
        };

        var repository = new InMemorySyncRemoteProfileRepository();

        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.Parse("2026-08-20T12:00:00Z")),
            new StubGoogleDriveOAuthService());
    }

    private static string ReadSyncView()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                return File.ReadAllText(Path.Combine(
                    directory.FullName, "GameSaves.App", "Views", "SyncView.axaml"));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }

    private sealed class CancellationObservingFactory : ISyncProviderFactory
    {
        public CancellationObservingProvider Provider { get; } = new();

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot) => Provider;

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) =>
            Provider;

        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) =>
            Provider;

        public void ForgetSftpHostKey(string host, int port)
        {
        }
    }

    /// <summary>
    /// Records the token it was handed and whether it observed a cancellation,
    /// and runs a caller-supplied action from inside <c>ExecuteAsync</c> so a
    /// test can inspect or cancel while the sync is genuinely running.
    /// </summary>
    private sealed class CancellationObservingProvider : ISyncProvider
    {
        public CancellationToken? ExecuteToken { get; private set; }

        public bool SawCancellation { get; private set; }

        public Action? OnExecute { get; set; }

        public string ProviderName => "Local folder";

        public string RemoteRoot => "Observing remote";

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            var item = new SyncItem(
                RunName: "run-one",
                Action: SyncItemAction.UploadToRemote,
                ExistsLocally: true,
                ExistsRemotely: false,
                LocalPath: "local/run-one",
                RemotePath: "remote/run-one",
                GameName: "Test Game",
                FileCount: 1,
                TotalBytes: 10,
                StatusText: "Copy to remote");

            return Task.FromResult(new SyncPlan(
                ProviderName,
                RemoteRoot,
                [item],
                [],
                CanExecute: true,
                UploadCount: 1,
                DownloadCount: 0,
                InSyncCount: 0,
                ConflictCount: 0,
                BytesToUpload: 10,
                BytesToDownload: 0));
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            ExecuteToken = cancellationToken;
            OnExecute?.Invoke();

            if (cancellationToken.IsCancellationRequested)
            {
                SawCancellation = true;
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(new SyncResult(
                plan,
                DryRun: false,
                Uploaded: 1,
                Downloaded: 0,
                Skipped: 0,
                BytesCopied: 10,
                Items: [],
                Warnings: []));
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>([]);

        public void Dispose()
        {
        }
    }
}
