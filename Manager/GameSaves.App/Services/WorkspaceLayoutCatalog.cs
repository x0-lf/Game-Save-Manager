using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Services
{
    /// <summary>
    /// One panel's immutable default placement. The catalog of these
    /// definitions *is* the default layout: it is code, not data, so it can
    /// never drift from what the views actually declare and can never be
    /// corrupted by a bad settings file. Everything a user changes is an
    /// overlay on top of it, and "reset to default" is simply dropping the
    /// overlay.
    /// </summary>
    public sealed record WorkspacePanelDefinition(
        string Key,
        string PageKey,
        string Title,
        string Region,
        int Order,
        double Size,
        bool CanHide = true,
        bool CanFloat = true,
        bool CanCollapse = true)
    {
        /// <summary>This panel's placement in the default layout.</summary>
        public UiPanelPlacement ToPlacement() => new(
            Key,
            Region,
            Order,
            Size,
            Collapsed: false,
            Hidden: false,
            Left: 0,
            Top: 0,
            Width: UiPanelPlacement.DefaultFloatExtent,
            Height: UiPanelPlacement.DefaultFloatExtent);
    }

    /// <summary>
    /// The immutable default workspace layout for every page, and the
    /// resolution rule that merges a user's saved arrangement onto it.
    ///
    /// Resolution is deliberately forgiving in one direction only: unknown
    /// panel keys are dropped and missing ones fall back to their default, so
    /// a layout saved by an older or newer build always yields a complete,
    /// usable page rather than an empty one. A panel the catalog forbids from
    /// hiding, floating, or collapsing can never be put into that state by a
    /// saved file, however the file was produced.
    /// </summary>
    public static class WorkspaceLayoutCatalog
    {
        private static readonly IReadOnlyList<WorkspacePanelDefinition> Definitions =
            BuildDefinitions();

        private static readonly IReadOnlyDictionary<string, IReadOnlyList<WorkspacePanelDefinition>>
            ByPage = Definitions
                .GroupBy(definition => definition.PageKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<WorkspacePanelDefinition>)group
                        .OrderBy(definition => RegionRank(definition.Region))
                        .ThenBy(definition => definition.Order)
                        .ToArray(),
                    StringComparer.Ordinal);

        /// <summary>Every panel the app declares, in page then region order.</summary>
        public static IReadOnlyList<WorkspacePanelDefinition> All => Definitions;

        /// <summary>The pages that have a workspace layout, in rail order.</summary>
        public static IReadOnlyList<string> Pages { get; } =
            UiRailLayoutSettings.CanonicalTabOrder
                .Where(ByPage.ContainsKey)
                .ToArray();

        public static IReadOnlyList<WorkspacePanelDefinition> PanelsFor(string pageKey) =>
            ByPage.TryGetValue(pageKey, out IReadOnlyList<WorkspacePanelDefinition>? panels)
                ? panels
                : Array.Empty<WorkspacePanelDefinition>();

        public static WorkspacePanelDefinition? Find(string pageKey, string panelKey) =>
            PanelsFor(pageKey).FirstOrDefault(definition =>
                string.Equals(definition.Key, panelKey, StringComparison.Ordinal));

        /// <summary>This page's default arrangement, with no user changes applied.</summary>
        public static IReadOnlyList<UiPanelPlacement> DefaultPlacements(string pageKey) =>
            PanelsFor(pageKey)
                .Select(definition => definition.ToPlacement())
                .ToArray();

        /// <summary>
        /// The effective arrangement for a page: the catalog default with the
        /// saved layout overlaid. Unknown, malformed, or forbidden saved state
        /// is discarded entry by entry rather than failing the whole page, and
        /// the result is always the complete set of the page's panels with
        /// dense 0..n-1 ordering inside each region.
        /// </summary>
        public static IReadOnlyList<UiPanelPlacement> Resolve(
            string pageKey,
            UiPageLayout? saved)
        {
            IReadOnlyList<WorkspacePanelDefinition> definitions = PanelsFor(pageKey);

            if (definitions.Count == 0)
                return Array.Empty<UiPanelPlacement>();

            var savedByKey = new Dictionary<string, UiPanelPlacement>(StringComparer.Ordinal);

            if (saved is not null)
            {
                foreach (UiPanelPlacement placement in saved.Panels)
                {
                    // An entry for a panel this build does not know about is a
                    // layout from a different version; drop it silently.
                    if (Find(pageKey, placement.Key) is null)
                        continue;

                    savedByKey[placement.Key] = placement;
                }
            }

            var resolved = new List<UiPanelPlacement>(definitions.Count);

            foreach (WorkspacePanelDefinition definition in definitions)
            {
                UiPanelPlacement placement =
                    savedByKey.TryGetValue(definition.Key, out UiPanelPlacement? stored)
                        ? Constrain(definition, stored)
                        : definition.ToPlacement();

                resolved.Add(placement);
            }

            return Renumber(resolved);
        }

        // The catalog, not the file, decides what a panel is allowed to do. A
        // saved layout that hides a panel the catalog pins is treated as if it
        // had not asked.
        private static UiPanelPlacement Constrain(
            WorkspacePanelDefinition definition,
            UiPanelPlacement placement)
        {
            // Resolve is the last line of defence, so it re-validates rather
            // than trusting that the placement came through the store's parser.
            // A layout can also arrive from an import or from another build's
            // in-memory record, and an unknown region must degrade to the
            // panel's home rather than to a region nothing can render.
            string region = UiPanelRegion.IsRegion(placement.Region)
                ? placement.Region
                : definition.Region;

            if (region == UiPanelRegion.Float && !definition.CanFloat)
                region = definition.Region;

            return placement with
            {
                Region = region,
                Order = Math.Clamp(
                    placement.Order, UiPanelPlacement.MinOrder, UiPanelPlacement.MaxOrder),
                Size = UiPanelPlacement.NormalizeSize(placement.Size),
                Hidden = placement.Hidden && definition.CanHide,
                Collapsed = placement.Collapsed && definition.CanCollapse,
                DockedRegion = placement.DockedRegion is { } home &&
                    UiPanelRegion.IsRegion(home) &&
                    home != UiPanelRegion.Float
                        ? home
                        : null,
            };
        }

        /// <summary>
        /// Rewrites orders to a dense 0..n-1 sequence inside each region,
        /// preserving relative order. A corrupt file with duplicate or absurd
        /// order values therefore still produces one deterministic arrangement.
        /// </summary>
        internal static IReadOnlyList<UiPanelPlacement> Renumber(
            IReadOnlyList<UiPanelPlacement> placements)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);

            return placements
                .OrderBy(placement => RegionRank(placement.Region))
                .ThenBy(placement => placement.Order)
                .Select(placement =>
                {
                    counters.TryGetValue(placement.Region, out int next);
                    counters[placement.Region] = next + 1;
                    return placement with { Order = next };
                })
                .ToArray();
        }

        private static int RegionRank(string region) => region switch
        {
            UiPanelRegion.Left => 0,
            UiPanelRegion.Top => 1,
            UiPanelRegion.Center => 2,
            UiPanelRegion.Bottom => 3,
            UiPanelRegion.Right => 4,
            _ => 5,
        };

        // ---------------------------------------------------------------------
        // The default layout. Each page's entries reproduce the arrangement the
        // application shipped with, region by region and in the same order, so
        // a first run and a reset both render exactly what the views declared
        // before the workspace system existed.
        // ---------------------------------------------------------------------
        private static IReadOnlyList<WorkspacePanelDefinition> BuildDefinitions()
        {
            var definitions = new List<WorkspacePanelDefinition>();

            void Page(string pageKey, params (string Key, string Title, string Region, double Size, bool CanHide)[] panels)
            {
                var orders = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach ((string key, string title, string region, double size, bool canHide) in panels)
                {
                    orders.TryGetValue(region, out int order);
                    orders[region] = order + 1;

                    definitions.Add(new WorkspacePanelDefinition(
                        Key: $"{pageKey}.{key}",
                        PageKey: pageKey,
                        Title: title,
                        Region: region,
                        Order: order,
                        Size: size,
                        CanHide: canHide));
                }
            }

            const string C = UiPanelRegion.Center;
            const string L = UiPanelRegion.Left;
            const string T = UiPanelRegion.Top;

            // Dashboard: the page header, the two first-run sections, then the
            // six stat cards — all content-height in one flowing centre column,
            // in exactly the order the page shipped in.
            Page(UiRailLayoutSettings.TabDashboard,
                ("intro", "Dashboard", C, 1.0, false),
                ("steamMissing", "Steam was not found on this computer", C, 1.0, false),
                ("getSetUp", "Get set up", C, 1.0, true),
                ("libraries", "Libraries", C, 1.0, true),
                ("installedGames", "Installed games", C, 1.0, true),
                ("steamProfiles", "Steam profiles", C, 1.0, true),
                ("approvedMappings", "Approved mappings", C, 1.0, true),
                ("pendingMappings", "Pending mappings", C, 1.0, true),
                ("needsAttention", "Needs attention", C, 1.0, true));

            // Installed games: one column — header, the conditional banner,
            // then the table, which is the panel that absorbs the slack.
            Page(UiRailLayoutSettings.TabInstalledGames,
                ("header", "Installed games", C, 1.0, false),
                ("steamMissing", "Steam was not found on this computer", C, 1.0, false),
                ("table", "Games", C, 1.0, false));

            // Profiles: header, the conditional banner, then the source and
            // target cards side by side (both declare a preferred width, so the
            // run flows), with the detected-profiles list filling the rest —
            // exactly the arrangement the page shipped with.
            Page(UiRailLayoutSettings.TabProfiles,
                ("header", "Profiles", C, 1.0, false),
                ("steamMissing", "Steam was not found on this computer", C, 1.0, false),
                ("sourceProfile", "Source profile", C, 1.0, true),
                ("targetProfile", "Target profile", C, 1.0, true),
                ("detected", "Detected profiles", C, 1.0, true));

            // Transfer profiles: inputs and summary on the left, warnings over
            // results in the centre.
            Page(UiRailLayoutSettings.TabTransferPreview,
                ("header", "Transfer profiles", T, 1.0, false),
                ("noProfiles", "No profiles found", T, 1.0, false),
                ("inputs", "Transfer inputs", T, 1.0, true),
                ("summary", "Summary", L, 1.0, true),
                ("warnings", "Warnings", C, 1.0, true),
                ("results", "Preview results", C, 1.0, true));

            // Manual backup: selectors on the left; summary, preview, warnings
            // and results stacked in the centre.
            Page(UiRailLayoutSettings.TabManualBackup,
                ("header", "Manual backup", T, 1.0, false),
                ("noProfiles", "No profiles found", T, 1.0, false),
                ("selectors", "Backup options", T, 1.0, true),
                ("summary", "Summary", L, 1.0, true),
                ("preview", "What will be backed up", C, 1.0, true),
                ("warnings", "Warnings", C, 1.0, true),
                ("results", "Execution results", C, 1.0, true));

            // Backups: the run list on the left, everything about the selected
            // run in the centre.
            Page(UiRailLayoutSettings.TabBackups,
                ("header", "Backups", T, 1.0, false),
                ("runs", "Backup runs", L, 1.0, false),
                ("restore", "Restore", C, 1.0, true),
                ("files", "Files in this backup run", C, 1.0, true),
                ("restoreResults", "Restore results", C, 1.0, true),
                ("archive", "Archive (ZIP)", C, 1.0, true),
                ("cleanup", "Cleanup", C, 1.0, true));

            // Sync: one column, in the order the run is performed.
            Page(UiRailLayoutSettings.TabSync,
                ("header", "Sync", C, 1.0, false),
                ("remoteProfile", "Remote profile", C, 1.0, true),
                ("target", "Sync target & connection", C, 1.0, true),
                ("plan", "Sync plan", C, 1.0, true),
                ("warnings", "Warnings", C, 1.0, true),
                ("results", "Execution results", C, 1.0, true),
                ("history", "Sync history", C, 1.0, true));

            // History: the run list on the left, that run's files in the centre.
            Page(UiRailLayoutSettings.TabHistory,
                ("header", "History", T, 1.0, false),
                ("runs", "Executed runs", L, 1.0, false),
                ("files", "Run files", C, 1.0, true));

            return definitions;
        }
    }
}
