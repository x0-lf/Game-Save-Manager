using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;

namespace GameSaves.Tests;

/// <summary>
/// SYNC-003. One backup set uploaded to several saved profiles: a preview per
/// destination, destinations run one after another, a failure stays with its
/// destination, cancellation stops the workflow, and each destination records
/// its own history row.
/// </summary>
public sealed class MultiTargetSyncTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    // ---- coordinator ----

    [Fact]
    public async Task Preview_BuildsOnePlanPerDestination_UploadOnly_AndIsolatesAFailedPreview()
    {
        var log = new List<string>();
        var a = new ScriptedProvider("a", log);
        var b = new ScriptedProvider("b", log) { PreviewFailure = new InvalidOperationException("b is unreachable") };
        var c = new ScriptedProvider("c", log);

        using MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: true, a, b, c);

        Assert.Equal(new[] { "preview a", "preview b", "preview c" }, log);
        Assert.Equal(new[] { true, false, true }, coordinator.Previews.Select(preview => preview.CanExecute));
        Assert.Equal("b is unreachable", coordinator.Previews[1].Error);
        Assert.True(b.IsDisposed);

        foreach (ScriptedProvider provider in new[] { a, c })
        {
            SyncOptions options = Assert.Single(provider.PreviewOptions);
            Assert.True(options.DryRun);
            Assert.True(options.Upload);
            Assert.False(options.Download);
            Assert.True(options.ArchiveSync);
        }
    }

    [Fact]
    public async Task Execute_RunsDestinationsOneAfterAnother_InTheChosenOrder()
    {
        var log = new List<string>();
        var a = new ScriptedProvider("a", log) { ExecuteGate = new TaskCompletionSource() };
        var b = new ScriptedProvider("b", log);
        using MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: false, a, b);
        log.Clear();

        Task<IReadOnlyList<SyncDestinationResult>> running = coordinator.ExecuteAsync();

        // b must not start while a is still uploading.
        Assert.Equal(new[] { "start a" }, log);
        a.ExecuteGate.SetResult();
        IReadOnlyList<SyncDestinationResult> results = await running;

        Assert.Equal(new[] { "start a", "end a", "start b", "end b" }, log);
        Assert.All(results, result => Assert.Equal(SyncDestinationOutcome.Completed, result.Outcome));
        SyncOptions options = Assert.Single(a.ExecuteOptions);
        Assert.False(options.DryRun);
        Assert.True(options.ConfirmExecution);
        Assert.False(options.Download);
    }

    [Fact]
    public async Task Execute_AFailingDestination_DoesNotStopTheOthers()
    {
        var log = new List<string>();
        var a = new ScriptedProvider("a", log) { ExecuteFailure = new IOException("disk full") };
        var b = new ScriptedProvider("b", log);
        using MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: false, a, b);

        IReadOnlyList<SyncDestinationResult> results = await coordinator.ExecuteAsync();

        Assert.Equal(SyncDestinationOutcome.Failed, results[0].Outcome);
        Assert.Equal("disk full", results[0].Message);
        Assert.Equal(SyncDestinationOutcome.Completed, results[1].Outcome);
        Assert.Contains("end b", log);
    }

    [Fact]
    public async Task Execute_SkipsDestinationsWhosePreviewFailedOrFoundNothingToCopy()
    {
        var log = new List<string>();
        var failed = new ScriptedProvider("failed", log) { PreviewFailure = new InvalidOperationException("no password") };
        var empty = new ScriptedProvider("empty", log) { Plan = Plan(uploads: 0) };
        var ready = new ScriptedProvider("ready", log);
        using MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: false, failed, empty, ready);

        IReadOnlyList<SyncDestinationResult> results = await coordinator.ExecuteAsync();

        Assert.Equal(SyncDestinationOutcome.Skipped, results[0].Outcome);
        Assert.Contains("no password", results[0].Message);
        Assert.Equal(SyncDestinationOutcome.Skipped, results[1].Outcome);
        Assert.Equal(SyncDestinationOutcome.Completed, results[2].Outcome);
        Assert.Empty(failed.ExecuteOptions);
        Assert.Empty(empty.ExecuteOptions);
    }

    [Fact]
    public async Task Cancelling_StopsTheRunningDestination_AndStartsNoOther()
    {
        var log = new List<string>();
        var a = new ScriptedProvider("a", log);
        var b = new ScriptedProvider("b", log) { ExecuteGate = new TaskCompletionSource() };
        var c = new ScriptedProvider("c", log);
        using MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: false, a, b, c);
        using var cancellation = new CancellationTokenSource();
        var finished = new List<SyncDestinationOutcome>();

        Task<IReadOnlyList<SyncDestinationResult>> running = coordinator.ExecuteAsync(
            destinationFinished: result => finished.Add(result.Outcome),
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        IReadOnlyList<SyncDestinationResult> results = await running;

        Assert.Equal(
            new[] { SyncDestinationOutcome.Completed, SyncDestinationOutcome.Cancelled, SyncDestinationOutcome.NotStarted },
            results.Select(result => result.Outcome));
        Assert.Equal(results.Select(result => result.Outcome), finished);
        Assert.Empty(c.ExecuteOptions);
    }

    [Fact]
    public async Task APreviewRunsOnce_AndDisposingReleasesEveryProvider()
    {
        var log = new List<string>();
        var a = new ScriptedProvider("a", log);
        var b = new ScriptedProvider("b", log);
        MultiTargetSyncCoordinator coordinator = await Preview(archiveSync: false, a, b);

        await coordinator.ExecuteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync());

        coordinator.Dispose();
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
    }

    // The real engine per destination: each copies the run and records its
    // own history row naming its own folder; a destination that cannot run
    // is skipped without touching the others.
    [Fact]
    public async Task RealProviders_EachDestinationCopiesTheRunAndRecordsItsOwnHistory()
    {
        using var temp = new TemporaryDirectory();
        var backupHistory = new BackupHistoryService(
            new TestDatabasePathProvider(temp.GetPath("app", "gamesave.db")));
        string basePath = backupHistory.GetBackupBasePath();
        TestData.CreateBackupRun(Path.Combine(basePath, "run-1"), temp.GetPath("orig.sav"), "save data");
        var history = new RecordingHistoryRepository();

        string folderA = temp.GetPath("remote-a");
        string folderB = temp.GetPath("remote-b");
        string insideBase = Path.Combine(basePath, "not-a-remote");
        var roots = new List<(Guid Id, string Root)>
        {
            (Guid.NewGuid(), folderA),
            (Guid.NewGuid(), insideBase),
            (Guid.NewGuid(), folderB)
        };
        string RootOf(SyncDestination destination) =>
            roots.Single(pair => pair.Id == destination.ProfileId).Root;

        using MultiTargetSyncCoordinator coordinator = await MultiTargetSyncCoordinator.PreviewAsync(
            roots.Select(pair => new SyncDestination(pair.Id, Path.GetFileName(pair.Root))).ToList(),
            destination => new EngineSyncProvider(
                "Local folder",
                RootOf(destination),
                new LocalFolderRemoteFileSystem(RootOf(destination), basePath),
                backupHistory,
                history),
            archiveSync: false);

        IReadOnlyList<SyncDestinationResult> results = await coordinator.ExecuteAsync();

        Assert.Equal(
            new[] { SyncDestinationOutcome.Completed, SyncDestinationOutcome.Skipped, SyncDestinationOutcome.Completed },
            results.Select(result => result.Outcome));
        Assert.True(File.Exists(Path.Combine(folderA, "run-1", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(folderB, "run-1", "manifest.json")));
        Assert.False(Directory.Exists(insideBase));
        Assert.Equal(
            new[] { folderA, folderB },
            history.Records
                .Where(record => record.Kind == TransferRunKind.Sync)
                .Select(record => record.TargetAccountId));
    }

    // ---- the Sync page ----

    [Fact]
    public async Task SyncPage_OffersEverySavedProfileButSftp_AndUploadsToTheTickedOnes()
    {
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory, out _);

        Assert.Equal(
            new[] { "Alpha", "Beta", "Delta" },
            viewModel.MultiTargetDestinations.Select(row => row.DisplayName));
        Assert.False(viewModel.CanPreviewMultiTarget);

        Tick(viewModel, "Alpha", "Beta");
        Assert.True(viewModel.CanPreviewMultiTarget);

        await viewModel.PreviewMultiTargetUploadCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.LocalFolderCreateCount);
        Assert.Equal(@"D:\SyncA", factory.LastLocalFolderPath);
        Assert.Equal(1, factory.WebDavCreateCount);
        Assert.Equal(0, factory.OneDriveCreateCount);
        Assert.True(viewModel.HasMultiTargetPlan);
        Assert.True(viewModel.CanExecuteMultiTarget);
        Assert.Equal("Upload to 2 profile(s) (2 run(s), 20 B)", viewModel.MultiTargetActionCaption);
        Assert.All(
            viewModel.MultiTargetDestinations.Where(row => row.IsSelected),
            row => Assert.StartsWith("Upload: 1 run(s)", row.PlanText, StringComparison.Ordinal));
        Assert.Equal("", viewModel.MultiTargetDestinations.Single(row => row.DisplayName == "Delta").PlanText);

        await viewModel.ExecuteMultiTargetUploadCommand.ExecuteAsync(null);

        Assert.All(
            viewModel.MultiTargetDestinations.Where(row => row.IsSelected),
            row =>
            {
                Assert.Equal("✓", row.OutcomeGlyph);
                Assert.StartsWith("Uploaded", row.OutcomeText, StringComparison.Ordinal);
            });
        Assert.StartsWith("Finished: 2 of 2 profile(s)", viewModel.MultiTargetStatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.HasMultiTargetPlan);
        Assert.False(viewModel.IsSyncRunning);
    }

    [Fact]
    public async Task SyncPage_DiscardsThePreviewWhenTheTickedProfilesOrContainerChoiceChange()
    {
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory, out _);
        Tick(viewModel, "Alpha");

        await viewModel.PreviewMultiTargetUploadCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasMultiTargetPlan);

        Tick(viewModel, "Beta");
        Assert.False(viewModel.HasMultiTargetPlan);
        Assert.False(viewModel.CanExecuteMultiTarget);
        Assert.Contains("Preview again", viewModel.MultiTargetStatusMessage);

        await viewModel.PreviewMultiTargetUploadCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasMultiTargetPlan);

        viewModel.ArchiveSync = !viewModel.ArchiveSync;
        Assert.False(viewModel.HasMultiTargetPlan);
    }

    [Fact]
    public void SyncView_HasTheMultiProfilePanelWithNamedCheckboxes()
    {
        string xaml = CancelSyncTests.ReadSyncView();

        Assert.Contains("PanelKey=\"sync.multiTarget\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding MultiTargetDestinations}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccessibleName}\"", xaml);
        Assert.Contains("Command=\"{Binding PreviewMultiTargetUploadCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding ExecuteMultiTargetUploadCommand}\"", xaml);
        Assert.Contains("Content=\"{Binding MultiTargetActionCaption}\"", xaml);
    }

    // ---- helpers ----

    private static void Tick(SyncViewModel viewModel, params string[] names)
    {
        foreach (string name in names)
            viewModel.MultiTargetDestinations.Single(row => row.DisplayName == name).IsSelected = true;
    }

    private static SyncViewModel CreateViewModel(
        SyncProviderSelectionTests.RecordingSyncProviderFactory factory,
        out InMemorySyncRemoteProfileRepository repository)
    {
        repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile("Beta", SyncProviderKind.WebDav,
            new WebDavSyncRemoteSettings("https://dav.example.test/", "alice", "Backups")));
        repository.Create(Profile("Alpha", SyncProviderKind.LocalFolder,
            new LocalFolderSyncRemoteSettings(@"D:\SyncA")));
        repository.Create(Profile("Gamma", SyncProviderKind.Sftp,
            new SftpSyncRemoteSettings("host", 22, "alice", SftpAuthMethod.Password, null, "/srv")));
        repository.Create(Profile("Delta", SyncProviderKind.OneDrive,
            new OneDriveSyncRemoteSettings(null, OneDriveAuthorizationScopes.AppFolder)));

        SyncUiSettings settings = SyncUiSettings.Default;

        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(T0),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());
    }

    private static SyncRemoteProfile Profile(
        string name,
        SyncProviderKind kind,
        SyncRemoteProfileSettings settings) =>
        new(Guid.NewGuid(), name, kind, null, $"{name} root", settings, T0, T0, null, null, null);

    private static async Task<MultiTargetSyncCoordinator> Preview(
        bool archiveSync,
        params ScriptedProvider[] providers)
    {
        List<(Guid Id, ScriptedProvider Provider)> byId =
            providers.Select(provider => (Guid.NewGuid(), provider)).ToList();

        return await MultiTargetSyncCoordinator.PreviewAsync(
            byId.Select(pair => new SyncDestination(pair.Id, pair.Provider.ProviderName)).ToList(),
            destination => byId.Single(pair => pair.Id == destination.ProfileId).Provider,
            archiveSync);
    }

    private static SyncPlan Plan(int uploads)
    {
        SyncItem[] items = Enumerable.Range(1, uploads)
            .Select(index => new SyncItem(
                RunName: $"run-{index}",
                Action: SyncItemAction.UploadToRemote,
                ExistsLocally: true,
                ExistsRemotely: false,
                LocalPath: $"local/run-{index}",
                RemotePath: $"remote/run-{index}",
                GameName: "Test Game",
                FileCount: 1,
                TotalBytes: 10,
                StatusText: "Copy to the sync folder"))
            .ToArray();

        return new SyncPlan(
            ProviderName: "Scripted",
            RemoteRoot: "scripted",
            Items: items,
            Warnings: Array.Empty<TransferPreviewWarning>(),
            CanExecute: uploads > 0,
            UploadCount: uploads,
            DownloadCount: 0,
            InSyncCount: 0,
            ConflictCount: 0,
            BytesToUpload: uploads * 10L,
            BytesToDownload: 0);
    }

    private sealed class ScriptedProvider(string name, List<string> log) : ISyncProvider
    {
        public string ProviderName => name;

        public string RemoteRoot => name;

        public SyncPlan Plan { get; init; } = MultiTargetSyncTests.Plan(uploads: 1);

        public Exception? PreviewFailure { get; init; }

        public Exception? ExecuteFailure { get; init; }

        public TaskCompletionSource? ExecuteGate { get; init; }

        public List<SyncOptions> PreviewOptions { get; } = [];

        public List<SyncOptions> ExecuteOptions { get; } = [];

        public bool IsDisposed { get; private set; }

        public Task<SyncPlan> CreatePreviewAsync(SyncOptions options, CancellationToken cancellationToken = default)
        {
            log.Add($"preview {name}");
            PreviewOptions.Add(options);
            return PreviewFailure is null ? Task.FromResult(Plan) : Task.FromException<SyncPlan>(PreviewFailure);
        }

        public async Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            log.Add($"start {name}");
            ExecuteOptions.Add(options);

            if (ExecuteGate is not null)
                await ExecuteGate.Task.WaitAsync(cancellationToken);

            if (ExecuteFailure is not null)
                throw ExecuteFailure;

            log.Add($"end {name}");
            return new SyncResult(
                plan,
                DryRun: false,
                Uploaded: plan.UploadCount,
                Downloaded: 0,
                Skipped: 0,
                BytesCopied: plan.BytesToUpload,
                Items: Array.Empty<SyncItemResult>(),
                Warnings: Array.Empty<TransferPreviewWarning>());
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>(Array.Empty<SyncLogEntry>());

        public void Dispose() => IsDisposed = true;
    }
}
