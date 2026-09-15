using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.UiCapture
{
    // The Sync state matrix, rendered rather than described. Every plan
    // presence and every execution/verification state gets one row, and the
    // whole set is captured in both themes, under every accent, and in High
    // Contrast.
    //
    // The rows are pushed straight into the bound collections. That is the
    // point: no provider, no account, no disk, and therefore no state that
    // depends on a live remote being in a particular mood. Every value is a
    // literal, so a capture is comparable across runs and carries no path or
    // account name from the machine that produced it.
    //
    // The report beside the PNGs records what each row claims in words, so a
    // reviewer can check "colour is not the only indicator" without reading
    // pixels, and a future change that quietly makes two states read alike
    // shows up as two identical rows.
    internal static class SyncStateSweep
    {
        private const string RemoteLabel = "Google Drive";

        private static readonly string[] Accents =
        {
            AppUiSettings.AccentIndigo,
            AppUiSettings.AccentTeal,
            AppUiSettings.AccentRose,
            AppUiSettings.AccentAmber,
            AppUiSettings.AccentViolet,
        };

        private static readonly List<string> Report = new()
        {
            "kind\trow\tlabel\tseverity\tselectable\tselection\taccessibleName",
        };

        public static int Run(
            Window window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            ThemeService themeService,
            IUiSettingsStore settingsStore,
            string outputDirectory,
            Func<string, int> shot)
        {
            window.Width = 1400;
            window.Height = 900;

            // The Sync tab, by the same key the rail uses.
            tabs.SelectedIndex = Array.IndexOf(
                new[]
                {
                    UiRailLayoutSettings.TabDashboard,
                    UiRailLayoutSettings.TabInstalledGames,
                    UiRailLayoutSettings.TabProfiles,
                    UiRailLayoutSettings.TabTransferPreview,
                    UiRailLayoutSettings.TabManualBackup,
                    UiRailLayoutSettings.TabBackups,
                    UiRailLayoutSettings.TabSync,
                    UiRailLayoutSettings.TabHistory,
                    UiRailLayoutSettings.TabSettings,
                },
                UiRailLayoutSettings.TabSync);

            Dispatcher.UIThread.RunJobs();

            Populate(viewModel.Sync);
            Describe(viewModel.Sync);

            int written = 0;

            foreach (string theme in new[]
                     { AppUiSettings.ThemeDark, AppUiSettings.ThemeLight })
            {
                foreach (string accent in Accents)
                {
                    written += Capture(
                        window, viewModel, themeService, settingsStore, shot,
                        theme, accent, highContrast: false,
                        $"sync-states_{theme}_{accent}");
                }

                // High Contrast is authoritative: it must stay readable with
                // the semantic states intact, whatever the accent says.
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    theme, AppUiSettings.AccentIndigo, highContrast: true,
                    $"sync-states_{theme}_highcontrast");
            }

            File.WriteAllLines(
                Path.Combine(outputDirectory, "sync-state-report.tsv"), Report);

            return written;
        }

        private static int Capture(
            Window window,
            MainWindowViewModel viewModel,
            ThemeService themeService,
            IUiSettingsStore settingsStore,
            Func<string, int> shot,
            string theme,
            string accent,
            bool highContrast,
            string name)
        {
            viewModel.Settings.ThemeChoice = theme;
            viewModel.Settings.AccentTheme = accent;
            viewModel.Settings.HighContrast = highContrast;

            themeService.Apply(settingsStore.Load());
            Dispatcher.UIThread.RunJobs();

            // Plan states and execution states are two separate lists on the
            // same long page, so each combination needs both frames.
            ScrollToPanel(window, "sync.plan");
            int written = shot($"{name}_plan");

            ScrollToPanel(window, "sync.results");
            written += shot($"{name}_results");

            return written;
        }

        // The state rows sit below the connection settings, so a capture taken
        // at the top of the page would show none of what this sweep exists for.
        private static void ScrollToPanel(Window window, string panelKey)
        {
            GameSaves.App.Views.Workspace.WorkspacePanel? panel = window
                .GetVisualDescendants()
                .OfType<GameSaves.App.Views.Workspace.WorkspacePanel>()
                .FirstOrDefault(candidate => candidate.PanelKey == panelKey);

            panel?.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }

        private static void Describe(SyncViewModel sync)
        {
            foreach (SyncItemRowViewModel row in sync.Items)
            {
                Report.Add(string.Join('\t',
                    "plan",
                    row.RunName,
                    row.PresenceText,
                    row.Severity.ToString(),
                    row.IsSelectable,
                    row.SelectionStateText,
                    row.ActionAccessibleName));
            }

            foreach (SyncItemResultRowViewModel row in sync.ExecutionResults)
            {
                Report.Add(string.Join('\t',
                    "result",
                    row.RunName,
                    row.StateText,
                    row.Severity.ToString(),
                    "n/a",
                    "n/a",
                    row.StateDetail));
            }
        }

        // One row per state the UI can reach, named so the capture explains
        // itself without the report open.
        private static void Populate(SyncViewModel sync)
        {
            sync.Items.Clear();
            sync.ExecutionResults.Clear();

            var checkedAt = new DateTimeOffset(2026, 9, 5, 10, 30, 0, TimeSpan.Zero);

            void Plan(string name, SyncItem item) =>
                sync.Items.Add(new SyncItemRowViewModel(item, RemoteLabel, checkedAt));

            Plan("local-only", Item(
                "2026-09-05_09-00-00_local-only",
                SyncItemAction.UploadToRemote,
                existsLocally: true,
                existsRemotely: false,
                "Copy to the sync folder"));

            Plan("remote-only", Item(
                "2026-09-05_09-10-00_remote-only",
                SyncItemAction.DownloadToLocal,
                existsLocally: false,
                existsRemotely: true,
                "Copy to the local backup base"));

            Plan("in-sync", Item(
                "2026-09-05_09-20-00_in-sync",
                SyncItemAction.InSync,
                existsLocally: true,
                existsRemotely: true,
                "In sync"));

            Plan("conflict", Item(
                "2026-09-05_09-30-00_conflict",
                SyncItemAction.Conflict,
                existsLocally: true,
                existsRemotely: true,
                "Conflict: same name, different content. Never copied automatically."));

            // The deselected state, which is a selection state rather than a
            // plan state and therefore needs its own row.
            var deselected = new SyncItemRowViewModel(
                Item(
                    "2026-09-05_09-40-00_deselected",
                    SyncItemAction.UploadToRemote,
                    existsLocally: true,
                    existsRemotely: false,
                    "Copy to the sync folder"),
                RemoteLabel,
                checkedAt)
            {
                IncludeInSync = false
            };

            sync.Items.Add(deselected);

            // No measurable content: an interrupted upload, which cannot be
            // confirmed by comparing manifests.
            sync.Items.Add(new SyncItemRowViewModel(
                Item(
                    "2026-09-05_09-50-00_unverifiable",
                    SyncItemAction.UploadToRemote,
                    existsLocally: true,
                    existsRemotely: false,
                    "Copy to the sync folder") with
                {
                    FileCount = 0,
                    TotalBytes = 0
                },
                RemoteLabel,
                checkedAt));

            void Result(
                string name,
                SyncItemStatus status,
                long bytes,
                SyncVerificationState verification,
                string? error = null)
            {
                sync.ExecutionResults.Add(new SyncItemResultRowViewModel(
                    new SyncItemResult(
                        Item(
                            name,
                            SyncItemAction.UploadToRemote,
                            existsLocally: true,
                            existsRemotely: false,
                            "Copy to the sync folder"),
                        bytes,
                        status,
                        error),
                    RemoteLabel)
                {
                    Verification = verification
                });
            }

            Result("failed-before-copying", SyncItemStatus.Failed, 0,
                SyncVerificationState.NotRequested, "The remote refused the request.");
            Result("failed-after-partial", SyncItemStatus.Failed, 4096,
                SyncVerificationState.NotRequested, "The upload stopped partway.");
            Result("incomplete", SyncItemStatus.Incomplete, 4096,
                SyncVerificationState.NotRequested);
            Result("copied-unverified", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.NotRequested);
            Result("manifest-match", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.ManifestMatch);
            Result("sidecar-manifest-match", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.SidecarManifestMatch);
            Result("payload-verified", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.PayloadVerified);
            Result("payload-mismatch", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.PayloadMismatch);
            Result("verification-unavailable", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.EndpointUnavailable);
            Result("verification-mismatch", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.ContentMismatch);
            Result("verification-missing-remote", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.MissingRemotely);
            Result("verification-missing-local", SyncItemStatus.Downloaded, 8192,
                SyncVerificationState.MissingLocally);
            Result("verification-cancelled", SyncItemStatus.Uploaded, 8192,
                SyncVerificationState.Cancelled);
            Result("conflict-skipped", SyncItemStatus.SkippedConflict, 0,
                SyncVerificationState.NotRequested);
            Result("deselected", SyncItemStatus.SkippedDeselected, 0,
                SyncVerificationState.NotRequested);

            sync.SummaryDisplay =
                "Upload: 2 run(s) (8 KB)   Download: 1 run(s) (8 KB)   In sync: 1   Conflicts: 1";
            sync.ExecutionStatusMessage =
                "Sync finished. Uploaded 1 run(s), downloaded 1 run(s), skipped 2, " +
                "copied 16 KB. Nothing was deleted. Transferred is not yet verified.";
            sync.VerificationStatusMessage =
                "Verified in sync: 1 of 6 transferred run(s). The rest are listed with " +
                "what was actually found; nothing was changed.";
            sync.PlanSectionExpanded = true;
            sync.ResultsSectionExpanded = true;

            Dispatcher.UIThread.RunJobs();
        }

        private static SyncItem Item(
            string runName,
            SyncItemAction action,
            bool existsLocally,
            bool existsRemotely,
            string statusText) => new(
            RunName: runName,
            Action: action,
            ExistsLocally: existsLocally,
            ExistsRemotely: existsRemotely,
            LocalPath: $"backups/{runName}",
            RemotePath: $"GameSave Manager Backups/{runName}",
            GameName: "Showcase Game",
            FileCount: 12,
            TotalBytes: 8192,
            StatusText: statusText);
    }
}
