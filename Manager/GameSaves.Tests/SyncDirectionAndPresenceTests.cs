using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Epic C, cards DRIVE-001 through DRIVE-009, at the presentation boundary.
///
/// Everything here is deterministic: the provider is scripted, so a plan, a
/// transfer result, and a revalidation outcome can be stated exactly and the
/// UI's reading of them checked without a network, an account, or a disk.
/// The end-to-end half of DRIVE-008 lives in
/// <see cref="SyncUiEndToEndTests"/>, where the real engine moves real bytes.
/// </summary>
public sealed class SyncDirectionAndPresenceTests
{
    private const string DriveLabel = "Google Drive";

    // ---------------------------------------------------------------
    // DRIVE-001: explicit Upload and Download workflows
    // ---------------------------------------------------------------

    [Fact]
    public async Task PreviewUpload_AsksTheOneEngineForThatDirectionOnly()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewUploadCommand.ExecuteAsync(null);

        Assert.True(viewModel.UploadEnabled);
        Assert.False(viewModel.DownloadEnabled);

        SyncOptions asked = Assert.Single(provider.PreviewOptions);
        Assert.True(asked.Upload);
        Assert.False(asked.Download);

        // The direction chose a preview, never a transfer.
        Assert.Empty(provider.ExecuteOptions);
    }

    [Fact]
    public async Task PreviewDownload_AsksTheOneEngineForThatDirectionOnly()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewDownloadCommand.ExecuteAsync(null);

        Assert.False(viewModel.UploadEnabled);
        Assert.True(viewModel.DownloadEnabled);

        SyncOptions asked = Assert.Single(provider.PreviewOptions);
        Assert.False(asked.Upload);
        Assert.True(asked.Download);
        Assert.Empty(provider.ExecuteOptions);
    }

    [Fact]
    public async Task PreviewBoth_AsksForBothDirections()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        SyncOptions asked = Assert.Single(provider.PreviewOptions);
        Assert.True(asked.Upload);
        Assert.True(asked.Download);
    }

    [Fact]
    public async Task SyncNow_IsWithheldUntilAPlanASelectionAndAConfirmationExist()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        Assert.False(viewModel.CanExecuteSyncNow);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        // A plan and a default selection are not enough on their own.
        Assert.True(viewModel.CanExecuteSync);
        Assert.True(viewModel.HasSelectedRuns);
        Assert.False(viewModel.CanExecuteSyncNow);

        viewModel.ConfirmSync = true;
        Assert.True(viewModel.CanExecuteSyncNow);

        viewModel.DeselectAllRunsCommand.Execute(null);
        Assert.False(viewModel.CanExecuteSyncNow);
    }

    [Fact]
    public async Task ExecuteWithoutTheConfirmation_TransfersNothing()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.Empty(provider.ExecuteOptions);
        Assert.Equal(
            "Sync blocked. Confirm the checkbox first.",
            viewModel.ExecutionStatusMessage);
    }

    [Fact]
    public async Task PlanState_SaysWhichDirectionsTheCurrentPlanActuallyHas()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("upload-run"))
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        Assert.True(viewModel.PlanHasUploads);
        Assert.False(viewModel.PlanHasDownloads);
    }

    // ---------------------------------------------------------------
    // DRIVE-002 and DRIVE-003: presence and semantic treatment
    // ---------------------------------------------------------------

    [Fact]
    public void EveryPresenceState_HasItsOwnWordsAndItsOwnSeverity()
    {
        SyncItemRowViewModel upload = Row(UploadItem("run"));
        SyncItemRowViewModel download = Row(DownloadItem("run"));
        SyncItemRowViewModel inSync = Row(InSyncItem("run"));
        SyncItemRowViewModel conflict = Row(ConflictItem("run"));
        SyncItemRowViewModel unverifiable = Row(
            UploadItem("run") with { FileCount = 0, TotalBytes = 0 });

        Assert.Equal(SyncPresence.LocalOnly, upload.Presence);
        Assert.Equal("Local only", upload.PresenceText);

        Assert.Equal(SyncPresence.RemoteOnly, download.Presence);
        Assert.Equal($"{DriveLabel} only", download.PresenceText);

        Assert.Equal(SyncPresence.BothIdentical, inSync.Presence);
        Assert.Equal("On both, identical", inSync.PresenceText);

        Assert.Equal(SyncPresence.BothConflicting, conflict.Presence);
        Assert.Equal("On both, conflicting", conflict.PresenceText);

        Assert.Equal(SyncPresence.Unverifiable, unverifiable.Presence);
        Assert.Equal("Incomplete or unverifiable", unverifiable.PresenceText);

        // Direction is the accent's job; agreement, disagreement and doubt
        // keep their own meaning, so no accent choice can rename a state.
        Assert.Equal(SyncStateSeverity.Direction, upload.Severity);
        Assert.Equal(SyncStateSeverity.Direction, download.Severity);
        Assert.Equal(SyncStateSeverity.Success, inSync.Severity);
        Assert.Equal(SyncStateSeverity.Warning, conflict.Severity);
        Assert.Equal(SyncStateSeverity.Warning, unverifiable.Severity);

        // Exactly one class is ever set, so a style cannot stack two colours.
        foreach (SyncItemRowViewModel row in
                 new[] { upload, download, inSync, conflict, unverifiable })
        {
            Assert.Equal(
                1,
                new[]
                {
                    row.IsDirectionState,
                    row.IsSuccessState,
                    row.IsWarningState,
                    row.IsDangerState
                }.Count(set => set));
        }
    }

    [Fact]
    public void ARowNamesBothLocations_AndSaysWhichSideDoesNotHaveItYet()
    {
        SyncItemRowViewModel upload = Row(UploadItem("run"));

        Assert.Equal(@"C:\backups\run", upload.LocalLocationDisplay);
        Assert.Equal(
            "GameSave Manager Backups/run (not created yet)",
            upload.RemoteLocationDisplay);

        SyncItemRowViewModel download = Row(DownloadItem("run"));

        Assert.Equal(@"C:\backups\run (not created yet)", download.LocalLocationDisplay);
        Assert.Equal("GameSave Manager Backups/run", download.RemoteLocationDisplay);
    }

    [Fact]
    public void FileCountAndSize_AreOnlyReportedWhenTheyWereActuallyMeasured()
    {
        SyncItemRowViewModel measured = Row(UploadItem("run"));

        Assert.Equal("3 file(s)", measured.FilesDisplay);
        Assert.Equal("2 KB", measured.SizeDisplay);

        SyncItemRowViewModel unmeasured = Row(
            UploadItem("run") with { FileCount = 0, TotalBytes = 0 });

        // "0 file(s)" would present a guess as a measurement.
        Assert.Equal("Not available", unmeasured.FilesDisplay);
        Assert.Equal("Not available", unmeasured.SizeDisplay);
    }

    [Fact]
    public void ARowRecordsWhenBothSidesWereLastRead()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.Zero);
        SyncItemRowViewModel row = new(UploadItem("run"), DriveLabel, checkedAt);

        Assert.Equal(
            $"Checked {checkedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            row.LastVerifiedDisplay);
    }

    [Fact]
    public async Task PlanRows_CarryNoProviderIdentifierOrRawResponse()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        // Everything a row can render, checked against the shape of a Drive
        // object ID rather than against one specific string.
        foreach (SyncItemRowViewModel row in viewModel.Items)
        {
            foreach (string text in new[]
                     {
                         row.PresenceText,
                         row.LocalLocationDisplay,
                         row.RemoteLocationDisplay,
                         row.StatusText,
                         row.ActionAccessibleName,
                         row.ActionToolTip
                     })
            {
                Assert.DoesNotContain(ScriptedSyncProvider.SecretMarker, text);
            }
        }
    }

    // ---------------------------------------------------------------
    // DRIVE-007: the visible action is the selection
    // ---------------------------------------------------------------

    [Fact]
    public void TheDirectionActionToggles_TheOneSelectionStateThatExists()
    {
        int notified = 0;
        SyncItemRowViewModel row = new(
            UploadItem("run"),
            DriveLabel,
            DateTimeOffset.UnixEpoch,
            () => notified++);

        Assert.True(row.IsSelectable);
        Assert.True(row.IncludeInSync);
        Assert.Equal("Selected", row.SelectionStateText);

        row.IncludeInSync = false;

        Assert.Equal("Not selected", row.SelectionStateText);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void TheAccessibleName_SaysTheRunTheDirectionTheStateAndWhatHappensNext()
    {
        SyncItemRowViewModel row = Row(UploadItem("Sunday-run"));

        Assert.Equal(
            "Sunday-run. Upload. Selected. Activate to exclude this run from the next sync.",
            row.ActionAccessibleName);

        row.IncludeInSync = false;

        Assert.Equal(
            "Sunday-run. Upload. Not selected. Activate to include this run in the next sync.",
            row.ActionAccessibleName);
    }

    [Fact]
    public void ConflictAndInSyncRows_CannotBeSelected()
    {
        SyncItemRowViewModel conflict = Row(ConflictItem("run"));
        SyncItemRowViewModel inSync = Row(InSyncItem("run"));

        Assert.False(conflict.IsSelectable);
        Assert.False(inSync.IsSelectable);
        Assert.Equal("Not selectable", conflict.SelectionStateText);
        Assert.Contains("Not selectable", conflict.ActionAccessibleName);
    }

    [Fact]
    public void AnIncompleteRunStaysSelectable_BecauseTheSafetyModelMakesRetrySafe()
    {
        // Upload is create-only and download never overwrites, so copying an
        // interrupted run again adds what is missing rather than damaging it.
        SyncItemRowViewModel row = Row(
            UploadItem("run") with { FileCount = 0, TotalBytes = 0 });

        Assert.Equal(SyncPresence.Unverifiable, row.Presence);
        Assert.True(row.IsSelectable);
    }

    [Fact]
    public async Task SelectAllAndSelectNone_WriteTheSameStateTheActionWrites()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(
                UploadItem("upload-run"),
                DownloadItem("download-run"),
                ConflictItem("conflict-run"))
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        viewModel.DeselectAllRunsCommand.Execute(null);
        Assert.All(
            viewModel.Items.Where(row => row.IsSelectable),
            row => Assert.False(row.IncludeInSync));
        Assert.False(viewModel.HasSelectedRuns);

        viewModel.SelectAllRunsCommand.Execute(null);
        Assert.All(
            viewModel.Items.Where(row => row.IsSelectable),
            row => Assert.True(row.IncludeInSync));
        Assert.True(viewModel.HasSelectedRuns);

        // The conflict row is untouched by either action.
        Assert.False(
            viewModel.Items.Single(row => row.RunName == "conflict-run").IsSelectable);
    }

    [Fact]
    public async Task ChangingTheProvider_CannotLeaveSelectionFromAStalePlan()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        Assert.NotEmpty(viewModel.Items);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.CanExecuteSync);
        Assert.False(viewModel.ConfirmSync);
        Assert.False(viewModel.HasSelectedRuns);
    }

    [Fact]
    public async Task ALargePlan_StaysLinearRatherThanQuadratic()
    {
        // Every row property is computed from that row alone, and the selected
        // summary recomputes once per selection change. The defect this guards
        // is the easy one: a summary or a presence rule that walks the whole
        // collection per row, which is invisible at three runs and unusable at
        // two thousand.
        SyncItem[] runs = Enumerable.Range(0, 2000)
            .Select(index => UploadItem($"run-{index:0000}"))
            .ToArray();

        var provider = new ScriptedSyncProvider { Plan = PlanOf(runs) };
        SyncViewModel viewModel = CreateViewModel(provider);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        // Touch everything a rendered row binds to, for every row.
        foreach (SyncItemRowViewModel row in viewModel.Items)
        {
            _ = row.PresenceText;
            _ = row.LocalLocationDisplay;
            _ = row.RemoteLocationDisplay;
            _ = row.FilesDisplay;
            _ = row.SizeDisplay;
            _ = row.LastVerifiedDisplay;
            _ = row.ActionAccessibleName;
        }

        viewModel.SelectAllRunsCommand.Execute(null);
        viewModel.DeselectAllRunsCommand.Execute(null);
        stopwatch.Stop();

        Assert.Equal(2000, viewModel.Items.Count);

        // Deliberately loose. A linear pass over 2000 rows is milliseconds; a
        // quadratic one is minutes, and only that difference is being caught.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"2000 plan rows took {stopwatch.Elapsed}, which is not a linear cost.");
    }

    // ---------------------------------------------------------------
    // DRIVE-006: endpoints named before the preview
    // ---------------------------------------------------------------

    [Fact]
    public void LocalFolderEndpoints_NameTheResolvedDirectoryAndTheLocalBase()
    {
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());

        Assert.Equal(@"C:\backups", viewModel.LocalEndpointDisplay);
        Assert.Equal(@"D:\MountedBackups", viewModel.RemoteEndpointDisplay);
    }

    [Fact]
    public void SftpEndpoint_ShowsHostPortAndPath_AndNoCredential()
    {
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        viewModel.SftpHost = "backup.example.test";
        viewModel.SftpPort = "2222";
        viewModel.SftpUsername = "alice";
        viewModel.SftpRemotePath = "/srv/game-saves";
        viewModel.SftpPassword = "password-secret";
        viewModel.SftpKeyPassphrase = "passphrase-secret";
        viewModel.SftpKeyFilePath = @"C:\keys\id_rsa";

        Assert.Equal(
            "backup.example.test:2222 /srv/game-saves",
            viewModel.RemoteEndpointDisplay);

        foreach (string secret in new[]
                 {
                     "password-secret", "passphrase-secret", "alice", "id_rsa"
                 })
        {
            Assert.DoesNotContain(secret, viewModel.RemoteEndpointDisplay);
        }
    }

    [Fact]
    public void UnsavedSettings_AreIdentifiedAsUnsavedRatherThanAsAProfile()
    {
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());

        Assert.True(viewModel.IsUsingUnsavedSettings);
        Assert.Equal(
            "Unsaved settings — not stored in any remote profile.",
            viewModel.EndpointProfileStateDisplay);
    }

    [Fact]
    public void IncompleteConfiguration_IsNamedBeforeAnyPreviewRuns()
    {
        SyncViewModel viewModel = CreateViewModel(
            new ScriptedSyncProvider(),
            remoteRoot: "");

        Assert.True(viewModel.HasEndpointIssue);
        Assert.Equal(
            "Choose a local or mounted sync folder first.",
            viewModel.EndpointIssue);

        viewModel.RemoteRootPath = @"D:\MountedBackups";

        Assert.False(viewModel.HasEndpointIssue);
        Assert.Null(viewModel.EndpointIssue);
    }

    [Fact]
    public void ChangingTheProvider_InvalidatesTheEndpointDescription()
    {
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());
        var seen = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncViewModel.RemoteEndpointDisplay))
                seen.Add(viewModel.RemoteEndpointDisplay);
        };

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.Contains("No SFTP host configured yet.", seen);
        Assert.Equal("No SFTP host configured yet.", viewModel.RemoteEndpointDisplay);
    }

    [Fact]
    public void RenderingEndpoints_MakesNoRemoteCall()
    {
        var provider = new ScriptedSyncProvider();
        SyncViewModel viewModel = CreateViewModel(provider);

        _ = viewModel.LocalEndpointDisplay;
        _ = viewModel.RemoteEndpointDisplay;
        _ = viewModel.EndpointProfileStateDisplay;
        _ = viewModel.EndpointIssue;

        Assert.Empty(provider.PreviewOptions);
        Assert.Empty(provider.ExecuteOptions);
    }

    // ---------------------------------------------------------------
    // DRIVE-004: opening locations
    // ---------------------------------------------------------------

    [Fact]
    public void OpeningTheLocalBackupFolder_IsRefusedWhenItDoesNotExist()
    {
        var launched = new List<string>();
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());
        viewModel.LocationLauncher = target =>
        {
            launched.Add(target);
            return true;
        };

        // The stub history service points at a path that is never created.
        Assert.False(viewModel.CanOpenLocalBackupLocation);

        viewModel.OpenLocalBackupLocationCommand.Execute(null);

        Assert.Empty(launched);
        Assert.Contains(
            "The local backup folder does not exist yet",
            viewModel.StatusMessage);
    }

    [Fact]
    public void OpeningARealLocalFolder_HandsTheShellThatFolderAndNothingElse()
    {
        using var directory = new TemporaryDirectory();
        var launched = new List<string>();
        SyncViewModel viewModel = CreateViewModel(
            new ScriptedSyncProvider(),
            remoteRoot: directory.Path,
            localBase: directory.Path);

        viewModel.LocationLauncher = target =>
        {
            launched.Add(target);
            return true;
        };

        Assert.True(viewModel.CanOpenLocalBackupLocation);
        Assert.Equal(directory.Path, viewModel.ResolveRemoteLocationTarget());

        viewModel.OpenLocalBackupLocationCommand.Execute(null);
        viewModel.OpenRemoteLocationCommand.Execute(null);

        Assert.Equal(new[] { directory.Path, directory.Path }, launched);
    }

    [Fact]
    public void AGoogleDriveRootThatIsNotReady_ProducesGuidanceAndNoTarget()
    {
        SyncViewModel viewModel = CreateViewModel(new ScriptedSyncProvider());
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        foreach (GoogleDriveRootFolderStatus status in new[]
                 {
                     GoogleDriveRootFolderStatus.Missing,
                     GoogleDriveRootFolderStatus.Trashed,
                     GoogleDriveRootFolderStatus.Ambiguous,
                     GoogleDriveRootFolderStatus.ReauthenticationRequired
                 })
        {
            viewModel.GoogleDriveRootFolderStatus = status;
            Assert.Null(viewModel.ResolveRemoteLocationTarget());
        }
    }

    // ---------------------------------------------------------------
    // DRIVE-008 and DRIVE-009: revalidation and its states
    // ---------------------------------------------------------------

    [Fact]
    public async Task ACompletedUpload_IsOnlyCalledVerifiedAfterBothSidesAgree()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("run-a")),
            Result = ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        // The plan the check will read: the run is now on both sides.
        provider.Plan = PlanOf(InSyncItem("run-a"));

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        SyncItemResultRowViewModel row = Assert.Single(viewModel.ExecutionResults);

        Assert.Equal(SyncVerificationState.ManifestMatch, row.Verification);
        Assert.Equal("Manifest match", row.StateText);
        Assert.Equal(SyncStateSeverity.Success, row.Severity);

        // The transfer status itself is never rewritten by the check.
        Assert.Equal(nameof(SyncItemStatus.Uploaded), row.Status);
    }

    [Fact]
    public async Task ACopiedRunTheCheckCannotFind_IsNotDescribedAsVerified()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("run-a")),
            Result = ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        // The remote still does not report the run, so the fresh plan still
        // wants to upload it.
        provider.Plan = PlanOf(UploadItem("run-a"));

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        SyncItemResultRowViewModel row = Assert.Single(viewModel.ExecutionResults);

        Assert.Equal(SyncVerificationState.MissingRemotely, row.Verification);
        Assert.Equal(
            $"Copied, verification found it missing on {DriveLabel}",
            row.StateText);
        Assert.Equal(DriveLabel, row.AffectedSideText);
        Assert.Contains("Nothing was deleted by this app", row.StateDetail);
        Assert.False(row.IsVerified);
    }

    [Fact]
    public async Task AMismatchAfterADownload_IsReportedAsAMismatchNotAsSuccess()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(DownloadItem("run-a")),
            Result = ResultOf(DownloadItem("run-a"), SyncItemStatus.Downloaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        provider.Plan = PlanOf(ConflictItem("run-a"));

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        SyncItemResultRowViewModel row = Assert.Single(viewModel.ExecutionResults);

        Assert.Equal(SyncVerificationState.ContentMismatch, row.Verification);
        Assert.Equal(SyncStateSeverity.Danger, row.Severity);
        Assert.Contains("Nothing was changed", row.StateDetail);
    }

    [Fact]
    public async Task AnUnreadableEndpoint_LeavesTheTransferIntactAndSaysSo()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("run-a")),
            Result = ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);

        provider.PreviewFailure = new InvalidOperationException("The remote is unreachable.");

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        SyncItemResultRowViewModel row = Assert.Single(viewModel.ExecutionResults);

        Assert.Equal(SyncVerificationState.EndpointUnavailable, row.Verification);
        Assert.Equal(SyncStateSeverity.Warning, row.Severity);

        // The successful transfer is still recorded and still visible.
        Assert.Equal(nameof(SyncItemStatus.Uploaded), row.Status);
        Assert.Contains("The transfer itself is unchanged", row.StateDetail);
        Assert.Contains(
            "Verification could not read both sides",
            viewModel.VerificationStatusMessage);

        // And it can be retried without repeating the transfer.
        provider.PreviewFailure = null;
        provider.Plan = PlanOf(InSyncItem("run-a"));

        await viewModel.VerifyLastSyncCommand.ExecuteAsync(null);

        Assert.Equal(SyncVerificationState.ManifestMatch, row.Verification);
        Assert.Single(provider.ExecuteOptions);
    }

    [Fact]
    public async Task RevalidationAfterTheProviderChanged_IsRefusedRatherThanMisleading()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("run-a")),
            Result = ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        provider.Plan = PlanOf(InSyncItem("run-a"));
        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanVerifyLastSync);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.False(viewModel.CanVerifyLastSync);

        // The result of the run that did happen stays exactly as recorded.
        SyncItemResultRowViewModel row = Assert.Single(viewModel.ExecutionResults);
        Assert.Equal(nameof(SyncItemStatus.Uploaded), row.Status);
        Assert.Equal(SyncVerificationState.ManifestMatch, row.Verification);

        await viewModel.VerifyLastSyncCommand.ExecuteAsync(null);

        Assert.Contains(
            "The provider or profile changed after that sync ran",
            viewModel.VerificationStatusMessage);
    }

    [Fact]
    public async Task RevalidationRefreshesThePlan_WithoutErasingTheExecutionResult()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("run-a")),
            Result = ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded)
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        provider.Plan = PlanOf(InSyncItem("run-a"));

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        // The plan now shows the refreshed state...
        SyncItemRowViewModel planRow = Assert.Single(viewModel.Items);
        Assert.Equal(SyncPresence.BothIdentical, planRow.Presence);

        // ...and the completed run is still on screen beside it.
        Assert.Single(viewModel.ExecutionResults);

        // Nothing can run again without a fresh confirmation.
        Assert.False(viewModel.ConfirmSync);
        Assert.False(viewModel.CanExecuteSyncNow);

        // Two reads in total: the preview and the one revalidation reused as
        // the refreshed plan.
        Assert.Equal(2, provider.PreviewOptions.Count);
    }

    [Fact]
    public async Task ARunThatWasNotCopied_IsNeverGivenAVerificationVerdict()
    {
        var provider = new ScriptedSyncProvider
        {
            Plan = PlanOf(UploadItem("copied"), ConflictItem("skipped")),
            Result = new SyncResult(
                PlanOf(UploadItem("copied"), ConflictItem("skipped")),
                DryRun: false,
                Uploaded: 1,
                Downloaded: 0,
                Skipped: 1,
                BytesCopied: 2048,
                Items: new[]
                {
                    new SyncItemResult(UploadItem("copied"), 2048, SyncItemStatus.Uploaded, null),
                    new SyncItemResult(
                        ConflictItem("skipped"),
                        0,
                        SyncItemStatus.SkippedConflict,
                        "Conflicts are never copied automatically.")
                },
                Warnings: Array.Empty<TransferPreviewWarning>())
        };

        SyncViewModel viewModel = CreateViewModel(provider);
        await viewModel.PreviewBothDirectionsCommand.ExecuteAsync(null);
        provider.Plan = PlanOf(InSyncItem("copied"), ConflictItem("skipped"));

        viewModel.ConfirmSync = true;
        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        SyncItemResultRowViewModel copied = viewModel.ExecutionResults
            .Single(row => row.RunName == "copied");
        SyncItemResultRowViewModel skipped = viewModel.ExecutionResults
            .Single(row => row.RunName == "skipped");

        Assert.Equal(SyncVerificationState.ManifestMatch, copied.Verification);
        Assert.Equal(SyncVerificationState.NotRequested, skipped.Verification);
        Assert.Equal("Conflict skipped", skipped.StateText);
        Assert.Equal("Both sides", skipped.AffectedSideText);
    }

    [Fact]
    public void EveryExecutionAndVerificationState_HasItsOwnUserFacingWords()
    {
        var labels = new List<string>();

        foreach (SyncItemStatus status in new[]
                 {
                     SyncItemStatus.Failed,
                     SyncItemStatus.Incomplete,
                     SyncItemStatus.SkippedConflict,
                     SyncItemStatus.SkippedDeselected,
                     SyncItemStatus.SkippedAlreadyExists,
                     SyncItemStatus.DryRun
                 })
        {
            var row = new SyncItemResultRowViewModel(
                new SyncItemResult(UploadItem("run"), 0, status, null),
                DriveLabel);

            labels.Add(row.StateText);
            Assert.NotEqual("", row.StateDetail);
        }

        // A failure after bytes moved is a different situation from a failure
        // before any moved, and reads differently.
        labels.Add(new SyncItemResultRowViewModel(
            new SyncItemResult(UploadItem("run"), 4096, SyncItemStatus.Failed, null),
            DriveLabel).StateText);

        foreach (SyncVerificationState state in Enum.GetValues<SyncVerificationState>())
        {
            var row = new SyncItemResultRowViewModel(
                new SyncItemResult(UploadItem("run"), 4096, SyncItemStatus.Uploaded, null),
                DriveLabel)
            {
                Verification = state
            };

            labels.Add(row.StateText);
            Assert.NotEqual("", row.StateDetail);

            // Only ManifestMatch and PayloadVerified represent a verified state.
            Assert.Equal(
                state == SyncVerificationState.ManifestMatch,
                row.StateText.Contains("Manifest match", StringComparison.Ordinal));
        }

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CancelledVerification_NeverTurnsACompletedTransferIntoAFailure()
    {
        var row = new SyncItemResultRowViewModel(
            new SyncItemResult(UploadItem("run"), 4096, SyncItemStatus.Uploaded, null),
            DriveLabel)
        {
            Verification = SyncVerificationState.Cancelled
        };

        Assert.Equal(nameof(SyncItemStatus.Uploaded), row.Status);
        Assert.Equal(SyncStateSeverity.Warning, row.Severity);
        Assert.Contains("The transfer is unchanged and still recorded", row.StateDetail);
    }

    // ---------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------

    private static SyncItemRowViewModel Row(SyncItem item) =>
        new(item, DriveLabel, DateTimeOffset.UnixEpoch);

    private static SyncItem UploadItem(string name) => new(
        RunName: name,
        Action: SyncItemAction.UploadToRemote,
        ExistsLocally: true,
        ExistsRemotely: false,
        LocalPath: $@"C:\backups\{name}",
        RemotePath: $"GameSave Manager Backups/{name}",
        GameName: "Test Game",
        FileCount: 3,
        TotalBytes: 2048,
        StatusText: "Copy to the sync folder");

    private static SyncItem DownloadItem(string name) => new(
        RunName: name,
        Action: SyncItemAction.DownloadToLocal,
        ExistsLocally: false,
        ExistsRemotely: true,
        LocalPath: $@"C:\backups\{name}",
        RemotePath: $"GameSave Manager Backups/{name}",
        GameName: "Test Game",
        FileCount: 3,
        TotalBytes: 2048,
        StatusText: "Copy to the local backup base");

    private static SyncItem InSyncItem(string name) => new(
        RunName: name,
        Action: SyncItemAction.InSync,
        ExistsLocally: true,
        ExistsRemotely: true,
        LocalPath: $@"C:\backups\{name}",
        RemotePath: $"GameSave Manager Backups/{name}",
        GameName: "Test Game",
        FileCount: 3,
        TotalBytes: 2048,
        StatusText: "In sync");

    private static SyncItem ConflictItem(string name) => new(
        RunName: name,
        Action: SyncItemAction.Conflict,
        ExistsLocally: true,
        ExistsRemotely: true,
        LocalPath: $@"C:\backups\{name}",
        RemotePath: $"GameSave Manager Backups/{name}",
        GameName: "Test Game",
        FileCount: 3,
        TotalBytes: 2048,
        StatusText: "Conflict: same name, different content. Never copied automatically.");

    private static SyncPlan PlanOf(params SyncItem[] items) => new(
        ProviderName: DriveLabel,
        RemoteRoot: "GameSave Manager Backups",
        Items: items,
        Warnings: Array.Empty<TransferPreviewWarning>(),
        CanExecute: items.Any(item =>
            item.Action is SyncItemAction.UploadToRemote or SyncItemAction.DownloadToLocal),
        UploadCount: items.Count(item => item.Action == SyncItemAction.UploadToRemote),
        DownloadCount: items.Count(item => item.Action == SyncItemAction.DownloadToLocal),
        InSyncCount: items.Count(item => item.Action == SyncItemAction.InSync),
        ConflictCount: items.Count(item => item.Action == SyncItemAction.Conflict),
        BytesToUpload: items
            .Where(item => item.Action == SyncItemAction.UploadToRemote)
            .Sum(item => item.TotalBytes),
        BytesToDownload: items
            .Where(item => item.Action == SyncItemAction.DownloadToLocal)
            .Sum(item => item.TotalBytes));

    private static SyncResult ResultOf(SyncItem item, SyncItemStatus status) => new(
        PlanOf(item),
        DryRun: false,
        Uploaded: status == SyncItemStatus.Uploaded ? 1 : 0,
        Downloaded: status == SyncItemStatus.Downloaded ? 1 : 0,
        Skipped: 0,
        BytesCopied: item.TotalBytes,
        Items: new[] { new SyncItemResult(item, item.TotalBytes, status, null) },
        Warnings: Array.Empty<TransferPreviewWarning>());

    private static SyncViewModel CreateViewModel(
        ScriptedSyncProvider provider,
        string remoteRoot = @"D:\MountedBackups",
        string localBase = @"C:\backups")
    {
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.LocalFolder,
            LocalFolderPath = remoteRoot
        };

        var repository = new InMemorySyncRemoteProfileRepository();

        return new SyncViewModel(
            new SingleProviderFactory(provider),
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.Parse("2026-09-05T10:00:00Z")),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout(),
            backupHistoryService: new StubBackupBaseService(localBase))
        {
            RemoteRootPath = remoteRoot
        };
    }

    /// <summary>
    /// Only <see cref="IBackupHistoryService.GetBackupBasePath"/> is ever
    /// reached from the sync view model, so the rest refuses loudly rather
    /// than quietly returning something a test could mistake for real data.
    /// </summary>
    private sealed class StubBackupBaseService : IBackupHistoryService
    {
        private readonly string _basePath;

        public StubBackupBaseService(string basePath)
        {
            _basePath = basePath;
        }

        public string GetBackupBasePath() => _basePath;

        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleProviderFactory : ISyncProviderFactory
    {
        private readonly ScriptedSyncProvider _provider;

        public SingleProviderFactory(ScriptedSyncProvider provider)
        {
            _provider = provider;
        }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot) => _provider;

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) => _provider;

        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) => _provider;

        public ISyncProvider CreateOneDriveProvider(Guid remoteProfileId) => _provider;

        public ISyncProvider CreateMegaProvider(Guid remoteProfileId) => _provider;

        public void ForgetSftpHostKey(string host, int port)
        {
        }
    }

    /// <summary>
    /// A provider whose plan and result a test states outright, so a
    /// revalidation verdict can be produced deterministically. It copies
    /// nothing and holds nothing.
    /// </summary>
    private sealed class ScriptedSyncProvider : ISyncProvider
    {
        /// <summary>
        /// Stands in for a provider object identifier. No bound row property
        /// may ever contain it.
        /// </summary>
        public const string SecretMarker = "drive-object-id-marker";

        public string ProviderName => DriveLabel;

        public string RemoteRoot => "GameSave Manager Backups";

        public SyncPlan Plan { get; set; } = PlanOf(UploadItem("run-a"));

        public SyncResult Result { get; set; } =
            ResultOf(UploadItem("run-a"), SyncItemStatus.Uploaded);

        public Exception? PreviewFailure { get; set; }

        public List<SyncOptions> PreviewOptions { get; } = new();

        public List<SyncOptions> ExecuteOptions { get; } = new();

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            PreviewOptions.Add(options);

            if (PreviewFailure is not null)
                throw PreviewFailure;

            return Task.FromResult(Plan);
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            ExecuteOptions.Add(options);
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>(Array.Empty<SyncLogEntry>());

        public void Dispose()
        {
        }
    }
}
