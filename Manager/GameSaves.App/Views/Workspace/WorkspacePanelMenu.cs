using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using GameSaves.App.Services;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// The compact layout menu on every panel header. It is the keyboard and
    /// screen-reader route to everything dragging can do — move to a region,
    /// reorder inside one, collapse, float, hide — plus the per-page reset.
    /// Drag is the fast path; this is the complete one, so no layout action is
    /// reachable only by pointer.
    /// </summary>
    internal static class WorkspacePanelMenu
    {
        public static MenuFlyout Build(
            IWorkspaceLayoutPage layout,
            WorkspacePanel panel,
            WorkspacePanelDefinition? definition)
        {
            UiPanelPlacement? placement = layout.Placements.FirstOrDefault(entry =>
                string.Equals(entry.Key, panel.PanelKey, StringComparison.Ordinal));

            var items = new List<Control>();

            foreach (string region in UiPanelRegion.DockedRegions)
            {
                string name = UiPanelRegion.DisplayName(region);
                var item = new MenuItem
                {
                    Header = $"Move to {name.ToLowerInvariant()}",
                    // The panel's current region is the one move that would do
                    // nothing, so it is disabled rather than silently inert.
                    IsEnabled = placement is not null && placement.Region != region,
                };

                string target = region;
                item.Click += (_, _) => layout.MovePanel(panel.PanelKey, target, int.MaxValue);
                AutomationProperties.SetName(item, $"Move {panel.Title} to the {name.ToLowerInvariant()}");
                items.Add(item);
            }

            items.Add(new Separator());

            int index = placement?.Order ?? 0;
            int lastInRegion = placement is null
                ? 0
                : layout.Placements.Count(entry =>
                    string.Equals(entry.Region, placement.Region, StringComparison.Ordinal)) - 1;

            var moveEarlier = new MenuItem
            {
                Header = "Move earlier",
                IsEnabled = placement is not null && !placement.IsFloating && index > 0,
            };
            moveEarlier.Click += (_, _) => layout.NudgePanel(panel.PanelKey, -1);
            AutomationProperties.SetName(moveEarlier, $"Move {panel.Title} earlier");
            items.Add(moveEarlier);

            var moveLater = new MenuItem
            {
                Header = "Move later",
                IsEnabled = placement is not null && !placement.IsFloating && index < lastInRegion,
            };
            moveLater.Click += (_, _) => layout.NudgePanel(panel.PanelKey, 1);
            AutomationProperties.SetName(moveLater, $"Move {panel.Title} later");
            items.Add(moveLater);

            items.Add(new Separator());

            if (panel.CanCollapse)
            {
                bool collapsed = placement?.Collapsed ?? false;
                var collapse = new MenuItem { Header = collapsed ? "Expand" : "Collapse" };
                collapse.Click += (_, _) => layout.SetCollapsed(panel.PanelKey, !collapsed);
                AutomationProperties.SetName(
                    collapse, $"{(collapsed ? "Expand" : "Collapse")} {panel.Title}");
                items.Add(collapse);
            }

            if (panel.CanFloat && definition?.CanFloat != false)
            {
                bool floating = placement?.IsFloating ?? false;
                var float_ = new MenuItem { Header = floating ? "Dock back" : "Float in its own window" };

                if (floating)
                    float_.Click += (_, _) => layout.DockPanel(panel.PanelKey);
                else
                    float_.Click += (_, _) => FloatAt(layout, panel);

                AutomationProperties.SetName(
                    float_, floating ? $"Dock {panel.Title} back" : $"Float {panel.Title}");
                items.Add(float_);
            }

            if (panel.CanHide && definition?.CanHide != false)
            {
                var hide = new MenuItem { Header = "Hide this section" };
                hide.Click += (_, _) => layout.SetHidden(panel.PanelKey, true);
                AutomationProperties.SetName(hide, $"Hide {panel.Title}");
                items.Add(hide);
            }

            items.Add(new Separator());

            var reset = new MenuItem { Header = "Reset this page's layout" };
            reset.Click += (_, _) => layout.ResetPage();
            AutomationProperties.SetName(reset, "Reset this page's layout to the default");
            items.Add(reset);

            return new MenuFlyout { ItemsSource = items };
        }

        // A panel floated from the menu opens beside where it sat, at the size
        // it last floated at. The window itself clamps onto a visible screen.
        private static void FloatAt(IWorkspaceLayoutPage layout, WorkspacePanel panel)
        {
            UiPanelPlacement? placement = layout.Placements.FirstOrDefault(entry =>
                string.Equals(entry.Key, panel.PanelKey, StringComparison.Ordinal));

            double width = placement is { Width: > 0 }
                ? placement.Width
                : Math.Max(panel.MinPanelWidth, UiPanelPlacement.DefaultFloatExtent);
            double height = placement is { Height: > 0 }
                ? placement.Height
                : Math.Max(panel.MinPanelHeight, UiPanelPlacement.DefaultFloatExtent);

            layout.FloatPanel(
                panel.PanelKey,
                placement?.Left ?? 0,
                placement?.Top ?? 0,
                width,
                height);
        }
    }
}
