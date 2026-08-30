using System.Reflection;
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

    private const string SftpPassword = "end-to-end-sftp-password";
    private const string SftpPassphrase = "end-to-end-sftp-passphrase";
    private const string SftpKeyPath = @"C:\private\end-to-end-key\id_rsa";

    private static SftpConnectionSettings SftpSettings() =>
        new(
            Host: "sftp.example.invalid",
            Port: 2222,
            Username: "backup-user",
            AuthMethod: SftpAuthMethod.PrivateKey,
            Password: SftpPassword,
            PrivateKeyPath: SftpKeyPath,
            PrivateKeyPassphrase: SftpPassphrase,
            RemotePath: "/srv/game-saves",
            TrustNewHostKey: false);

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

    // ---- W Task 5: history and sync-log effects of a view-model-driven run ----

    [Fact]
    public async Task AViewModelDrivenRun_IsRecordedInTransferHistory()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        Assert.Equal(0, workspace.History.CountRuns());

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // A preview is a dry run and must record nothing.
        Assert.Equal(0, workspace.History.CountRuns());

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, workspace.History.CountRuns());

        TransferRunRecord run = Assert.Single(workspace.History.Records);

        Assert.False(run.DryRun);
        Assert.True(run.BytesCopied > 0);
        Assert.True(run.FilesCopied > 0);
        Assert.Equal(0, run.FilesFailed);
        Assert.Null(run.BlockedReason);

        // Read back through the repository rather than off the list, so the
        // recorded identifier is the one a caller would use.
        Assert.NotEmpty(workspace.History.GetRunItems(1));
    }

    [Fact]
    public async Task ABlockedRun_RecordsNothing()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = false;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.Equal(0, workspace.History.CountRuns());

        // Non-vacuity: the same view model and plan, confirmed, does record.
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, workspace.History.CountRuns());
    }

    [Fact]
    public async Task ADriveRun_AdvancesTheProfileMetadataItShould()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateDriveViewModel();

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        SyncRemoteProfile before =
            workspace.Profiles.GetById(workspace.DriveProfile.Id)!;
        Assert.Null(before.LastSuccessfulConnectionUtc);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        SyncRemoteProfile after =
            workspace.Profiles.GetById(workspace.DriveProfile.Id)!;

        // A preview that reached the remote and validated it is exactly the
        // event LastSuccessfulConnection records.
        Assert.NotNull(after.LastSuccessfulConnectionUtc);
        Assert.Equal(Workspace.ClockUtc, after.LastUsedUtc);

        // Tie the claim to the real composition. Without this the test passes
        // against any provider that reports a validated plan, because the
        // update itself is view-model logic; only the real Drive path asks the
        // internal factory for a remote boundary.
        Assert.Equal(
            new[] { workspace.DriveProfile.Id },
            workspace.RequestedDriveProfileIds);
    }

    [Fact]
    public async Task ARefusedSelection_AdvancesNoProfileMetadata()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateDriveViewModel();

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        // Refused before any provider is built, so nothing connected and
        // nothing should be recorded as having connected.
        viewModel.UploadEnabled = false;
        viewModel.DownloadEnabled = false;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Null(workspace.Profiles
            .GetById(workspace.DriveProfile.Id)!.LastSuccessfulConnectionUtc);
        Assert.Equal(0, workspace.History.CountRuns());
        Assert.Empty(workspace.RequestedDriveProfileIds);

        // Non-vacuity: the same view model, with a direction enabled, does
        // reach the real remote boundary and does advance the metadata.
        viewModel.UploadEnabled = true;
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.NotNull(workspace.Profiles
            .GetById(workspace.DriveProfile.Id)!.LastSuccessfulConnectionUtc);
        Assert.Equal(
            new[] { workspace.DriveProfile.Id },
            workspace.RequestedDriveProfileIds);
    }

    [Fact]
    public async Task TheSyncLog_RoundTripsThroughTheViewModelAndCarriesNothingPrivate()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // Written by the run just executed and read back through the same
        // shared RefreshSyncLogAsync call the preview uses.
        Assert.NotEmpty(viewModel.SyncLog);

        foreach (var entry in viewModel.SyncLog)
        {
            Assert.DoesNotContain("://", entry.SummaryDisplay, StringComparison.Ordinal);
            Assert.DoesNotContain(
                workspace.RemoteRoot, entry.SummaryDisplay, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                workspace.LocalBase, entry.SummaryDisplay, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- W Task 6: the SFTP provider, and what can honestly be covered ----

    [Fact]
    public void TheSftpProvider_IsBuiltByTheRealFactoryWithoutTouchingTheNetwork()
    {
        using var workspace = new Workspace();

        // Construction is inert: SftpRemoteFileSystem only normalises the
        // remote path in its constructor, and the known-hosts store only
        // remembers a file path, so nothing connects and nothing is written.
        using ISyncProvider provider =
            workspace.Factory.CreateSftpProvider(SftpSettings());

        SftpSyncProvider sftp = Assert.IsType<SftpSyncProvider>(provider);

        Assert.Equal("SFTP", sftp.ProviderName);
        Assert.Equal(SftpSettings().DisplayRoot, sftp.RemoteRoot);

        // The display root is the one piece of SFTP identity that reaches the
        // UI, so it must carry no secret even though the settings hold three.
        Assert.DoesNotContain(SftpPassword, sftp.RemoteRoot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SftpPassphrase, sftp.RemoteRoot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SftpKeyPath, sftp.RemoteRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSftpProvider_HasNoSeamForAHermeticRemoteFileSystem()
    {
        // This is a finding pinned as a test rather than a defect fixed here.
        // SftpSyncProvider constructs its own SftpRemoteFileSystem from the
        // connection settings, so unlike GoogleDriveSyncProvider it cannot be
        // handed a fake IRemoteFileSystem, and its transfer behaviour cannot be
        // exercised without a real SSH server. Milestone W adds no product
        // behaviour, so the seam is not added here.
        //
        // When a seam is added, rewrite this test to use it. Do not delete it.
        ConstructorInfo[] constructors = typeof(SftpSyncProvider).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        ConstructorInfo only = Assert.Single(constructors);

        Assert.False(only.IsPublic);
        Assert.Contains(
            only.GetParameters(),
            parameter => parameter.ParameterType == typeof(SftpConnectionSettings));
        Assert.DoesNotContain(
            only.GetParameters(),
            parameter => parameter.ParameterType == typeof(IRemoteFileSystem));
    }

    [Fact]
    public void TheTraversalGuardProtectingSftp_LivesInTheSharedEngine()
    {
        // `D-030` fixed an arbitrary local file write reachable from a hostile
        // SFTP directory listing. The fix is in SyncEngine, which every
        // provider shares, and it is already covered by
        // SyncRemotePathTraversalTests. Rather than restate those assertions,
        // pin the structural fact that makes citing them valid: the SFTP
        // provider really does run on that engine.
        FieldInfo[] fields = typeof(SftpSyncProvider).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Contains(fields, field => field.FieldType == typeof(SyncEngine));

        // And so does the provider this milestone has been driving, which is
        // why the end-to-end coverage above transfers to SFTP the moment a
        // seam exists.
        Assert.Contains(
            typeof(LocalFolderSyncProvider).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SyncEngine));
    }

    [Fact]
    public async Task SelectingSftpInTheUi_BuildsOnlyTheSftpProvider()
    {
        using var workspace = new Workspace();
        SyncViewModel viewModel = workspace.CreateViewModel();

        // Non-vacuity: the Local Folder path in this same view model really
        // does reach the engine and copy bytes.
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);
        Assert.True(File.Exists(workspace.RemoteManifest(LocalOnlyRun)));

        // Switching to SFTP with an incomplete form is refused by the shared
        // validation before any provider is built, so no connection is
        // attempted and the previous plan is discarded.
        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        viewModel.SftpHost = "";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal("Enter the SFTP host first.", viewModel.StatusMessage);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Empty(viewModel.Items);
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

        public static DateTimeOffset ClockUtc => Clock;

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
                // LastUsedUtc and LastSuccessfulConnectionUtc, both unset, so
                // a test can prove a run advances them. They are adjacent in
                // the record and easy to fill in the wrong order.
                null,
                null,
                RootFolderId));

            Factory = new SyncProviderFactory(
                new WorkspaceHistoryService(LocalBase),
                History,
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
                    History));
        }

        /// <summary>
        /// The one transfer-history repository both providers write through, so
        /// a test can read a view-model-driven run back out of it.
        /// </summary>
        public RecordingHistoryRepository History { get; } = new();

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
                SyncProviderSelectionTests.NewWorkspaceLayout(),
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
                new StubGoogleDriveOAuthService(),
                SyncProviderSelectionTests.NewWorkspaceLayout())
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
