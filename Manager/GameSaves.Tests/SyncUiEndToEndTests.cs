using System.Text.Json;
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

    // ---- helpers ----

    /// <summary>
    /// A local backup base and a remote folder, both inside one temporary
    /// directory that is deleted on dispose. Three runs: one only local, one
    /// only remote, one on both sides.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private const string RootFolderId = "end-to-end-root-folder-id";

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
