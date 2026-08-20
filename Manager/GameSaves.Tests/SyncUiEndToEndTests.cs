using System.Text.Json;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Platform;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone W Task 2. Every other view-model test builds
/// <see cref="SyncViewModel"/> on
/// <see cref="SyncProviderSelectionTests.RecordingSyncProviderFactory"/>, whose
/// fake provider returns a fixed one-item plan and copies nothing. So the path
/// from a UI command, through the Core factory, into the real engine and back
/// out into bound state had no deterministic coverage at all: only the
/// Milestone V live acceptance ever drove it, and that was a one-off manual run.
///
/// These tests drive the real <see cref="SyncProviderFactory"/> and the real
/// <see cref="LocalFolderSyncProvider"/> against temporary directories the test
/// creates and deletes. Nothing outside those directories is read or written,
/// and no network, account, or SSH server is involved.
/// </summary>
public sealed class SyncUiEndToEndTests
{
    private const string LocalOnlyRun = "2026-08-20_09-00-00_manual";
    private const string RemoteOnlyRun = "2026-08-20_10-00-00_manual";
    private const string SharedRun = "2026-08-20_11-00-00_manual";
    private const string SecondLocalOnlyRun = "2026-08-20_12-00-00_manual";
    private const string UnreadableRemoteRun = "2026-08-20_13-00-00_manual";

    [Fact]
    public async Task ViewModelPreview_UsesTheRealLocalFolderProvider()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // A double could report anything; only the real provider reports this
        // name, and only a real scan of the two directories produces these
        // counts. The fake used everywhere else returns one upload and nothing
        // else, so none of this could pass against it.
        Assert.Equal(
            "Local folder",
            Assert.IsType<LocalFolderSyncProvider>(
                workspace.Factory.CreateLocalFolderProvider(workspace.RemoteRoot))
                .ProviderName);

        Assert.Equal(3, viewModel.Items.Count);
        Assert.True(viewModel.CanExecuteSync);
        Assert.Contains("Upload: 1 run(s)", viewModel.SummaryDisplay);
        Assert.Contains("Download: 1 run(s)", viewModel.SummaryDisplay);
        Assert.Contains("In sync: 1", viewModel.SummaryDisplay);

        Assert.Equal(
            new[] { LocalOnlyRun, RemoteOnlyRun, SharedRun },
            viewModel.Items
                .Select(row => row.RunName)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ViewModelExecute_ActuallyMovesBytesInBothDirections()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // The point of the whole task: bytes on disk, not a status message. A
        // status message is what would still look healthy if the engine were
        // never reached.
        Assert.Equal(
            Workspace.PayloadFor(LocalOnlyRun),
            File.ReadAllText(workspace.RemotePayload(LocalOnlyRun)));
        Assert.Equal(
            Workspace.PayloadFor(RemoteOnlyRun),
            File.ReadAllText(workspace.LocalPayload(RemoteOnlyRun)));

        Assert.True(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));
        Assert.True(File.Exists(workspace.LocalManifest(RemoteOnlyRun)));

        Assert.Equal(1, viewModel.ExecutionResults.Count(
            row => string.Equals(row.RunName, LocalOnlyRun, StringComparison.Ordinal)));
        Assert.Equal(1, viewModel.ExecutionResults.Count(
            row => string.Equals(row.RunName, RemoteOnlyRun, StringComparison.Ordinal)));
        Assert.Contains("Uploaded 1 run(s)", viewModel.ExecutionStatusMessage);
        Assert.Contains("downloaded 1 run(s)", viewModel.ExecutionStatusMessage);
    }

    [Fact]
    public async Task ViewModelExecute_LeavesTheAlreadySyncedRunUntouched()
    {
        using var workspace = new Workspace();
        DateTime before = File.GetLastWriteTimeUtc(workspace.RemotePayload(SharedRun));
        string contentBefore = File.ReadAllText(workspace.RemotePayload(SharedRun));

        SyncViewModel viewModel = workspace.CreateViewModel();
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Non-vacuity first. A provider that copied nothing at all would
        // satisfy "the shared run is untouched" trivially, so prove the run
        // actually happened before asserting what it left alone.
        Assert.True(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));
        Assert.True(File.Exists(workspace.LocalManifest(RemoteOnlyRun)));
        Assert.Contains("Uploaded 1 run(s)", viewModel.ExecutionStatusMessage);

        // Upload is create-only and download never overwrites, so the run that
        // already exists on both sides must be left exactly as it was.
        Assert.Equal(contentBefore, File.ReadAllText(workspace.RemotePayload(SharedRun)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(workspace.RemotePayload(SharedRun)));
    }

    [Fact]
    public async Task ViewModelExecute_WithoutConfirmation_CopiesNothing()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = false;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.False(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));
        Assert.False(File.Exists(workspace.LocalManifest(RemoteOnlyRun)));
        Assert.Equal(
            "Sync blocked. Confirm the checkbox first.",
            viewModel.ExecutionStatusMessage);

        // Non-vacuity: the same view model and the same plan, with only the
        // confirmation changed, does copy. Without this the assertions above
        // pass against any provider that never copies anything, which is
        // exactly what the fake used elsewhere does.
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.True(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));
        Assert.True(File.Exists(workspace.LocalManifest(RemoteOnlyRun)));
    }

    // ---- W Task 3: the same, through the hermetic Google Drive composition ----

    [Fact]
    public async Task DriveViewModelExecute_MovesBytesThroughTheRealDriveWrapper()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateDriveViewModel();

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        Assert.True(viewModel.CanUseGoogleDriveForSync);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Bytes on the far side, exactly as the Local Folder task asserts. The
        // wrapper is the real GoogleDriveSyncProvider, built by the real
        // internal factory from the saved profile.
        Assert.Equal(
            Workspace.PayloadFor(LocalOnlyRun),
            File.ReadAllText(workspace.RemotePayload(LocalOnlyRun)));
        Assert.Equal(
            Workspace.PayloadFor(RemoteOnlyRun),
            File.ReadAllText(workspace.LocalPayload(RemoteOnlyRun)));

        // Construction was keyed by the saved profile ID, which is the whole
        // point of the Drive case in CreateConfiguredProvider.
        Assert.Equal(
            new[] { workspace.DriveProfile.Id },
            workspace.RequestedDriveProfileIds);
    }

    [Fact]
    public async Task DriveAndLocalFolder_LeaveIdenticalStateThroughTheSameUiPath()
    {
        using var driveSide = new Workspace();
        using var localSide = new Workspace();

        SyncViewModel drive = driveSide.CreateDriveViewModel();
        await drive.GoogleAuthenticationInitializationTask;
        await drive.GoogleRootFolderInitializationTask;

        UiRunState driveState = await RunAsync(drive);
        UiRunState localState = await RunAsync(localSide.CreateViewModel());

        // Non-vacuity: parity over a run that copied nothing would prove
        // nothing at all.
        Assert.Equal(3, localState.ItemCount);
        Assert.Equal(2, localState.ExecutionResultCount);
        Assert.Contains("Uploaded 1 run(s)", localState.ExecutionStatusMessage);

        Assert.Equal(localState, driveState);

        // And the same bytes on disk on both sides, not merely the same
        // bound state.
        Assert.Equal(localSide.RemoteTree(), driveSide.RemoteTree());
        Assert.Equal(localSide.LocalTree(), driveSide.LocalTree());
    }

    /// <summary>
    /// The bound state one sync run produces, with nothing provider-specific
    /// in it. The provider name is deliberately excluded: it is the one field
    /// that must differ.
    /// </summary>
    private sealed record UiRunState(
        int ItemCount,
        string SummaryDisplay,
        bool CanExecuteSync,
        int WarningCount,
        int ExecutionResultCount,
        string ExecutionStatusMessage,
        // Joined rather than an array: a record compares array members by
        // reference, so two equal sequences would never match.
        string ResultRunNames);

    private static async Task<UiRunState> RunAsync(SyncViewModel viewModel)
    {
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        return new UiRunState(
            viewModel.Items.Count,
            viewModel.SummaryDisplay,
            viewModel.CanExecuteSync,
            viewModel.Warnings.Count,
            viewModel.ExecutionResults.Count,
            viewModel.ExecutionStatusMessage,
            string.Join(
                "|",
                viewModel.ExecutionResults
                    .Select(row => row.RunName)
                    .OrderBy(name => name, StringComparer.Ordinal)));
    }

    // ---- W Task 4: selection, progress, and warnings against a real engine ----

    [Fact]
    public async Task UntickedRun_IsLeftAloneByTheRealEngine()
    {
        using var workspace = new Workspace();
        workspace.AddLocalRun(SecondLocalOnlyRun);

        SyncViewModel viewModel = workspace.CreateViewModel();
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // Two runs are now waiting to upload. Untick one.
        Assert.Equal(2, viewModel.Items.Count(row =>
            row.IsSelectable && row.Item.Action == SyncItemAction.UploadToRemote));

        foreach (var row in viewModel.Items)
        {
            row.IncludeInSync = row.IsSelectable &&
                !string.Equals(row.RunName, SecondLocalOnlyRun, StringComparison.Ordinal);
        }

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Non-vacuity: the ticked run really was copied, so the untouched
        // assertion below is about selection rather than about a run that
        // did nothing.
        Assert.True(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));

        Assert.False(Directory.Exists(
            Path.Combine(workspace.RemoteRoot, SecondLocalOnlyRun)));

        // The unticked run is not silently absent from the results: the engine
        // reports it as deliberately skipped, having copied nothing. That is
        // better than omitting it, because a user who unticked by accident can
        // still see what happened.
        SyncItemResultRowViewModel skipped = Assert.Single(
            viewModel.ExecutionResults,
            row => string.Equals(
                row.RunName, SecondLocalOnlyRun, StringComparison.Ordinal));

        Assert.Equal("SkippedDeselected", skipped.Status);
        Assert.Equal(0, skipped.Result.Bytes);
    }

    [Fact]
    public async Task Progress_AdvancesAgainstRealBytesAndEndsComplete()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Progress<T> marshals its callbacks, so the last report can still be
        // queued when ExecuteAsync returns. Wait for it rather than racing it;
        // a bare assertion here would be intermittently green.
        Assert.True(
            await WaitUntilAsync(() =>
                viewModel.ProgressMax > 1 &&
                viewModel.ProgressValue >= viewModel.ProgressMax),
            "progress never reached its maximum");

        // The completion text is set synchronously after the engine returns,
        // so it needs no wait.
        Assert.StartsWith("Done:", viewModel.ProgressText, StringComparison.Ordinal);
        Assert.False(viewModel.IsSyncRunning);

        // Nothing private reaches the progress line.
        Assert.DoesNotContain("://", viewModel.ProgressText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            workspace.RemoteRoot, viewModel.ProgressText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            workspace.LocalBase, viewModel.ProgressText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEngineWarning_ReachesTheBoundWarningsAndDeletesNothing()
    {
        using var workspace = new Workspace();
        string ignored = workspace.AddUnreadableRemoteRun(UnreadableRemoteRun);

        SyncViewModel viewModel = workspace.CreateViewModel();
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // The warning is produced by SyncEngine while reading the remote side,
        // not by anything this test wrote into the view model. The code is
        // RemoteRunUnreadable rather than RemoteManifestUnreadable: the engine
        // has both, and this is the branch a folder with a safe run name and a
        // corrupt manifest takes.
        TransferWarningRowViewModel warning = Assert.Single(
            viewModel.Warnings,
            row => string.Equals(
                row.Code, "RemoteRunUnreadable", StringComparison.Ordinal));

        Assert.Equal(TransferWarningSeverity.Warning, warning.Severity);

        // Non-vacuity: the plan is otherwise healthy and still executable, so
        // the warning is an additional finding rather than a failed preview.
        Assert.True(viewModel.CanExecuteSync);
        Assert.Equal(3, viewModel.Items.Count);

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // "Nothing is deleted automatically" is the promise the warning itself
        // makes. Hold the engine to it.
        Assert.True(File.Exists(ignored));
    }

    [Fact]
    public async Task TheSameEngineWarning_CarriesNoIdentifierOnTheDrivePath()
    {
        using var workspace = new Workspace();
        workspace.AddUnreadableRemoteRun(UnreadableRemoteRun);

        SyncViewModel viewModel = workspace.CreateDriveViewModel();
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // Non-vacuity: the same warning really is raised on this path too.
        TransferWarningRowViewModel warning = Assert.Single(
            viewModel.Warnings,
            row => string.Equals(
                row.Code, "RemoteRunUnreadable", StringComparison.Ordinal));

        // The Local Folder wording embeds the remote display path, which is the
        // user's own folder and is fine there. On the Drive path the same
        // sentence must not carry the folder identifier.
        Assert.DoesNotContain(
            Workspace.DriveRootFolderId, warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", warning.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>
    /// Polls a condition briefly. Used only where the production code marshals
    /// a callback, so the value is correct but not yet applied when the awaited
    /// command returns.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>
    /// A local backup base and a remote folder, both inside one temporary
    /// directory that is deleted on dispose. Three runs: one only local, one
    /// only remote, one on both sides.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        public const string DriveRootFolderId = "end-to-end-root-folder-id";

        private const string RootFolderId = DriveRootFolderId;

        private static readonly DateTimeOffset Clock =
            DateTimeOffset.Parse("2026-08-20T12:00:00Z");

        private readonly TemporaryDirectory _root = new();

        private readonly WorkspaceRemoteFileSystemFactory _driveFileSystems;

        public Workspace()
        {
            Directory.CreateDirectory(LocalBase);
            Directory.CreateDirectory(RemoteRoot);

            WriteRun(LocalBase, LocalOnlyRun);
            WriteRun(RemoteRoot, RemoteOnlyRun);
            WriteRun(LocalBase, SharedRun);
            WriteRun(RemoteRoot, SharedRun);

            _driveFileSystems = new WorkspaceRemoteFileSystemFactory(
                RemoteRoot, LocalBase);

            DriveProfile = Profiles.Create(new SyncRemoteProfile(
                Guid.NewGuid(),
                "end-to-end-profile",
                SyncProviderKind.GoogleDrive,
                "Example User",
                GoogleDriveApplicationRoot.DisplayName,
                new GoogleDriveSyncRemoteSettings(
                    "end-to-end@example.invalid",
                    GoogleDriveAuthorizationScopes.DriveFile),
                Clock,
                Clock,
                null,
                Clock,
                RootFolderId));

            Factory = new SyncProviderFactory(
                new WorkspaceHistoryService(LocalBase),
                new RecordingHistoryRepository(),
                new WorkspaceDatabasePathProvider(
                    Path.Combine(_root.Path, "gamesaves.db")),
                new GoogleDriveSyncProviderFactory(
                    Profiles,
                    // The Drive wrapper is handed the same local-folder-backed
                    // remote boundary GoogleDriveSyncProviderParityTests uses,
                    // so the composition is real and hermetic at once: no
                    // network, no account, no Google SDK type in this test's
                    // own surface.
                    _driveFileSystems,
                    new WorkspaceHistoryService(LocalBase),
                    new RecordingHistoryRepository()));
        }

        public InMemorySyncRemoteProfileRepository Profiles { get; } = new();

        public IReadOnlyList<Guid> RequestedDriveProfileIds =>
            _driveFileSystems.RequestedProfileIds;

        public string[] RemoteTree() => Tree(RemoteRoot);

        public string[] LocalTree() => Tree(LocalBase);

        private static string[] Tree(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

        public SyncRemoteProfile DriveProfile { get; }

        /// <summary>
        /// The real concrete factory, built with its internal constructor
        /// exactly as the composition root builds it. See `D-028`.
        /// </summary>
        public SyncProviderFactory Factory { get; }

        public string LocalBase => Path.Combine(_root.Path, "backups");

        public string RemoteRoot => Path.Combine(_root.Path, "remote");

        public static string PayloadFor(string runName) => $"payload for {runName}";

        public string LocalPayload(string runName) =>
            Path.Combine(LocalBase, runName, "files", "save.dat");

        public string RemotePayload(string runName) =>
            Path.Combine(RemoteRoot, runName, "files", "save.dat");

        public string LocalManifest(string runName) =>
            Path.Combine(LocalBase, runName, "manifest.json");

        public string RemoteManifest(string runName) =>
            Path.Combine(RemoteRoot, runName, "manifest.json");

        public SyncViewModel CreateDriveViewModel()
        {
            SyncUiSettings settings = SyncUiSettings.Default with
            {
                SelectedProviderKind = SyncProviderKind.GoogleDrive,
                SelectedRemoteProfileId = DriveProfile.Id
            };

            var oauth = new StubGoogleDriveOAuthService
            {
                ConfigurationState = new GoogleDriveOAuthClientConfigurationState(
                    GoogleDriveOAuthClientConfigurationStatus.Available),
                RestoreResult = Connected(),
                ConnectResult = Connected(),
                ReconnectResult = Connected()
            };

            var roots = new StubGoogleDriveRootFolderService
            {
                InspectResult = new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Ready,
                    DriveProfile.Id,
                    RootFolderId,
                    GoogleDriveApplicationRoot.DisplayName,
                    WasValidatedById: true,
                    Message: "The Google Drive backup folder is ready.")
            };

            return new SyncViewModel(
                Factory,
                new SyncProviderCatalog(),
                new SyncProviderSelectionTests.NullFolderPickerService(),
                new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
                Profiles,
                new SyncRemoteProfileService(Profiles, new InMemorySecretStore()),
                new StubSyncRemoteProfileMigrationService(settings),
                new FixedUtcClock(Clock),
                oauth,
                roots);
        }

        private GoogleDriveAuthenticationResult Connected() =>
            new(
                GoogleDriveAuthenticationStatus.Connected,
                new GoogleDriveConnectionSettings(
                    DriveProfile.Id,
                    DriveProfile.AccountDisplayName,
                    (DriveProfile.ProviderSettings as GoogleDriveSyncRemoteSettings)
                        ?.AccountEmail,
                    DriveProfile.RemoteFolderId,
                    DriveProfile.RemoteRootDisplayName,
                    GoogleDriveAuthorizationScopes.DriveFile,
                    GoogleDriveConnectionStatus.Connected,
                    hasStoredToken: true),
                Message: "Google Drive account connected.");

        public SyncViewModel CreateViewModel()
        {
            SyncUiSettings settings = SyncUiSettings.Default with
            {
                SelectedProviderKind = SyncProviderKind.LocalFolder,
                LocalFolderPath = RemoteRoot
            };

            var repository = new InMemorySyncRemoteProfileRepository();

            return new SyncViewModel(
                Factory,
                new SyncProviderCatalog(),
                new SyncProviderSelectionTests.NullFolderPickerService(),
                new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
                repository,
                new SyncRemoteProfileService(repository, new InMemorySecretStore()),
                new StubSyncRemoteProfileMigrationService(settings),
                new FixedUtcClock(Clock),
                new StubGoogleDriveOAuthService())
            {
                RemoteRootPath = RemoteRoot
            };
        }

        /// <summary>
        /// Adds one more local-only run, so a test can tick one upload and
        /// untick another.
        /// </summary>
        public void AddLocalRun(string runName) => WriteRun(LocalBase, runName);

        /// <summary>
        /// Adds a remote folder with a safe run name and an unreadable
        /// manifest, which is what an interrupted upload leaves behind. Returns
        /// the manifest path so a test can prove nothing deleted it.
        /// </summary>
        public string AddUnreadableRemoteRun(string runName)
        {
            string runRoot = Path.Combine(RemoteRoot, runName);
            Directory.CreateDirectory(Path.Combine(runRoot, "files"));
            File.WriteAllText(Path.Combine(runRoot, "files", "save.dat"), "partial");

            string manifestPath = Path.Combine(runRoot, "manifest.json");
            File.WriteAllText(manifestPath, "{ this is not valid json");
            return manifestPath;
        }

        public void Dispose() => _root.Dispose();

        private static void WriteRun(string basePath, string runName)
        {
            string runRoot = Path.Combine(basePath, runName);
            Directory.CreateDirectory(Path.Combine(runRoot, "files"));
            File.WriteAllText(
                Path.Combine(runRoot, "files", "save.dat"),
                PayloadFor(runName));
            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(Manifest(runName)));
        }

        private static TransferBackupManifest Manifest(string runName) => new(
            SchemaVersion: 1,
            Kind: OverwriteBackupContext.ManualKind,
            Game: "End To End Game",
            SteamAppId: "4321",
            SourceAccountId: "source",
            TargetAccountId: "target",
            StartedUtc: DateTimeOffset.Parse("2026-08-20T09:00:00Z"),
            CompletedUtc: DateTimeOffset.Parse("2026-08-20T09:00:01Z"),
            FileCount: 1,
            TotalBytes: PayloadFor(runName).Length,
            Items: []);
    }

    private sealed class WorkspaceHistoryService(string basePath) : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runs = Directory.GetDirectories(basePath)
                .Select(runRoot => new TransferBackupRunInfo(
                    runRoot,
                    Path.Combine(runRoot, "manifest.json"),
                    JsonSerializer.Deserialize<TransferBackupManifest>(
                        File.ReadAllText(Path.Combine(runRoot, "manifest.json")))!))
                .ToList();

            return Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(runs);
        }

        public string GetBackupBasePath() => basePath;
    }

    /// <summary>
    /// Hands the Drive wrapper a local-folder-backed remote boundary, which is
    /// the hermetic backend the parity tests already use. The profile ID is
    /// recorded so a test can prove the view model keyed construction by the
    /// saved profile rather than by anything else.
    /// </summary>
    private sealed class WorkspaceRemoteFileSystemFactory(
        string remoteRoot, string localBase) : IGoogleDriveRemoteFileSystemFactory
    {
        public List<Guid> RequestedProfileIds { get; } = [];

        public IRemoteFileSystem Create(Guid remoteProfileId)
        {
            RequestedProfileIds.Add(remoteProfileId);
            return new LocalFolderRemoteFileSystem(remoteRoot, localBase);
        }
    }

    private sealed class WorkspaceDatabasePathProvider(string databasePath)
        : IAppDatabasePathProvider
    {
        public string GetDatabasePath() => databasePath;
    }
}
