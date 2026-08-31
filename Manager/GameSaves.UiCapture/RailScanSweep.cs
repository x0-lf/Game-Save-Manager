using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;

namespace GameSaves.UiCapture
{
    // Walks the navigation rail's Scan/Refresh action across every page and
    // every rail arrangement, and records what the button actually is at each
    // stop: visible or not, its label, its tooltip, its accessible name, and
    // which command object it is bound to. The command is reported by identity
    // against the page view models, so "runs the right page's refresh" is
    // observed rather than assumed.
    //
    // The captures are the visual half of the same matrix. Settings is
    // included on purpose: its expected row is the one with no button at all.
    internal static class RailScanSweep
    {
        private static readonly string[] TabOrder =
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
        };

        private static readonly string[] Positions =
        {
            UiRailLayoutSettings.PositionLeft,
            UiRailLayoutSettings.PositionRight,
            UiRailLayoutSettings.PositionTop,
        };

        private static readonly List<string> Report = new()
        {
            "page\tposition\trail\tvisible\tenabled\tlabel\ttooltip\taccessibleName\tcommand",
        };

        // Which page view model owns the bound command. Anything that comes
        // back "unmapped" here is the rail pointing at a command that is not
        // the active page's own, which is the defect this sweep exists to
        // catch.
        private static string CommandOwner(
            MainWindowViewModel viewModel, System.Windows.Input.ICommand? command)
        {
            if (command is null)
                return "none";

            if (ReferenceEquals(command, viewModel.RefreshCommand))
                return "dashboard.Refresh";
            if (ReferenceEquals(command, viewModel.InstalledGames.RefreshCommand))
                return "installedGames.Refresh";
            if (ReferenceEquals(command, viewModel.Profiles.RefreshProfilesCommand))
                return "profiles.RefreshProfiles";
            if (ReferenceEquals(command, viewModel.TransferPreview.RefreshInputsCommand))
                return "transferPreview.RefreshInputs";
            if (ReferenceEquals(command, viewModel.ManualBackup.RefreshInputsCommand))
                return "manualBackup.RefreshInputs";
            if (ReferenceEquals(command, viewModel.BackupHistory.RefreshRunsCommand))
                return "backups.RefreshRuns";
            if (ReferenceEquals(command, viewModel.Sync.CheckSyncStatusCommand))
                return "sync.CheckSyncStatus";
            if (ReferenceEquals(command, viewModel.TransferHistory.RefreshRunsCommand))
                return "history.RefreshRuns";

            return "unmapped";
        }

        private static void Describe(
            Window window, MainWindowViewModel viewModel, string page,
            string position, string rail)
        {
            Button? scan = window
                .GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Name == "RailScanButton");

            // The label is dropped by a style while the rail is collapsed, so
            // it is read from the rendered TextBlock rather than from the view
            // model: what the user can actually read is the thing under test.
            TextBlock? label = scan?
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("railChromeLabel"));

            string labelText = scan is null || label is null || !label.IsVisible
                ? "(none)"
                : label.Text ?? string.Empty;

            Report.Add(string.Join('\t',
                page,
                position,
                rail,
                scan?.IsVisible.ToString() ?? "missing",
                scan?.IsEnabled.ToString() ?? "missing",
                labelText,
                (ToolTip.GetTip(scan!) as string) ?? "(none)",
                AutomationProperties.GetName(scan!) ?? "(none)",
                CommandOwner(viewModel, scan?.Command)));
        }

        public static int Run(
            Window window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            string outputDirectory,
            Func<string, int> shot)
        {
            int written = 0;

            window.Width = 1366;
            window.Height = 768;

            foreach (string position in Positions)
            {
                foreach (bool collapsed in new[] { false, true })
                {
                    viewModel.Settings.RailPosition = position;
                    viewModel.Settings.RailCollapsed = collapsed;
                    Dispatcher.UIThread.RunJobs();

                    string rail = collapsed ? "collapsed" : "expanded";

                    for (int index = 0; index < TabOrder.Length; index++)
                    {
                        tabs.SelectedIndex = index;
                        Dispatcher.UIThread.RunJobs();

                        string page = TabOrder[index];
                        Describe(window, viewModel, page, position, rail);
                        written += shot($"{index:00}-{page}_{position}_{rail}");
                    }
                }
            }

            viewModel.Settings.RailPosition = UiRailLayoutSettings.PositionLeft;
            viewModel.Settings.RailCollapsed = false;
            Dispatcher.UIThread.RunJobs();

            File.WriteAllLines(
                Path.Combine(outputDirectory, "rail-scan-report.tsv"), Report);

            return written;
        }
    }
}
