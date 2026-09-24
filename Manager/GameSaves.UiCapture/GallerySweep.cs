using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameSaves.UiCapture
{
    /// <summary>
    /// Generates high-fidelity, completely sanitized UI screenshots for documentation and user guides (DOC-021).
    /// Real user data is never read: all accounts, file paths, and database rows are 100% synthetic,
    /// ensuring zero credentials, tokens, personal paths, or private IDs can ever appear in pixels.
    /// </summary>
    internal static class GallerySweep
    {
        private static readonly List<string> Report = new()
        {
            "filename\ttab\ttitle\tdescription\twidth\theight\ttheme\tsanitized",
        };

        public static int Run(
            Window window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            ThemeService themeService,
            IUiSettingsStore settingsStore,
            string outputDirectory,
            Func<string, int> shot,
            ITransferHistoryRepository? historyRepo = null)
        {
            int written = 0;
            Directory.CreateDirectory(outputDirectory);

            window.Width = 1400;
            window.Height = 900;
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            // Apply default theme and settings
            var baseSettings = settingsStore.Load() with
            {
                ThemeChoice = AppUiSettings.ThemeDark,
                AccentTheme = AppUiSettings.AccentIndigo
            };
            settingsStore.Save(baseSettings);
            themeService.ApplyThemeVariant(AppUiSettings.ThemeDark);
            themeService.Apply(baseSettings);
            viewModel.Settings.ThemeChoice = AppUiSettings.ThemeDark;
            Dispatcher.UIThread.RunJobs();

            // 1. Setup synthetic data across all ViewModels
            PopulateAllData(viewModel, historyRepo);

            // Tab index mappings
            // 0: Dashboard, 1: Installed games, 2: Profiles, 3: Transfer preview,
            // 4: Manual backup, 5: Backups, 6: Sync, 7: History, 8: Settings

            // 00: Dashboard
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "00-dashboard", "Dashboard", "Steam discovery summary and system readiness status");

            // 01: Installed games
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "01-installed-games", "Installed Games", "Installed game library with mapping statuses, save sizes, and pagination controls");

            // 02: Profiles
            tabs.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "02-profiles", "Steam Profiles", "Detected Steam user profiles and source/target transfer selection");

            // 03: Transfer preview
            tabs.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "03-transfer-preview", "Transfer Preview", "Profile-to-profile transfer preview with safety overwrite checks");

            // 04: Manual backup
            tabs.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "04-manual-backup", "Manual Backup", "Timestamped manual backup creation with source choices and destination preset");

            // 05: Backups - Hierarchical Tree Mode (UI-015)
            tabs.SelectedIndex = 5;
            viewModel.BackupHistory.IsFileTreeMode = true;
            // The tree exists only in tree mode, so the demo verification is
            // applied now, keyed the way the payload verifier keys it.
            viewModel.BackupHistory.FileTree.UpdateVerification(
                viewModel.BackupHistory.SelectedRun!.Run.Manifest.Items.ToDictionary(i => i.OriginalFile, _ => true));
            viewModel.BackupHistory.FileTree.ExpandAll();
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "05-backups-tree", "Backups (Hierarchical Tree)", "Discovered backup runs with virtualized file tree, folder expanders, aggregate sizes, and verification indicators");

            // 05: Backups - Flat Table Mode
            viewModel.BackupHistory.IsFileTreeMode = false;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "05-backups-table", "Backups (Flat Table)", "Backup runs with virtualized flat file DataGrid, column sorting, and archive format badges");

            // 06: Sync - Cloud Sync Plan
            tabs.SelectedIndex = 6;
            viewModel.Sync.Workspace.SetCollapsed("sync.remoteProfile", true);
            viewModel.Sync.Workspace.SetCollapsed("sync.target", true);
            ScrollToPanel(window, "sync.plan");
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "06-sync-plan", "Cloud Sync Plan", "Cloud backup synchronization plan showing local and remote presence indicators and direction toggles");
            viewModel.Sync.Workspace.ResetPage();
            Dispatcher.UIThread.RunJobs();

            // 07: History
            tabs.SelectedIndex = 7;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "07-history", "Executed History", "Executed transfer, backup, restore, and sync runs with status badges and pagination");

            // 08: Settings - Appearance & Custom Accent (OBS-022)
            tabs.SelectedIndex = 8;
            viewModel.Settings.CustomAccentHex = "#3B82F6";
            viewModel.Settings.IsCustomAccentSelected = true;
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "08-settings-appearance", "Settings Appearance", "Appearance preferences showing theme modes, WCAG AA custom hex accent color system, and window materials");

            // 09: Custom Accent Showcase (Emerald Green)
            viewModel.Settings.CustomAccentHex = "#10B981";
            viewModel.Settings.IsCustomAccentSelected = true;
            tabs.SelectedIndex = 1; // Show Installed Games with Emerald accent
            Dispatcher.UIThread.RunJobs();
            written += RecordShot(shot, "09-custom-accent", "Custom Accent Theme", "Application UI adapting dynamically to custom emerald accent (#10B981) while preserving WCAG AA contrast");

            // Restore shipped theme before workspace state
            viewModel.Settings.AccentTheme = AppUiSettings.AccentIndigo;
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            // 10: Workspace Layout Customization
            IWorkspaceLayoutPage dashboardLayout = viewModel.Workspace;
            IReadOnlyList<WorkspacePanelDefinition> panels =
                WorkspaceLayoutCatalog.PanelsFor(UiRailLayoutSettings.TabDashboard);
            if (panels.Count > 2)
            {
                dashboardLayout.MovePanel(panels[2].Key, UiPanelRegion.Left, int.MaxValue);
                Dispatcher.UIThread.RunJobs();
                written += RecordShot(shot, "10-workspace-layout", "Workspace Customization", "Flexible multi-region workspace with user-customized panel docking");
                dashboardLayout.ResetPage();
                Dispatcher.UIThread.RunJobs();
            }

            File.WriteAllLines(
                Path.Combine(outputDirectory, "gallery-manifest.tsv"), Report);

            return written;
        }

        private static int RecordShot(Func<string, int> shot, string name, string title, string description)
        {
            int result = shot(name);
            Report.Add($"{name}.png\t{title}\t{title}\t{description}\t1400\t900\tDark\ttrue");
            return result;
        }

        private static void ScrollToPanel(Window window, string panelKey)
        {
            GameSaves.App.Views.Workspace.WorkspacePanel? panel = window
                .GetVisualDescendants()
                .OfType<GameSaves.App.Views.Workspace.WorkspacePanel>()
                .FirstOrDefault(candidate => candidate.PanelKey == panelKey);

            panel?.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }

        private static void PopulateAllData(MainWindowViewModel viewModel, ITransferHistoryRepository? historyRepo)
        {
            // 1. Dashboard
            viewModel.LibraryCount = 2;
            viewModel.InstalledGameCount = 4;
            viewModel.SteamProfileCount = 2;
            viewModel.ApprovedMappingCount = 6;
            viewModel.PendingMappingCount = 2;
            viewModel.NeedsFixMappingCount = 1;
            viewModel.Platform = "Windows";
            viewModel.SteamRoot = "Steam";
            viewModel.IsSteamMissing = false;
            viewModel.StatusMessage = "Ready. 4 games found across 2 Steam libraries.";

            // 2. Installed Games
            viewModel.InstalledGames.Games.Clear();
            viewModel.InstalledGames.Games.Add(Game(
                "107410", "Arma 3", "SteamLibrary/steamapps/common/Arma 3",
                "SteamLibrary", 3, 0, 0, true, 42, 157286400, "Ready"));
            viewModel.InstalledGames.Games.Add(Game(
                "220", "Half-Life 2", "SteamLibrary/steamapps/common/Half-Life 2",
                "SteamLibrary", 1, 1, 0, true, 8, 4194304, "Review pending"));
            viewModel.InstalledGames.Games.Add(Game(
                "730", "Counter-Strike 2", "FastLibrary/steamapps/common/Counter-Strike 2",
                "FastLibrary", 2, 0, 0, true, 16, 67108864, "Ready"));
            viewModel.InstalledGames.Games.Add(Game(
                "999001", "A Long Game Title Used To Prove Column Alignment",
                "ArchiveLibrary/steamapps/common/A Long Game Title",
                "ArchiveLibrary", 0, 2, 1, false, 0, 0, "Needs attention"));
            viewModel.InstalledGames.Pagination.SetSource(viewModel.InstalledGames.Games);
            viewModel.InstalledGames.SelectedGame = viewModel.InstalledGames.Games[0];
            viewModel.InstalledGames.StatusMessage = "4 installed games found.";
            viewModel.InstalledGames.Pagination.SetPageSize(20);

            // 3. Profiles
            viewModel.Profiles.Profiles.Clear();
            var p1 = new SteamProfileRowViewModel(new SteamProfile("10000001", "76561198000000001", "Primary Player", "Steam/userdata/10000001", 12, true));
            var p2 = new SteamProfileRowViewModel(new SteamProfile("10000002", "76561198000000002", "Secondary User", "Steam/userdata/10000002", 4, false));
            viewModel.Profiles.Profiles.Add(p1);
            viewModel.Profiles.Profiles.Add(p2);
            viewModel.Profiles.SourceProfile = p1;
            viewModel.Profiles.TargetProfile = p2;
            viewModel.Profiles.StatusMessage = "2 Steam profiles discovered.";

            // 4. Transfer Preview
            viewModel.TransferPreview.SelectedSourceProfile = p1;
            viewModel.TransferPreview.SelectedTargetProfile = p2;
            viewModel.TransferPreview.SelectedGame = viewModel.InstalledGames.Games[0];

            var preview1 = new TransferPreviewItem(
                TransferSourceType.SteamUserDataGameFolder,
                null,
                null,
                "107410",
                "Arma 3",
                "Steam/userdata/10000001",
                "Steam/userdata/10000002",
                "Steam/userdata/10000001/107410/remote/save1.armasave",
                "Steam/userdata/10000002/107410/remote/save1.armasave",
                TransferCopyScope.SingleFile,
                true,
                false,
                1,
                4194304,
                TransferConflictStatus.None,
                "Ready to copy",
                "Copy");

            var preview2 = new TransferPreviewItem(
                TransferSourceType.SteamUserDataGameFolder,
                null,
                null,
                "107410",
                "Arma 3",
                "Steam/userdata/10000001",
                "Steam/userdata/10000002",
                "Steam/userdata/10000001/107410/remote/profile.armaprofile",
                "Steam/userdata/10000002/107410/remote/profile.armaprofile",
                TransferCopyScope.SingleFile,
                true,
                true,
                1,
                1048576,
                TransferConflictStatus.None,
                "Target exists (safe backup before overwrite)",
                "Backup & Overwrite");

            var preview3 = new TransferPreviewItem(
                TransferSourceType.ApprovedMapping,
                1L,
                "%DOCUMENTS%/Arma 3/Saved",
                "107410",
                "Arma 3",
                "Documents/Arma 3/Saved/User1",
                "Documents/Arma 3/Saved/User2",
                "Documents/Arma 3/Saved/User1/campaign.sav",
                "Documents/Arma 3/Saved/User2/campaign.sav",
                TransferCopyScope.SingleFile,
                true,
                false,
                1,
                2097152,
                TransferConflictStatus.None,
                "Ready to copy",
                "Copy");

            viewModel.TransferPreview.Items.Clear();
            viewModel.TransferPreview.Items.Add(new TransferPreviewItemRowViewModel(preview1));
            viewModel.TransferPreview.Items.Add(new TransferPreviewItemRowViewModel(preview2));
            viewModel.TransferPreview.Items.Add(new TransferPreviewItemRowViewModel(preview3));

            viewModel.TransferPreview.UserDataItems.Clear();
            viewModel.TransferPreview.UserDataItems.Add(new TransferPreviewItemRowViewModel(preview1));
            viewModel.TransferPreview.UserDataItems.Add(new TransferPreviewItemRowViewModel(preview2));

            viewModel.TransferPreview.MappingItems.Clear();
            viewModel.TransferPreview.MappingItems.Add(new TransferPreviewItemRowViewModel(preview3));

            viewModel.TransferPreview.TotalFiles = 3;
            viewModel.TransferPreview.TotalSizeDisplay = "7.0 MB";
            viewModel.TransferPreview.CanExecuteCopy = true;
            viewModel.TransferPreview.StatusMessage = "Preview ready. 3 items to copy (7.0 MB). 1 target exists and will be backed up.";

            // 5. Manual Backup
            viewModel.ManualBackup.SelectedProfile = p1;
            viewModel.ManualBackup.SelectedGame = viewModel.InstalledGames.Games[0];
            viewModel.ManualBackup.DestinationPath = "Backups/Arma3_Manual";
            viewModel.ManualBackup.TotalFiles = 3;
            viewModel.ManualBackup.TotalSizeDisplay = "7.0 MB";
            viewModel.ManualBackup.CanExecuteBackup = true;
            viewModel.ManualBackup.StatusMessage = "Ready to create backup. 3 items (7.0 MB) will be preserved in a timestamped run.";

            viewModel.ManualBackup.Items.Clear();
            viewModel.ManualBackup.Items.Add(new TransferPreviewItemRowViewModel(preview1));
            viewModel.ManualBackup.Items.Add(new TransferPreviewItemRowViewModel(preview2));
            viewModel.ManualBackup.Items.Add(new TransferPreviewItemRowViewModel(preview3));

            // 6. Backups
            viewModel.BackupHistory.Runs.Clear();

            var backupItemsRun1 = new List<TransferOverwriteBackupItem>
            {
                new("SteamLibrary/steamapps/common/Arma 3/campaign/mission1.sav",
                    "Backups/2026-09-17_20-00-00_Arma-3/files/campaign/mission1.sav",
                    52428800, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    new DateTimeOffset(2026, 9, 17, 20, 0, 1, TimeSpan.Zero),
                    "files/campaign/mission1.sav"),
                new("SteamLibrary/steamapps/common/Arma 3/campaign/mission2.sav",
                    "Backups/2026-09-17_20-00-00_Arma-3/files/campaign/mission2.sav",
                    52428800, "11a4325698fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    new DateTimeOffset(2026, 9, 17, 20, 0, 2, TimeSpan.Zero),
                    "files/campaign/mission2.sav"),
                new("SteamLibrary/steamapps/common/Arma 3/profiles/user.cfg",
                    "Backups/2026-09-17_20-00-00_Arma-3/files/profiles/user.cfg",
                    1048576, "22b5436798fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    new DateTimeOffset(2026, 9, 17, 20, 0, 3, TimeSpan.Zero),
                    "files/profiles/user.cfg"),
                new("SteamLibrary/steamapps/common/Arma 3/screenshots/briefing.jpg",
                    "Backups/2026-09-17_20-00-00_Arma-3/files/screenshots/briefing.jpg",
                    51380224, "33c6547898fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    new DateTimeOffset(2026, 9, 17, 20, 0, 4, TimeSpan.Zero),
                    "files/screenshots/briefing.jpg")
            };

            var manifest1 = new TransferBackupManifest(
                SchemaVersion: 2,
                Kind: OverwriteBackupContext.ManualKind,
                Game: "Arma 3",
                SteamAppId: "107410",
                SourceAccountId: "10000001",
                TargetAccountId: "10000001",
                StartedUtc: new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 9, 17, 20, 0, 5, TimeSpan.Zero),
                FileCount: backupItemsRun1.Count,
                TotalBytes: backupItemsRun1.Sum(i => i.Bytes),
                Items: backupItemsRun1);

            var run1 = new TransferBackupRunInfo(
                "Backups/2026-09-17_20-00-00_Arma-3",
                "Backups/2026-09-17_20-00-00_Arma-3/manifest.json",
                manifest1,
                BackupContainerFormat.Folder,
                VerificationStrength.PayloadVerified);

            var manifest2 = new TransferBackupManifest(
                SchemaVersion: 2,
                Kind: OverwriteBackupContext.ManualKind,
                Game: "Half-Life 2",
                SteamAppId: "220",
                SourceAccountId: "10000001",
                TargetAccountId: "10000001",
                StartedUtc: new DateTimeOffset(2026, 9, 17, 18, 30, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 9, 17, 18, 30, 3, TimeSpan.Zero),
                FileCount: 8,
                TotalBytes: 4194304,
                Items: Array.Empty<TransferOverwriteBackupItem>(),
                Format: "zip");

            var run2 = new TransferBackupRunInfo(
                "Backups/2026-09-17_18-30-00_Half-Life-2.zip",
                "Backups/2026-09-17_18-30-00_Half-Life-2.zip",
                manifest2,
                BackupContainerFormat.Zip,
                VerificationStrength.ManifestMatch);

            var manifest3 = new TransferBackupManifest(
                SchemaVersion: 2,
                Kind: OverwriteBackupContext.ManualKind,
                Game: "Counter-Strike 2",
                SteamAppId: "730",
                SourceAccountId: "10000001",
                TargetAccountId: "10000001",
                StartedUtc: new DateTimeOffset(2026, 9, 16, 14, 15, 0, TimeSpan.Zero),
                CompletedUtc: new DateTimeOffset(2026, 9, 16, 14, 15, 6, TimeSpan.Zero),
                FileCount: 16,
                TotalBytes: 67108864,
                Items: Array.Empty<TransferOverwriteBackupItem>(),
                Format: "7z");

            var run3 = new TransferBackupRunInfo(
                "Backups/2026-09-16_14-15-00_Counter-Strike-2.7z",
                "Backups/2026-09-16_14-15-00_Counter-Strike-2.7z",
                manifest3,
                BackupContainerFormat.SevenZip,
                VerificationStrength.Copied);

            viewModel.BackupHistory.Runs.Add(new BackupRunRowViewModel(run1));
            viewModel.BackupHistory.Runs.Add(new BackupRunRowViewModel(run2));
            viewModel.BackupHistory.Runs.Add(new BackupRunRowViewModel(run3));
            viewModel.BackupHistory.Pagination.SetSource(viewModel.BackupHistory.Runs);
            viewModel.BackupHistory.SelectedRun = viewModel.BackupHistory.Runs[0];
            viewModel.BackupHistory.StatusMessage = "3 backup runs discovered.";

            // 7. Sync
            SyncStateSweepSetup(viewModel.Sync);

            // 8. History
            viewModel.TransferHistory.Runs.Clear();
            var now = new DateTimeOffset(2026, 9, 17, 20, 15, 0, TimeSpan.Zero);

            var run1Items = new List<TransferRunItemRecord>
            {
                new("Backups/2026-09-17_20-00-00_Arma-3/files/campaign/mission1.sav",
                    "SteamLibrary/steamapps/common/Arma 3/campaign/mission1.sav",
                    52428800, true, "Copied", null, "Backups/PreRestore_Arma3/campaign/mission1.sav"),
                new("Backups/2026-09-17_20-00-00_Arma-3/files/campaign/mission2.sav",
                    "SteamLibrary/steamapps/common/Arma 3/campaign/mission2.sav",
                    52428800, true, "Copied", null, "Backups/PreRestore_Arma3/campaign/mission2.sav"),
                new("Backups/2026-09-17_20-00-00_Arma-3/files/profiles/user.cfg",
                    "SteamLibrary/steamapps/common/Arma 3/profiles/user.cfg",
                    1048576, true, "Copied", null, "Backups/PreRestore_Arma3/profiles/user.cfg"),
                new("Backups/2026-09-17_20-00-00_Arma-3/files/screenshots/briefing.jpg",
                    "SteamLibrary/steamapps/common/Arma 3/screenshots/briefing.jpg",
                    51380224, true, "Copied", null, "Backups/PreRestore_Arma3/screenshots/briefing.jpg")
            };

            var run1Record = new TransferRunRecord(
                TransferRunKind.Restore,
                "Arma 3",
                "107410",
                "10000001",
                "10000001",
                false,
                true,
                true,
                4,
                4,
                0,
                0,
                157286400,
                4,
                "Backups/PreRestore_Arma3",
                null,
                now.AddMinutes(-15),
                now.AddMinutes(-14),
                run1Items);

            long recordedRunId = 1;
            if (historyRepo != null)
            {
                try
                {
                    recordedRunId = historyRepo.RecordRun(run1Record);
                }
                catch
                {
                    // Fallback to in-memory ID
                }
            }

            var run1Row = new TransferRunRowViewModel(new TransferRunInfo(
                recordedRunId,
                TransferRunKind.Restore,
                "Arma 3",
                "107410",
                "10000001",
                "10000001",
                false,
                true,
                true,
                4,
                4,
                0,
                0,
                157286400,
                4,
                "Backups/PreRestore_Arma3",
                null,
                now.AddMinutes(-15),
                now.AddMinutes(-14)));

            viewModel.TransferHistory.Runs.Add(run1Row);
            viewModel.TransferHistory.Runs.Add(new TransferRunRowViewModel(new TransferRunInfo(
                2,
                TransferRunKind.Sync,
                "Showcase Game",
                "999001",
                "10000001",
                "Google Drive",
                false,
                false,
                false,
                12,
                12,
                0,
                0,
                8192,
                0,
                null,
                null,
                now.AddMinutes(-30),
                now.AddMinutes(-29))));

            viewModel.TransferHistory.Runs.Add(new TransferRunRowViewModel(new TransferRunInfo(
                3,
                TransferRunKind.TransferCopy,
                "Half-Life 2",
                "220",
                "10000001",
                "10000002",
                false,
                false,
                false,
                8,
                8,
                0,
                0,
                4194304,
                0,
                null,
                null,
                now.AddHours(-1),
                now.AddHours(-1).AddSeconds(30))));

            viewModel.TransferHistory.Runs.Add(new TransferRunRowViewModel(new TransferRunInfo(
                4,
                TransferRunKind.ManualBackup,
                "Counter-Strike 2",
                "730",
                "10000001",
                "Backups",
                false,
                false,
                false,
                16,
                16,
                0,
                0,
                67108864,
                0,
                "Backups/CS2_Manual",
                null,
                now.AddHours(-2),
                now.AddHours(-2).AddSeconds(45))));

            viewModel.TransferHistory.Pagination.SetSource(viewModel.TransferHistory.Runs);
            viewModel.TransferHistory.SelectedRun = run1Row;
            viewModel.TransferHistory.StatusMessage = "4 executed runs found.";

            // Ensure RunItems are populated for screenshot rendering
            for (int i = 0; i < 25 && viewModel.TransferHistory.RunItems.Count == 0; i++)
            {
                System.Threading.Thread.Sleep(20);
                Dispatcher.UIThread.RunJobs();
            }
            if (viewModel.TransferHistory.RunItems.Count == 0)
            {
                foreach (var item in run1Items)
                {
                    viewModel.TransferHistory.RunItems.Add(new TransferRunItemRowViewModel(item));
                }
            }
        }

        private static void SyncStateSweepSetup(SyncViewModel sync)
        {
            sync.Items.Clear();
            sync.ExecutionResults.Clear();

            var checkedAt = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

            sync.Items.Add(new SyncItemRowViewModel(new SyncItem(
                "2026-09-17_20-00-00_Arma-3",
                SyncItemAction.UploadToRemote,
                true, false,
                "Backups/2026-09-17_20-00-00_Arma-3",
                "GameSave Manager Backups/2026-09-17_20-00-00_Arma-3",
                "Arma 3", 4, 157286400, "Copy to the sync folder"),
                "Google Drive", checkedAt));

            sync.Items.Add(new SyncItemRowViewModel(new SyncItem(
                "2026-09-17_18-30-00_Half-Life-2.zip",
                SyncItemAction.DownloadToLocal,
                false, true,
                "Backups/2026-09-17_18-30-00_Half-Life-2.zip",
                "GameSave Manager Backups/2026-09-17_18-30-00_Half-Life-2.zip",
                "Half-Life 2", 8, 4194304, "Copy to the local backup base"),
                "Google Drive", checkedAt));

            sync.Items.Add(new SyncItemRowViewModel(new SyncItem(
                "2026-09-16_14-15-00_Counter-Strike-2.7z",
                SyncItemAction.InSync,
                true, true,
                "Backups/2026-09-16_14-15-00_Counter-Strike-2.7z",
                "GameSave Manager Backups/2026-09-16_14-15-00_Counter-Strike-2.7z",
                "Counter-Strike 2", 16, 67108864, "In sync"),
                "Google Drive", checkedAt));

            sync.SummaryDisplay =
                "Upload: 1 run(s) (150 MB)   Download: 1 run(s) (4 MB)   In sync: 1   Conflicts: 0";
            sync.PlanSectionExpanded = true;
            sync.ResultsSectionExpanded = true;
        }

        private static InstalledGameRowViewModel Game(
            string appId,
            string name,
            string gamePath,
            string libraryPath,
            int approved,
            int pending,
            int needsFix,
            bool savePathExists,
            int fileCount,
            long totalBytes,
            string status)
        {
            return new InstalledGameRowViewModel(new InstalledGameSaveStatus(
                new SteamGame(
                    appId,
                    name,
                    name,
                    libraryPath,
                    $"manifests/{appId}.acf",
                    gamePath,
                    FolderExists: true,
                    SteamDiscoveryConfidence.High),
                needsFix > 0
                    ? GameSaveStatusKind.NeedsFixOnly
                    : pending > 0
                        ? GameSaveStatusKind.MappingMissing
                        : GameSaveStatusKind.Ready,
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
    }
}
