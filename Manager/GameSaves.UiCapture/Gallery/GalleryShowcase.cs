using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using Avalonia.Threading;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// The deterministic showcase fixture: the populated state the website
    /// screenshots are taken against.
    ///
    /// It lives entirely in the capture harness. No production view model
    /// carries marketing constants, and nothing here reads the machine: every
    /// value below is a literal, every path is synthetic, every timestamp is
    /// fixed, and every Steam identifier is visibly masked. The harness that
    /// calls it has already replaced the database, the UI settings file, the
    /// sync settings file, and Steam discovery with throwaway equivalents, so
    /// seeding the view models directly is the last step rather than the only
    /// protection.
    ///
    /// The regression sweeps keep using the empty state; only Gallery mode
    /// asks for this.
    /// </summary>
    public static class GalleryShowcase
    {
        // One fixed instant the whole fixture is derived from, so two runs of
        // the same commit produce the same text. Local rendering still follows
        // the capture machine's time zone, which is why a gallery is
        // regenerated rather than diffed across machines.
        private static readonly DateTimeOffset Epoch =
            new(2026, 8, 28, 19, 42, 0, TimeSpan.Zero);

        // Synthetic roots. No drive letter here belongs to a real machine and
        // no segment can contain a Windows account name.
        private const string SteamRoot = @"D:\Steam";
        private const string LibraryRoot = @"D:\Steam\steamapps";
        private const string FastLibrary = @"E:\SteamLibrary\steamapps";
        private const string ArchiveLibrary = @"F:\ArchiveLibrary\steamapps";
        private const string BackupRoot = @"D:\GameSaveBackups";
        private const string SyncFolder = @"D:\GameSaveSync";

        // Masked account identifiers. The digits are placeholders and the
        // SteamID64 column is deliberately elided, so no row can be mistaken
        // for a real account.
        private const string PrimaryAccount = "100000001";
        private const string LivingRoomAccount = "100000002";
        private const string SecondaryAccount = "100000003";

        /// <summary>Dashboard mapping totals, fixed for the website.</summary>
        public const int ApprovedMappings = 2698;
        public const int PendingMappings = 5978;
        public const int NeedsAttentionMappings = 16;

        /// <summary>
        /// Populates every page. Safe to call more than once; each collection
        /// is rebuilt from scratch so a second call cannot double the rows.
        /// </summary>
        public static void Apply(MainWindowViewModel viewModel)
        {
            ApplyDashboard(viewModel);
            ApplyInstalledGames(viewModel.InstalledGames);
            ApplyProfiles(viewModel.Profiles);
            ApplyTransferPreview(viewModel.TransferPreview);
            ApplyManualBackup(viewModel.ManualBackup);
            ApplyBackups(viewModel.BackupHistory);
            ApplyHistory(viewModel.TransferHistory);
        }

        private static void ApplyDashboard(MainWindowViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.IsSteamMissing = false;
            viewModel.SteamRoot = SteamRoot;
            viewModel.LibraryCount = 3;
            viewModel.InstalledGameCount = 12;
            viewModel.SteamProfileCount = 3;
            viewModel.ApprovedMappingCount = ApprovedMappings;
            viewModel.PendingMappingCount = PendingMappings;
            viewModel.NeedsFixMappingCount = NeedsAttentionMappings;
            viewModel.StatusMessage =
                "Scan complete. 3 libraries, 12 installed games, 3 Steam profiles.";
        }

        // A stable mix of Ready, review-pending and needs-attention rows, so
        // the table shows the states the status column exists to distinguish
        // rather than twelve identical green rows.
        private static void ApplyInstalledGames(InstalledGamesViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.Games.Clear();

            foreach (InstalledGameRowViewModel row in Games())
                viewModel.Games.Add(row);

            viewModel.SelectedGame = viewModel.Games[0];
            viewModel.StatusMessage = $"{viewModel.Games.Count} installed games found.";

            // All ten columns together are a few pixels wider than the page's
            // own maximum width, so the star-sized Status column loses its
            // right edge to the scrollbar. Library repeats the start of Install
            // path, so the gallery hides it through the table's own column
            // chooser rather than by changing the table. The underlying width
            // problem is recorded in the gallery report, not papered over.
            foreach (InstalledGameColumnOption option in viewModel.ColumnOptions)
            {
                if (option.Key == AppUiSettings.LibraryColumn)
                    option.IsVisible = false;
            }
        }

        /// <summary>
        /// Puts the three showcase sync profiles into the Sync page's own
        /// profile list, the way the application would after reading them back
        /// from its database. Nothing here contacts a provider: these are the
        /// non-secret profile records the application already stores, with an
        /// unroutable host and a masked account.
        /// </summary>
        private static void SeedSyncProfiles(SyncViewModel viewModel)
        {
            if (viewModel.RemoteProfiles.Count > 0)
                return;

            foreach (SyncRemoteProfile profile in ShowcaseProfiles())
            {
                viewModel.RemoteProfiles.Add(profile);
                viewModel.RemoteProfileOptions.Add(
                    new SyncRemoteProfileOption(profile, profile.DisplayName));
            }
        }

        private static IReadOnlyList<SyncRemoteProfile> ShowcaseProfiles() =>
            new[]
            {
                new SyncRemoteProfile(
                    new Guid("00000000-0000-4000-8000-000000000001"),
                    "External drive (Local Folder)",
                    SyncProviderKind.LocalFolder,
                    AccountDisplayName: null,
                    RemoteRootDisplayName: SyncFolder,
                    new LocalFolderSyncRemoteSettings(SyncFolder),
                    Epoch.AddDays(-60),
                    Epoch.AddDays(-3),
                    Epoch.AddDays(-1),
                    Epoch.AddDays(-1),
                    RemoteFolderId: null),

                new SyncRemoteProfile(
                    new Guid("00000000-0000-4000-8000-000000000002"),
                    "Home server (SFTP)",
                    SyncProviderKind.Sftp,
                    AccountDisplayName: "gamesave",
                    RemoteRootDisplayName: "/srv/gamesave-sync",
                    // backup.example.test: .test is reserved by RFC 2606 and
                    // can never resolve, so no host in a screenshot is real.
                    new SftpSyncRemoteSettings(
                        "backup.example.test",
                        22,
                        "gamesave",
                        SftpAuthMethod.PrivateKey,
                        @"D:\keys\gamesave-sync.pem",
                        "/srv/gamesave-sync"),
                    Epoch.AddDays(-45),
                    Epoch.AddDays(-2),
                    Epoch.AddDays(-2),
                    Epoch.AddDays(-2),
                    RemoteFolderId: null),

                new SyncRemoteProfile(
                    new Guid("00000000-0000-4000-8000-000000000003"),
                    "Google Drive (showcase)",
                    SyncProviderKind.GoogleDrive,
                    AccountDisplayName: "Showcase Account",
                    RemoteRootDisplayName: GoogleDriveApplicationRoot.DisplayName,
                    new GoogleDriveSyncRemoteSettings(
                        "showcase@example.com",
                        GoogleDriveAuthorizationScopes.DriveFile),
                    Epoch.AddDays(-30),
                    Epoch.AddDays(-1),
                    Epoch.AddDays(-1),
                    Epoch.AddDays(-1),
                    RemoteFolderId: "showcase-backup-folder"),
            };

        public static IReadOnlyList<InstalledGameRowViewModel> Games() => new[]
        {
            Game("107410", "Arma 3", LibraryRoot, 3, 0, 0, true, 142, 486539264, GameSaveStatusKind.Ready, "Ready"),
            Game("413150", "Stardew Valley", LibraryRoot, 2, 0, 0, true, 38, 27262976, GameSaveStatusKind.Ready, "Ready"),
            Game("1145360", "Hades", LibraryRoot, 1, 0, 0, true, 22, 9437184, GameSaveStatusKind.Ready, "Ready"),
            Game("367520", "Hollow Knight", LibraryRoot, 1, 0, 0, true, 14, 3670016, GameSaveStatusKind.Ready, "Ready"),
            Game("105600", "Terraria", FastLibrary, 2, 1, 0, true, 63, 71303168, GameSaveStatusKind.MappingMissing, "Review pending"),
            Game("427520", "Factorio", FastLibrary, 2, 0, 0, true, 51, 118489088, GameSaveStatusKind.Ready, "Ready"),
            Game("294100", "RimWorld", FastLibrary, 1, 2, 0, true, 87, 204472320, GameSaveStatusKind.MappingMissing, "Review pending"),
            Game("292030", "The Witcher 3: Wild Hunt", FastLibrary, 2, 0, 0, true, 46, 1073741824, GameSaveStatusKind.Ready, "Ready"),
            Game("1086940", "Baldur's Gate 3", ArchiveLibrary, 1, 1, 0, true, 118, 2415919104, GameSaveStatusKind.MappingMissing, "Review pending"),
            Game("220", "Half-Life 2", ArchiveLibrary, 1, 0, 0, true, 9, 4194304, GameSaveStatusKind.Ready, "Ready"),
            Game("730", "Counter-Strike 2", ArchiveLibrary, 2, 0, 0, true, 16, 67108864, GameSaveStatusKind.Ready, "Ready"),
            Game("999001", "Untitled Sandbox Prototype", ArchiveLibrary, 0, 2, 1, false, 0, 0, GameSaveStatusKind.NeedsFixOnly, "Needs attention"),
        };

        private static InstalledGameRowViewModel Game(
            string appId,
            string name,
            string libraryPath,
            int approved,
            int pending,
            int needsFix,
            bool savePathExists,
            int fileCount,
            long totalBytes,
            GameSaveStatusKind kind,
            string status)
        {
            string installDirectory = name
                .Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace("'", string.Empty, StringComparison.Ordinal);

            return new InstalledGameRowViewModel(new InstalledGameSaveStatus(
                new SteamGame(
                    appId,
                    name,
                    installDirectory,
                    libraryPath,
                    $"{libraryPath}\\appmanifest_{appId}.acf",
                    $"{libraryPath}\\common\\{installDirectory}",
                    FolderExists: true,
                    SteamDiscoveryConfidence.High),
                kind,
                status,
                approved,
                pending,
                needsFix,
                savePathExists,
                fileCount,
                totalBytes,
                Array.Empty<SavePathVerificationResult>(),
                Error: null));
        }

        private static void ApplyProfiles(ProfilesViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.Profiles.Clear();

            foreach (SteamProfileRowViewModel row in Profiles())
                viewModel.Profiles.Add(row);

            viewModel.SourceProfile = viewModel.Profiles[0];
            viewModel.TargetProfile = viewModel.Profiles[1];
            viewModel.StatusMessage = $"Loaded {viewModel.Profiles.Count} Steam profile(s).";
        }

        public static IReadOnlyList<SteamProfileRowViewModel> Profiles() => new[]
        {
            Profile(PrimaryAccount, "Primary Profile", 24, isCurrentUser: true),
            Profile(LivingRoomAccount, "Living Room", 11, isCurrentUser: false),
            Profile(SecondaryAccount, "Secondary Profile", 6, isCurrentUser: false),
        };

        // SteamID64 is shown as a masked placeholder rather than a plausible
        // number: a screenshot must not carry anything that could be read as a
        // real account, not even a made-up one that looks valid.
        private static SteamProfileRowViewModel Profile(
            string accountId, string displayName, int appFolderCount, bool isCurrentUser) =>
            new(new SteamProfile(
                accountId,
                "7656119" + new string('\u2022', 7) + accountId[^3..],
                displayName,
                $@"{SteamRoot}\userdata\{accountId}",
                appFolderCount,
                isCurrentUser));

        // The preview page's job is to show what would happen before anything
        // happens, so the fixture includes a skipped target and one blocked
        // item alongside the copyable ones.
        private static void ApplyTransferPreview(TransferPreviewViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.SelectedSourceProfile = viewModel.Profiles.FirstOrDefault();
            viewModel.SelectedTargetProfile = viewModel.Profiles.Skip(1).FirstOrDefault();
            viewModel.SelectedGame = viewModel.Games.FirstOrDefault();
            viewModel.IncludeSteamUserDataGameFolder = true;
            viewModel.IncludeApprovedMappings = true;

            Replace(viewModel.Items, PreviewItems());
            Replace(viewModel.UserDataItems, PreviewItems()
                .Where(item => item.SourceType == TransferSourceType.SteamUserDataGameFolder));
            Replace(viewModel.MappingItems, PreviewItems()
                .Where(item => item.SourceType == TransferSourceType.ApprovedMapping));

            Replace(viewModel.Warnings, new[]
            {
                Warning("TargetExists",
                    "1 target already exists and will be skipped. Existing files are never overwritten unless you ask for it.",
                    TransferWarningSeverity.Info),
                Warning("MappingNotProfileSpecific",
                    "1 approved mapping is not profile specific, so it resolves to the same path for both profiles and is blocked.",
                    TransferWarningSeverity.Warning),
            });

            viewModel.TotalFiles = 176;
            viewModel.TotalSizeDisplay = "512.5 MB";
            viewModel.BlockedItemCount = 1;
            viewModel.SkipBlockedItems = true;
            viewModel.CanExecuteCopy = false;
            viewModel.ConfirmRealTransfer = false;
            viewModel.OverwriteExisting = false;
            viewModel.BackupBeforeOverwrite = true;
            viewModel.StatusMessage =
                "Dry-run preview ready: 176 file(s), 512.5 MB across 4 item(s). Nothing has been copied.";
            viewModel.ExecutionStatusMessage = "No copy executed.";
        }

        private static IReadOnlyList<TransferPreviewItemRowViewModel> PreviewItems() => new[]
        {
            PreviewItem(
                TransferSourceType.SteamUserDataGameFolder, null, null,
                $@"{SteamRoot}\userdata\{PrimaryAccount}\107410",
                $@"{SteamRoot}\userdata\{LivingRoomAccount}\107410",
                sourceExists: true, targetExists: false, files: 142, bytes: 486539264,
                TransferConflictStatus.None, "Ready", "Copy 142 file(s)"),
            PreviewItem(
                TransferSourceType.ApprovedMapping, 4821,
                @"%USERPROFILE%\Documents\Arma 3\{accountId}",
                $@"{SteamRoot}\userdata\{PrimaryAccount}\107410\remote\profile",
                $@"{SteamRoot}\userdata\{LivingRoomAccount}\107410\remote\profile",
                sourceExists: true, targetExists: false, files: 27, bytes: 21495808,
                TransferConflictStatus.None, "Ready", "Copy 27 file(s)"),
            PreviewItem(
                TransferSourceType.ApprovedMapping, 4822,
                @"%USERPROFILE%\Documents\Arma 3\{accountId}\missions",
                $@"{SteamRoot}\userdata\{PrimaryAccount}\107410\remote\missions",
                $@"{SteamRoot}\userdata\{LivingRoomAccount}\107410\remote\missions",
                sourceExists: true, targetExists: true, files: 7, bytes: 4587520,
                TransferConflictStatus.TargetExists, "Target exists - skipped", "Skip"),
            PreviewItem(
                TransferSourceType.ApprovedMapping, 4823,
                @"%LOCALAPPDATA%\Arma 3\shared",
                $@"{SteamRoot}\userdata\shared\107410",
                $@"{SteamRoot}\userdata\shared\107410",
                sourceExists: true, targetExists: true, files: 0, bytes: 0,
                TransferConflictStatus.SameSourceAndTarget,
                "Source and target resolve to the same path - blocked", "Blocked"),
        };

        private static TransferPreviewItemRowViewModel PreviewItem(
            TransferSourceType sourceType,
            long? mappingId,
            string? mappingTemplate,
            string sourcePath,
            string targetPath,
            bool sourceExists,
            bool targetExists,
            int files,
            long bytes,
            TransferConflictStatus conflict,
            string status,
            string action) =>
            new(new TransferPreviewItem(
                sourceType,
                mappingId,
                mappingTemplate,
                "107410",
                "Arma 3",
                $@"{SteamRoot}\userdata\{PrimaryAccount}",
                $@"{SteamRoot}\userdata\{LivingRoomAccount}",
                sourcePath,
                targetPath,
                TransferCopyScope.DirectoryContents,
                sourceExists,
                targetExists,
                files,
                bytes,
                conflict,
                status,
                action));

        private static void ApplyManualBackup(ManualBackupViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.SelectedProfile = viewModel.Profiles.FirstOrDefault();
            viewModel.SelectedGame = viewModel.Games.FirstOrDefault();
            viewModel.DestinationPath = BackupRoot;
            viewModel.IncludeSteamUserDataGameFolder = true;
            viewModel.IncludeApprovedMappings = true;

            viewModel.Presets.Clear();
            viewModel.Presets.Add(new BackupPresetRowViewModel(new ManualBackupPreset(
                1, "Weekly full backup", BackupRoot, true, true,
                Epoch.AddDays(-45), Epoch.AddDays(-2))));
            viewModel.Presets.Add(new BackupPresetRowViewModel(new ManualBackupPreset(
                2, "Userdata only", BackupRoot, true, false,
                Epoch.AddDays(-30), Epoch.AddDays(-9))));
            viewModel.SelectedPreset = viewModel.Presets[0];

            Replace(viewModel.Items, PreviewItems().Take(2));
            Replace(viewModel.Warnings, new[]
            {
                Warning("BackupDestination",
                    "The destination is outside the Steam library, so a backup cannot overwrite a live save.",
                    TransferWarningSeverity.Info),
            });

            viewModel.TotalFiles = 169;
            viewModel.TotalSizeDisplay = "484.1 MB";
            viewModel.CanExecuteBackup = true;
            viewModel.ConfirmBackup = false;
            viewModel.StatusMessage =
                "Backup preview ready: 169 file(s), 484.1 MB. Confirm to create the backup.";
            viewModel.ExecutionStatusMessage = "No backup executed.";
        }

        private static void ApplyBackups(BackupHistoryViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.Runs.Clear();

            foreach (BackupRunRowViewModel run in BackupRuns())
                viewModel.Runs.Add(run);

            // As on History: selecting a run kicks off its own file load, and
            // the fixture's rows have to be written after that settles.
            viewModel.SelectedRun = viewModel.Runs[0];
            Settle();

            Replace(viewModel.RunItems, new[]
            {
                BackupItem(@"userdata\107410\remote\profile\player.vars",
                    "player.vars", 262144, "3f7a1c9d5e2b8046a1c3f5e7b9d2408614ca7f3e5d9b1027c4a6e8f0b2d41637"),
                BackupItem(@"userdata\107410\remote\profile\campaign.save",
                    "campaign.save", 8912896, "b1d4f60298ae3c57190bd2e4f8a60c3591de7b04a2f68c15d3907be24a1c5f88"),
                BackupItem(@"userdata\107410\remote\missions\coop-01.pbo",
                    "coop-01.pbo", 4194304, "7c2e910ab54d386f0192cd7be4530a8617fd29c40b6e8135a97240ef6b1c8d53"),
            });

            viewModel.RestoreToOriginal = true;
            viewModel.ConfirmRestore = false;
            viewModel.OverwriteCurrentFiles = false;
            viewModel.ResolvedTargetDisplay =
                $@"{SteamRoot}\userdata\{PrimaryAccount}\107410";
            viewModel.RestoreStatusMessage =
                "Preview a restore before running it. Existing files are skipped unless overwrite is enabled.";
            viewModel.ArchiveStatusMessage =
                "Export a run as a single ZIP file, or import a previously exported backup ZIP.";
            viewModel.KeepNewestRunsText = "10";
            viewModel.CleanupStatusMessage =
                "No cleanup executed. Preview first to see what would be deleted.";
            viewModel.StatusMessage = $"Showing the {viewModel.Runs.Count} most recent backup run(s).";
        }

        private static IReadOnlyList<BackupRunRowViewModel> BackupRuns() => new[]
        {
            BackupRun("Arma 3", "107410", OverwriteBackupContext.ManualKind, 0, 142, 486539264),
            BackupRun("Stardew Valley", "413150", OverwriteBackupContext.ManualKind, 1, 38, 27262976),
            BackupRun("Baldur's Gate 3", "1086940", OverwriteBackupContext.ManualKind, 2, 118, 2415919104),
            BackupRun("Factorio", "427520", OverwriteBackupContext.RestoreKind, 4, 51, 118489088),
            BackupRun("The Witcher 3: Wild Hunt", "292030", OverwriteBackupContext.ManualKind, 6, 46, 1073741824),
            BackupRun("Terraria", "105600", OverwriteBackupContext.ManualKind, 9, 63, 71303168),
        };

        private static BackupRunRowViewModel BackupRun(
            string game, string appId, string kind, int daysAgo, int files, long bytes)
        {
            DateTimeOffset started = Epoch.AddDays(-daysAgo);
            string stamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string root = $@"{BackupRoot}\{appId}\{stamp}";

            return new BackupRunRowViewModel(new TransferBackupRunInfo(
                root,
                $@"{root}\manifest.json",
                new TransferBackupManifest(
                    SchemaVersion: 1,
                    Kind: kind,
                    Game: game,
                    SteamAppId: appId,
                    SourceAccountId: PrimaryAccount,
                    TargetAccountId: PrimaryAccount,
                    StartedUtc: started,
                    CompletedUtc: started.AddMinutes(1),
                    FileCount: files,
                    TotalBytes: bytes,
                    Items: Array.Empty<TransferOverwriteBackupItem>())));
        }

        private static BackupItemRowViewModel BackupItem(
            string relative, string name, long bytes, string sha) =>
            new(new TransferOverwriteBackupItem(
                $@"{SteamRoot}\{relative}",
                $@"{BackupRoot}\107410\20260828-194200\files\{name}",
                bytes,
                sha,
                Epoch));

        // A mixture of operation kinds, because History exists to show what
        // the application has done rather than one kind of row repeated.
        private static void ApplyHistory(TransferHistoryViewModel viewModel)
        {
            viewModel.IsLoading = false;
            viewModel.Runs.Clear();

            foreach (TransferRunRowViewModel run in HistoryRuns())
                viewModel.Runs.Add(run);

            // Selecting a run starts a background read of that run's files from
            // the database, which is empty here and would clear the list a
            // moment after it was filled. Let that finish first, then seed.
            viewModel.SelectedRun = viewModel.Runs[0];
            Settle();

            Replace(viewModel.RunItems, new[]
            {
                HistoryItem(@"userdata\107410\remote\profile\player.vars", 262144, "Copied"),
                HistoryItem(@"userdata\107410\remote\profile\campaign.save", 8912896, "Copied"),
                HistoryItem(@"userdata\107410\remote\missions\coop-01.pbo", 4194304, "SkippedAlreadyExists"),
            });

            viewModel.StatusMessage = $"Showing the {viewModel.Runs.Count} most recent run(s).";
        }

        private static IReadOnlyList<TransferRunRowViewModel> HistoryRuns() => new[]
        {
            HistoryRun(1207, TransferRunKind.ManualBackup, "Arma 3", "107410",
                PrimaryAccount, PrimaryAccount, 0, 142, 0, 0, 486539264, null),
            HistoryRun(1206, TransferRunKind.Sync, "Sync upload", "-",
                PrimaryAccount, PrimaryAccount, 1, 6, 3, 0, 913309696, null),
            HistoryRun(1205, TransferRunKind.TransferCopy, "Stardew Valley", "413150",
                PrimaryAccount, LivingRoomAccount, 2, 38, 1, 0, 27262976, null),
            HistoryRun(1204, TransferRunKind.Restore, "Factorio", "427520",
                PrimaryAccount, PrimaryAccount, 4, 51, 0, 0, 118489088, null),
            HistoryRun(1203, TransferRunKind.Sync, "Sync download", "-",
                PrimaryAccount, PrimaryAccount, 5, 2, 4, 0, 41943040, null),
            HistoryRun(1202, TransferRunKind.TransferCopy, "Baldur's Gate 3", "1086940",
                PrimaryAccount, LivingRoomAccount, 7, 0, 0, 0, 0,
                "Source and target resolve to the same path."),
            HistoryRun(1201, TransferRunKind.Cleanup, "Backup cleanup", "-",
                PrimaryAccount, PrimaryAccount, 11, 0, 3, 0, 0, null),
        };

        private static TransferRunRowViewModel HistoryRun(
            long id,
            TransferRunKind kind,
            string game,
            string appId,
            string source,
            string target,
            int daysAgo,
            int copied,
            int skipped,
            int failed,
            long bytes,
            string? blockedReason)
        {
            DateTimeOffset started = Epoch.AddDays(-daysAgo);

            return new TransferRunRowViewModel(new TransferRunInfo(
                id,
                kind,
                game,
                appId,
                source,
                target,
                DryRun: false,
                OverwriteEnabled: false,
                BackupEnabled: true,
                FilesConsidered: copied + skipped + failed,
                FilesCopied: copied,
                FilesSkipped: skipped,
                FilesFailed: failed,
                BytesCopied: bytes,
                FilesBackedUp: 0,
                BackupRootPath: kind == TransferRunKind.ManualBackup
                    ? $@"{BackupRoot}\{appId}"
                    : null,
                BlockedReason: blockedReason,
                StartedUtc: started,
                CompletedUtc: started.AddMinutes(2)));
        }

        private static TransferRunItemRowViewModel HistoryItem(
            string relative, long bytes, string status) =>
            new(new TransferRunItemRecord(
                $@"{SteamRoot}\{relative}",
                $@"{SteamRoot}\userdata\{LivingRoomAccount}\{relative[9..]}",
                bytes,
                Copied: status == "Copied",
                Status: status,
                Error: null,
                BackupFile: null));

        // -------------------------------------------------------------------
        // Sync. Nothing here connects to anything: the provider selection and
        // the plan rows are set on the view model directly, so no credential
        // is read, no OAuth flow starts, and no network call is made.
        // -------------------------------------------------------------------
        public static void ApplySync(SyncViewModel viewModel, string providerScenario)
        {
            if (providerScenario == GalleryProviders.None)
                return;

            SeedSyncProfiles(viewModel);

            viewModel.IsLoading = false;
            viewModel.UploadEnabled = true;
            viewModel.DownloadEnabled = true;
            viewModel.TargetSectionExpanded = true;
            viewModel.PlanSectionExpanded = true;
            viewModel.WarningsSectionExpanded = true;
            viewModel.HistorySectionExpanded = true;
            viewModel.ResultsSectionExpanded =
                providerScenario == GalleryProviders.Results;

            SyncProviderKind kind = providerScenario switch
            {
                GalleryProviders.Sftp => SyncProviderKind.Sftp,
                GalleryProviders.GoogleDrive => SyncProviderKind.GoogleDrive,
                _ => SyncProviderKind.LocalFolder,
            };

            // Selecting the saved profile is what a user does, and it is what
            // makes the page internally consistent: the profile selector, the
            // "Saved" state, the provider fields and the Preview action all
            // come from the same record instead of being poked in one by one.
            SyncRemoteProfileOption? option = viewModel.RemoteProfileOptions
                .FirstOrDefault(candidate => candidate.Profile?.ProviderKind == kind);

            if (option is not null)
                viewModel.SelectedRemoteProfileOption = option;
            else
                viewModel.SelectedProviderKind = kind;

            // The profile's own authentication check has been dispatched by
            // now; let it finish before the connected state is described, or
            // it would overwrite it a moment later.
            Dispatcher.UIThread.RunJobs();

            if (kind == SyncProviderKind.GoogleDrive)
            {
                // The capture harness never authenticates, so the connected
                // state is described here rather than obtained. The manifest
                // records this as a showcase provider state, not as evidence
                // that Drive was contacted.
                viewModel.HasStoredAuthentication = true;
                viewModel.GoogleDriveAccountDisplayName = "Showcase Account";
                viewModel.GoogleDriveAccountEmail = "showcase@example.com";
                viewModel.GoogleDriveConnectionStatus = GoogleDriveConnectionStatus.Connected;
                viewModel.GoogleDriveConnectionMessage =
                    "Connected to Google Drive. Only files this application creates are visible to it.";
                viewModel.GoogleDriveRootFolderDisplayName =
                    GoogleDriveApplicationRoot.DisplayName;
                viewModel.GoogleDriveRootFolderStatus = GoogleDriveRootFolderStatus.Ready;
                viewModel.GoogleDriveRootFolderMessage =
                    $"The backup folder \"{GoogleDriveApplicationRoot.DisplayName}\" is ready.";
            }
            else
            {
                viewModel.ConnectionCheckMessage =
                    "Connected. In sync: 4, to upload: 2, to download: 1, conflicts: 0.";
            }

            Replace(viewModel.Items, SyncItems());

            Replace(viewModel.Warnings, new[]
            {
                Warning("SyncConflict",
                    "1 backup run exists on both sides with different contents. Conflicts are reported and never copied automatically.",
                    TransferWarningSeverity.Warning),
                Warning("SyncCreateOnly",
                    "Synchronization copies completed backup runs only, and never overwrites or deletes a run on either side.",
                    TransferWarningSeverity.Info),
            });

            viewModel.SummaryDisplay =
                "7 run(s): 4 in sync, 2 to upload, 1 to download, 1 conflict.";
            viewModel.SelectedSummaryDisplay =
                "Selected: 3 run(s), 871.3 MB.";
            viewModel.CanExecuteSync = providerScenario != GalleryProviders.Results;
            viewModel.ConfirmSync = false;
            viewModel.StatusMessage = providerScenario switch
            {
                GalleryProviders.Preview =>
                    "Sync preview ready. Nothing has been copied yet.",
                GalleryProviders.Results =>
                    "Sync complete: 2 uploaded, 1 downloaded, 1 conflict skipped.",
                _ => "Remote profile loaded. Build a sync preview to see what would be copied.",
            };

            if (providerScenario == GalleryProviders.Results)
            {
                Replace(viewModel.ExecutionResults, SyncResults());
                viewModel.ExecutionStatusMessage =
                    "Sync complete: 2 uploaded, 1 downloaded, 1 conflict skipped, 0 failed.";
                viewModel.CanExecuteSync = false;
            }
            else
            {
                viewModel.ExecutionResults.Clear();
                viewModel.ExecutionStatusMessage = "No sync executed.";
            }

            Replace(viewModel.SyncLog, new[]
            {
                SyncLog("WORKSTATION", 0, 2, 1, 0, 913309696,
                    new[] { "107410/20260828-194200" },
                    new[] { "413150/20260826-081500" }),
                SyncLog("LAPTOP", 3, 3, 0, 1, 411041792,
                    new[] { "427520/20260824-201100" },
                    Array.Empty<string>()),
                SyncLog("WORKSTATION", 6, 1, 2, 0, 138412032,
                    Array.Empty<string>(),
                    new[] { "292030/20260822-173000" }),
            });
        }

        private static IReadOnlyList<SyncItemRowViewModel> SyncItems() => new[]
        {
            SyncItem("107410/20260828-194200", "Arma 3", SyncItemAction.UploadToRemote, 142, 486539264, "Local only - would upload"),
            SyncItem("1086940/20260826-081500", "Baldur's Gate 3", SyncItemAction.UploadToRemote, 118, 2415919104, "Local only - would upload"),
            SyncItem("413150/20260826-081500", "Stardew Valley", SyncItemAction.DownloadToLocal, 38, 27262976, "Remote only - would download"),
            SyncItem("427520/20260824-201100", "Factorio", SyncItemAction.Conflict, 51, 118489088, "Same name, different contents - resolve manually"),
            SyncItem("292030/20260822-173000", "The Witcher 3: Wild Hunt", SyncItemAction.InSync, 46, 1073741824, "In sync"),
            SyncItem("105600/20260819-101200", "Terraria", SyncItemAction.InSync, 63, 71303168, "In sync"),
            SyncItem("730/20260815-224500", "Counter-Strike 2", SyncItemAction.InSync, 16, 67108864, "In sync"),
        };

        private static SyncItemRowViewModel SyncItem(
            string runName, string game, SyncItemAction action,
            int files, long bytes, string status) =>
            new(new SyncItem(
                runName,
                action,
                ExistsLocally: action != SyncItemAction.DownloadToLocal,
                ExistsRemotely: action != SyncItemAction.UploadToRemote,
                LocalPath: $@"{BackupRoot}\{runName.Replace('/', '\\')}",
                RemotePath: $"{SyncFolder}\\{runName.Replace('/', '\\')}",
                game,
                files,
                bytes,
                status));

        private static IReadOnlyList<SyncItemResultRowViewModel> SyncResults() =>
            SyncItems()
                .Where(row => row.Item.Action != SyncItemAction.InSync)
                .Select(row => new SyncItemResultRowViewModel(new SyncItemResult(
                    row.Item,
                    row.Item.TotalBytes,
                    row.Item.Action switch
                    {
                        SyncItemAction.UploadToRemote => SyncItemStatus.Uploaded,
                        SyncItemAction.DownloadToLocal => SyncItemStatus.Downloaded,
                        _ => SyncItemStatus.SkippedConflict,
                    },
                    Error: null)))
                .ToArray();

        private static SyncLogEntryRowViewModel SyncLog(
            string device, int daysAgo, int uploaded, int downloaded,
            int conflicts, long bytes,
            IReadOnlyList<string> uploadedRuns, IReadOnlyList<string> downloadedRuns) =>
            new(new SyncLogEntry(
                device,
                Epoch.AddDays(-daysAgo),
                uploaded,
                downloaded,
                conflicts,
                bytes,
                uploadedRuns,
                downloadedRuns));

        private static TransferWarningRowViewModel Warning(
            string code, string message, TransferWarningSeverity severity) =>
            new(new TransferPreviewWarning(code, message, severity));

        // Lets a view model's own background load finish before the fixture
        // writes over its result. The pool work completes during the pause and
        // its continuation is drained by the dispatcher pump.
        private static void Settle()
        {
            for (int pass = 0; pass < 4; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(25);
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();

            foreach (T item in items)
                target.Add(item);
        }
    }
}
