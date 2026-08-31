using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;

namespace GameSaves.UiCapture
{
    // Walks the workspace-layout acceptance matrix and writes one PNG per
    // (page, arrangement, window size). The arrangements are the ones the bug
    // reports show breaking: a user-style swap of the first and last section,
    // both rails occupied with nothing left in the centre, and every hideable
    // section hidden — followed by a reset, which must come back identical to
    // the default capture taken first.
    internal static class LayoutSweep
    {
        // The sizes the acceptance criteria name.
        private static readonly (string Slug, int Width, int Height)[] Sizes =
        {
            ("1280x720", 1280, 720),
            ("1366x768", 1366, 768),
            ("1920x1080", 1920, 1080),
        };

        // Rail order is the tab order, so a page key resolves to a tab index
        // without the harness needing to reach into the window's item list.
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

        // Written beside the captures: what the surface actually placed, so a
        // gap in a PNG can be attributed to a region rather than guessed at.
        private static readonly List<string> Report = new();

        private static void Describe(Window window, string state)
        {
            foreach (GameSaves.App.Views.Workspace.WorkspaceSurface surface in window
                .GetVisualDescendants()
                .OfType<GameSaves.App.Views.Workspace.WorkspaceSurface>())
            {
                Report.Add(
                    $"{state}\tSURFACE\tbounds={surface.Bounds.Width:0}x{surface.Bounds.Height:0}\t" +
                    $"desired={surface.DesiredSize.Width:0}x{surface.DesiredSize.Height:0}");
            }

            foreach (GameSaves.App.Views.Workspace.WorkspacePanel panel in window
                .GetVisualDescendants()
                .OfType<GameSaves.App.Views.Workspace.WorkspacePanel>())
            {
                Report.Add(
                    $"{state}\t{panel.PanelKey}\tregion={panel.Region}\t" +
                    $"visible={panel.IsVisible}\t" +
                    $"x={panel.Bounds.X:0}\tw={panel.Bounds.Width:0}\t" +
                    $"y={panel.Bounds.Y:0}\th={panel.Bounds.Height:0}");
            }
        }

        // A floating panel lives in its own window, so it is absent from the
        // main window's tree while floating and present again once docked.
        // Reporting that presence is what makes an orphaned panel — in neither
        // window — visible in the report rather than only on screen.
        private static void FloatRoundTrip(
            Window window,
            IWorkspaceLayoutPage layout,
            IReadOnlyList<WorkspacePanelDefinition> panels,
            string pageKey,
            string sizeSlug)
        {
            WorkspacePanelDefinition target =
                panels.LastOrDefault(panel => panel.CanFloat) ?? panels[^1];

            layout.FloatPanel(target.Key, 200, 200, 480, 360);
            Dispatcher.UIThread.RunJobs();
            Describe(window, $"{pageKey}_{sizeSlug}_05-floated");

            // Any change forces the rebuild that used to orphan it.
            layout.SetCollapsed(panels[0].Key, true);
            layout.SetCollapsed(panels[0].Key, false);
            Dispatcher.UIThread.RunJobs();
            Describe(window, $"{pageKey}_{sizeSlug}_06-floated-after-rebuild");

            layout.DockPanel(target.Key);
            Dispatcher.UIThread.RunJobs();
            Describe(window, $"{pageKey}_{sizeSlug}_07-docked-back");

            layout.ResetPage();
            Dispatcher.UIThread.RunJobs();
        }

        public static int Run(
            Window window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            string outputDirectory,
            Func<string, int> shot)
        {
            int written = 0;

            foreach (string pageKey in WorkspaceLayoutCatalog.Pages)
            {
                int index = Array.IndexOf(TabOrder, pageKey);

                if (index < 0)
                    continue;

                IWorkspaceLayoutPage layout = viewModel.WorkspacePageFor(pageKey);
                IReadOnlyList<WorkspacePanelDefinition> panels =
                    WorkspaceLayoutCatalog.PanelsFor(pageKey);

                foreach ((string sizeSlug, int width, int height) in Sizes)
                {
                    window.Width = width;
                    window.Height = height;
                    tabs.SelectedIndex = index;
                    layout.ResetPage();
                    Dispatcher.UIThread.RunJobs();

                    written += shot($"{pageKey}_{sizeSlug}_00-default");

                    // What the reports show: the page header pushed into one
                    // rail and the pane that absorbs the slack into the other.
                    layout.MovePanel(panels[0].Key, UiPanelRegion.Left, int.MaxValue);
                    layout.MovePanel(
                        panels[^1].Key, UiPanelRegion.Right, int.MaxValue);
                    Dispatcher.UIThread.RunJobs();
                    written += shot($"{pageKey}_{sizeSlug}_01-swapped");
                    Describe(window, $"{pageKey}_{sizeSlug}_01-swapped");

                    // Every section in a rail, so the centre is empty. This is
                    // the arrangement that used to leave a star-sized hole in
                    // the middle of the page.
                    for (int panel = 0; panel < panels.Count; panel++)
                    {
                        layout.MovePanel(
                            panels[panel].Key,
                            panel % 2 == 0 ? UiPanelRegion.Left : UiPanelRegion.Right,
                            int.MaxValue);
                    }

                    Dispatcher.UIThread.RunJobs();
                    written += shot($"{pageKey}_{sizeSlug}_02-rails-only");
                    Describe(window, $"{pageKey}_{sizeSlug}_02-rails-only");

                    foreach (WorkspacePanelDefinition panel in panels)
                    {
                        if (panel.CanHide)
                            layout.SetHidden(panel.Key, true);
                    }

                    Dispatcher.UIThread.RunJobs();
                    written += shot($"{pageKey}_{sizeSlug}_03-all-hidden");
                    Describe(window, $"{pageKey}_{sizeSlug}_03-all-hidden");

                    layout.ResetPage();
                    Dispatcher.UIThread.RunJobs();
                    written += shot($"{pageKey}_{sizeSlug}_04-reset");

                    // Content that grows on its own, at an unchanged window
                    // size: a card gaining lines when data loads. The page
                    // minimum has to grow with it and the scroller take over,
                    // or the growth is absorbed by squeezing the filling pane
                    // and the bottom of the page becomes unreachable.
                    if (pageKey == UiRailLayoutSettings.TabInstalledGames)
                    {
                        string original = viewModel.InstalledGames.StatusMessage;
                        viewModel.InstalledGames.StatusMessage =
                            string.Join(" ", Enumerable.Repeat(
                                "This status line is deliberately long so the header " +
                                "card wraps to many lines and the page has to grow.", 60));
                        Dispatcher.UIThread.RunJobs();
                        written += shot($"{pageKey}_{sizeSlug}_08-grown-content");
                        Describe(window, $"{pageKey}_{sizeSlug}_08-grown-content");
                        viewModel.InstalledGames.StatusMessage = original;
                        Dispatcher.UIThread.RunJobs();
                    }

                    // Floating: the section leaves the page for its own window,
                    // has to survive a rebuild while it is there, and has to
                    // come back when docked. A rebuild used to detach it and
                    // never reparent it, leaving a blank window and no card.
                    if (sizeSlug == Sizes[0].Slug)
                        FloatRoundTrip(window, layout, panels, pageKey, sizeSlug);
                }
            }

            File.WriteAllLines(
                Path.Combine(outputDirectory, "layout-report.tsv"), Report);

            return written;
        }
    }
}



