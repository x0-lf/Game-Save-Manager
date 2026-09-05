using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// Turns a <see cref="GalleryScenario"/> into live application state, and
    /// reports what the resulting layout actually did. Shared by both
    /// harnesses so a scenario means the same thing whether it is rendered
    /// headlessly or composited by Windows; only the capture step differs.
    ///
    /// Every setting is applied through the real Settings view model, which
    /// persists and re-applies exactly the way a user's click does. Nothing
    /// here reaches around the application to force a look.
    /// </summary>
    public static class GalleryScene
    {
        /// <summary>
        /// What one measured pass over the visual tree found. Used for the
        /// text-scale report; a screenshot alone cannot tell a reviewer
        /// whether a control was clipped or merely small.
        /// </summary>
        public sealed record LayoutAudit(
            int ClippedElements,
            int OverflowingElements,
            IReadOnlyList<string> Details)
        {
            public static LayoutAudit Empty { get; } =
                new(0, 0, Array.Empty<string>());

            /// <summary>
            /// PASS: nothing measured wrong. MINOR: content is cut off by a
            /// scroller, so it is off-screen but still reachable. FAIL: a
            /// control was given less room than it needs, or is cut off by a
            /// clip nothing can scroll, so text or an action is unreachable.
            /// </summary>
            public string Verdict => ClippedElements > 0
                ? "FAIL"
                : OverflowingElements > 0 ? "MINOR" : "PASS";
        }

        public static int TabIndexOf(string pageKey)
        {
            for (int index = 0; index < UiRailLayoutSettings.CanonicalTabOrder.Count; index++)
            {
                if (string.Equals(
                        UiRailLayoutSettings.CanonicalTabOrder[index],
                        pageKey,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, "Unknown page.");
        }

        public static TabControl NavigationOf(Visual root) =>
            root.GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault(control => control.Name == "MainNavigation")
            ?? root.GetVisualDescendants().OfType<TabControl>().First();

        public static TabControl? SettingsCategoriesOf(Visual root) =>
            root.GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault(control => control.Name == "SettingsCategories");

        /// <summary>
        /// Applies everything a scenario declares except the window size and
        /// the multi-window arrangement, which only the owning harness can do.
        /// </summary>
        public static void Apply(
            Window window,
            MainWindowViewModel viewModel,
            GalleryScenario scenario)
        {
            SettingsViewModel settings = viewModel.Settings;

            // High contrast first: it forces the effective material to none,
            // and applying it after a material request would briefly show a
            // transparent High Contrast window.
            settings.HighContrast = scenario.HighContrast;
            settings.ThemeChoice = scenario.Theme;
            settings.AccentTheme = scenario.Accent;
            settings.WindowMaterial = scenario.RequestedMaterial;
            settings.TextScale = scenario.TextScale;
            settings.ReduceMotion = scenario.ReduceMotion;
            settings.RailPosition = scenario.RailPosition;
            settings.RailCollapsed = scenario.RailCollapsed;

            if (scenario.ProviderScenario != GalleryProviders.None)
                GalleryShowcase.ApplySync(viewModel.Sync, scenario.ProviderScenario);

            ApplyWorkspace(viewModel, scenario.Page, scenario.Workspace);

            TabControl navigation = NavigationOf(window);
            navigation.SelectedIndex = TabIndexOf(scenario.Page);
            Dispatcher.UIThread.RunJobs();

            if (scenario.SettingsCategory is { } category &&
                SettingsCategoriesOf(window) is { } categories &&
                categories.ItemCount > category)
            {
                categories.SelectedIndex = category;
            }

            Dispatcher.UIThread.RunJobs();

            if (scenario.ScrollToPanel is { Length: > 0 } panelKey)
            {
                ScrollToPanel(window, panelKey);
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>
        /// Brings a named workspace panel to the top of its scroller, the way
        /// a user scrolling to a section would. A long page has more than one
        /// story on it, and a capture named after the lower one has to actually
        /// show it.
        /// </summary>
        public static void ScrollToPanel(Window window, string panelKey)
        {
            GameSaves.App.Views.Workspace.WorkspacePanel? panel = window
                .GetVisualDescendants()
                .OfType<GameSaves.App.Views.Workspace.WorkspacePanel>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.PanelKey, panelKey, StringComparison.Ordinal));

            if (panel is null || !panel.IsVisible)
                return;

            ScrollViewer? scroller = panel
                .GetVisualAncestors()
                .OfType<ScrollViewer>()
                .FirstOrDefault();

            if (scroller is null ||
                panel.TranslatePoint(default, scroller) is not { } relative)
            {
                return;
            }

            double maximum = Math.Max(
                0, scroller.Extent.Height - scroller.Viewport.Height);

            scroller.Offset = new Vector(
                scroller.Offset.X,
                Math.Clamp(scroller.Offset.Y + relative.Y - 8, 0, maximum));
        }

        /// <summary>
        /// Drives the real workspace layout API into the requested
        /// arrangement. Nothing is synthesized: if the product cannot reach an
        /// arrangement through these calls, no screenshot of it is produced.
        /// </summary>
        public static void ApplyWorkspace(
            MainWindowViewModel viewModel, string pageKey, string workspace)
        {
            IWorkspaceLayoutPage layout = viewModel.WorkspacePageFor(pageKey);
            IReadOnlyList<WorkspacePanelDefinition> panels =
                WorkspaceLayoutCatalog.PanelsFor(pageKey);

            layout.ResetPage();

            if (panels.Count < 3 ||
                workspace is GalleryWorkspaces.Default or GalleryWorkspaces.Restored)
            {
                Dispatcher.UIThread.RunJobs();
                return;
            }

            // Index 0 is the page header on every page; leaving it where it
            // belongs is what keeps a docked arrangement readable rather than
            // merely unusual.
            switch (workspace)
            {
                case GalleryWorkspaces.LeftRightSplit:
                    layout.MovePanel(panels[1].Key, UiPanelRegion.Left, 0);
                    layout.MovePanel(panels[^1].Key, UiPanelRegion.Right, 0);
                    break;

                case GalleryWorkspaces.TopBottom:
                    layout.MovePanel(panels[1].Key, UiPanelRegion.Top, int.MaxValue);
                    layout.MovePanel(panels[^1].Key, UiPanelRegion.Bottom, 0);
                    break;

                case GalleryWorkspaces.FourRegions:
                    layout.MovePanel(panels[1].Key, UiPanelRegion.Left, 0);
                    layout.MovePanel(panels[2].Key, UiPanelRegion.Left, 1);
                    layout.MovePanel(panels[^1].Key, UiPanelRegion.Right, 0);
                    layout.MovePanel(panels[^2].Key, UiPanelRegion.Bottom, 0);
                    break;

                case GalleryWorkspaces.Resized:
                    layout.MovePanel(panels[1].Key, UiPanelRegion.Left, 0);
                    layout.MovePanel(panels[^1].Key, UiPanelRegion.Right, 0);
                    layout.ResizeRegion(UiPanelRegion.Left, 1.6);
                    layout.ResizeRegion(UiPanelRegion.Right, 0.7);
                    break;

                case GalleryWorkspaces.Collapsed:
                    layout.SetCollapsed(panels[^1].Key, true);
                    break;

                case GalleryWorkspaces.Hidden:
                    foreach (WorkspacePanelDefinition panel in panels)
                    {
                        if (panel.CanHide)
                        {
                            layout.SetHidden(panel.Key, true);
                            break;
                        }
                    }

                    break;

                case GalleryWorkspaces.SavedCustom:
                    layout.MovePanel(panels[1].Key, UiPanelRegion.Left, 0);
                    layout.MovePanel(panels[^1].Key, UiPanelRegion.Right, 0);
                    layout.MovePanel(panels[^2].Key, UiPanelRegion.Bottom, 0);
                    layout.ResizeRegion(UiPanelRegion.Left, 1.3);
                    layout.SetCollapsed(panels[^2].Key, true);
                    break;

                case GalleryWorkspaces.FloatingPanel:
                    WorkspacePanelDefinition floatable =
                        panels.LastOrDefault(panel => panel.CanFloat) ?? panels[^1];
                    layout.FloatPanel(floatable.Key, 320, 220, 520, 380);
                    break;
            }

            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>
        /// Measures the arranged tree for the two defects a static screenshot
        /// hides: a control given less room than it asked for (text cut off,
        /// an action truncated), and content pushed past the window edge.
        /// </summary>
        public static LayoutAudit Audit(Window window)
        {
            int clipped = 0;
            int overflowing = 0;
            var details = new List<string>();
            double windowWidth = window.ClientSize.Width;

            foreach (Control control in window
                .GetVisualDescendants()
                .OfType<Control>())
            {
                if (!control.IsVisible ||
                    control.Bounds.Width <= 0 ||
                    control.Bounds.Height <= 0)
                {
                    continue;
                }

                // Only the leaves a user reads or clicks. Containers are
                // allowed to be smaller than their content: that is what a
                // scroller is for.
                if (control is not (TextBlock or Button or CheckBox or ComboBox or ToggleSwitch))
                    continue;

                // DesiredSize includes the margin; Bounds does not.
                double desiredWidth =
                    control.DesiredSize.Width - control.Margin.Left - control.Margin.Right;
                double desiredHeight =
                    control.DesiredSize.Height - control.Margin.Top - control.Margin.Bottom;

                if (desiredWidth > control.Bounds.Width + 0.5 ||
                    desiredHeight > control.Bounds.Height + 0.5)
                {
                    clipped++;

                    if (details.Count < 12)
                    {
                        details.Add(string.Create(CultureInfo.InvariantCulture,
                            $"clipped {Describe(control)}: wanted " +
                            $"{desiredWidth:0}x{desiredHeight:0}, got " +
                            $"{control.Bounds.Width:0}x{control.Bounds.Height:0}"));
                    }

                    continue;
                }

                // Cut off by an ancestor that clips. A scroller doing it only
                // means the content is off-screen; anything else means it
                // cannot be reached at all, which is a defect.
                if (ClippedBy(control) is not { } clipper)
                    continue;

                bool scrollable = clipper.Ancestor is ScrollContentPresenter ||
                    clipper.Ancestor.GetVisualAncestors().OfType<ScrollViewer>().Any();

                if (scrollable)
                    overflowing++;
                else
                    clipped++;

                if (details.Count < 12)
                {
                    details.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{(scrollable ? "scrolled out of" : "cut off by")} " +
                        $"{clipper.Ancestor.GetType().Name}: {Describe(control)} " +
                        $"ends at {clipper.Edge:0} of {clipper.Limit:0}"));
                }
            }

            _ = windowWidth;

            return new LayoutAudit(clipped, overflowing, details);
        }

        // The first clipping ancestor that the control does not fit inside.
        // Avalonia clips at the visual that sets ClipToBounds, so that is the
        // rectangle a user's eye actually stops at.
        private static (Visual Ancestor, double Edge, double Limit)? ClippedBy(Control control)
        {
            foreach (Visual ancestor in control.GetVisualAncestors())
            {
                if (ancestor is not Control { ClipToBounds: true } clipper)
                    continue;

                if (control.TranslatePoint(default, clipper) is not { } origin)
                    return null;

                double right = origin.X + control.Bounds.Width;
                double bottom = origin.Y + control.Bounds.Height;

                if (right > clipper.Bounds.Width + 0.5)
                    return (clipper, right, clipper.Bounds.Width);

                if (bottom > clipper.Bounds.Height + 0.5)
                    return (clipper, bottom, clipper.Bounds.Height);

                // The nearest clip decides; an outer one cannot cut what the
                // inner one already contained.
                return null;
            }

            return null;
        }

        private static string Describe(Control control) => control switch
        {
            TextBlock { Text: { Length: > 0 } text } =>
                $"text \"{Trim(text)}\"",
            Button { Content: string label } => $"button \"{Trim(label)}\"",
            _ => control.GetType().Name,
        };

        private static string Trim(string value) =>
            value.Length <= 48 ? value : value[..45] + "...";
    }
}
