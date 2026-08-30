using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Where a workspace panel sits. Panels dock into one of five regions of
    /// a page, or float in their own window. The five docked regions form the
    /// same cross an IDE tool-window layout uses: left and right run the full
    /// height, top and bottom sit between them, and the centre takes the rest.
    /// </summary>
    public static class UiPanelRegion
    {
        public const string Left = "left";
        public const string Top = "top";
        public const string Right = "right";
        public const string Bottom = "bottom";
        public const string Center = "center";
        public const string Float = "float";

        public static bool IsRegion(string? value) => value is
            Left or Top or Right or Bottom or Center or Float;

        /// <summary>Docked regions only, in the order a panel menu lists them.</summary>
        public static IReadOnlyList<string> DockedRegions { get; } = new[]
        {
            Left, Top, Center, Bottom, Right,
        };

        /// <summary>The human-readable name a panel menu shows for a region.</summary>
        public static string DisplayName(string region) => region switch
        {
            Left => "Left",
            Top => "Top",
            Right => "Right",
            Bottom => "Bottom",
            Center => "Centre",
            Float => "Floating",
            _ => region,
        };
    }

    /// <summary>
    /// One panel's placement inside a page's workspace layout. Everything here
    /// is presentation state: a stable panel key, where it sits, how much room
    /// it takes, and whether it is collapsed, hidden, or floating. No path,
    /// credential, identifier, or operational value is ever stored.
    ///
    /// <see cref="Size"/> is a star weight within the panel's region, not a
    /// pixel extent, so a saved layout restores the same proportions on a
    /// different display. Floating bounds are DIPs on the virtual desktop and
    /// are re-clamped onto the current screens when the layout is applied.
    /// </summary>
    public sealed record UiPanelPlacement(
        string Key,
        string Region,
        int Order,
        double Size,
        bool Collapsed,
        bool Hidden,
        double Left,
        double Top,
        double Width,
        double Height)
    {
        public const double MinSize = 0.1;
        public const double MaxSize = 10.0;
        public const double DefaultSize = 1.0;

        public const int MinOrder = 0;
        public const int MaxOrder = 63;

        // Floating panels reuse the detached-window envelope, so one clamp
        // policy covers every floating surface the app can produce.
        public const double MinWindowExtent = UiDetachedWindowSettings.MinWindowExtent;
        public const double MaxWindowExtent = UiDetachedWindowSettings.MaxWindowExtent;
        public const double MinPosition = UiDetachedWindowSettings.MinPosition;
        public const double MaxPosition = UiDetachedWindowSettings.MaxPosition;

        /// <summary>The default floating size for a panel that has never floated.</summary>
        public const double DefaultFloatExtent = 480;

        /// <summary>
        /// The docked region this panel came from, remembered while it floats
        /// so closing its window returns it where the user had it rather than
        /// to the catalog's default. An init property so every existing
        /// construction keeps compiling; null means "wherever the catalog puts
        /// it", which is also what a layout saved before this existed says.
        /// </summary>
        public string? DockedRegion { get; init; }

        public bool IsFloating => Region == UiPanelRegion.Float;

        /// <summary>
        /// Null when the entry cannot be salvaged (no key, or a region this
        /// version does not know); otherwise a copy clamped into the sane
        /// ranges. This is the single normalization path for saved, imported,
        /// and live-captured placements alike, so a corrupt file and a hostile
        /// import are treated identically.
        /// </summary>
        public static UiPanelPlacement? TryCreate(
            string? key,
            string? region,
            int order,
            double size,
            bool collapsed,
            bool hidden,
            double left,
            double top,
            double width,
            double height)
        {
            if (string.IsNullOrWhiteSpace(key) || !UiPanelRegion.IsRegion(region))
                return null;

            return new UiPanelPlacement(
                key!,
                region!,
                Math.Clamp(order, MinOrder, MaxOrder),
                NormalizeSize(size),
                collapsed,
                hidden,
                NormalizePosition(left),
                NormalizePosition(top),
                NormalizeExtent(width),
                NormalizeExtent(height));
        }

        public static double NormalizeSize(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, MinSize, MaxSize) : DefaultSize;

        private static double NormalizePosition(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, MinPosition, MaxPosition) : 0.0;

        private static double NormalizeExtent(double value) =>
            double.IsFinite(value)
                ? Math.Clamp(value, MinWindowExtent, MaxWindowExtent)
                : DefaultFloatExtent;
    }

    /// <summary>
    /// How much of a page one docked region takes, as a star weight against
    /// the other regions. Stored per region rather than per panel because
    /// dragging the splitter between the left rail of panels and the centre
    /// resizes the region, not any single panel inside it.
    /// </summary>
    public sealed record UiRegionSize(string Region, double Size)
    {
        public static UiRegionSize? TryCreate(string? region, double size)
        {
            // Floating panels have no region extent to remember.
            if (!UiPanelRegion.IsRegion(region) || region == UiPanelRegion.Float)
                return null;

            return new UiRegionSize(region!, UiPanelPlacement.NormalizeSize(size));
        }

        public static IReadOnlyList<UiRegionSize> NormalizeList(
            IEnumerable<UiRegionSize> regions)
        {
            var normalized = new List<UiRegionSize>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiRegionSize region in regions)
            {
                if (TryCreate(region.Region, region.Size) is not { } candidate)
                    continue;

                if (!seen.Add(candidate.Region))
                    continue;

                normalized.Add(candidate);
            }

            return normalized;
        }
    }

    /// <summary>
    /// One page's panel arrangement. <see cref="PageKey"/> is one of the
    /// stable rail tab keys, so a page layout and a rail tab always agree on
    /// what "the Sync page" means.
    /// </summary>
    public sealed record UiPageLayout(
        string PageKey,
        IReadOnlyList<UiPanelPlacement> Panels,
        IReadOnlyList<UiRegionSize> Regions)
    {
        public const int MaxPanelsPerPage = 32;

        /// <summary>
        /// Null when the page key is not a known rail tab; otherwise the
        /// layout with placements deduplicated by key (first wins) and capped.
        /// </summary>
        public static UiPageLayout? TryCreate(
            string? pageKey,
            IEnumerable<UiPanelPlacement> panels,
            IEnumerable<UiRegionSize>? regions = null)
        {
            if (pageKey is null || !UiRailLayoutSettings.IsTabKey(pageKey))
                return null;

            var entries = new List<UiPanelPlacement>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiPanelPlacement panel in panels)
            {
                if (!seen.Add(panel.Key))
                    continue;

                if (entries.Count >= MaxPanelsPerPage)
                    break;

                entries.Add(panel);
            }

            return new UiPageLayout(
                pageKey,
                entries,
                UiRegionSize.NormalizeList(regions ?? Array.Empty<UiRegionSize>()));
        }

        public UiPageLayout? Normalized() => TryCreate(PageKey, Panels, Regions);

        /// <summary>The stored weight for a region, or 1.0 when unset.</summary>
        public double RegionSize(string region)
        {
            foreach (UiRegionSize entry in Regions)
            {
                if (string.Equals(entry.Region, region, StringComparison.Ordinal))
                    return entry.Size;
            }

            return UiPanelPlacement.DefaultSize;
        }

        /// <summary>
        /// Normalizes a whole set of page layouts: per-page normalization,
        /// unique page keys (first wins), and empty pages dropped. Garbage is
        /// discarded rather than defaulted, so one bad page cannot cost the
        /// user the others.
        /// </summary>
        public static IReadOnlyList<UiPageLayout> NormalizeList(
            IEnumerable<UiPageLayout> pages)
        {
            var normalized = new List<UiPageLayout>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiPageLayout page in pages)
            {
                if (page.Normalized() is not { } candidate)
                    continue;

                if (candidate.Panels.Count == 0)
                    continue;

                if (!seen.Add(candidate.PageKey))
                    continue;

                normalized.Add(candidate);
            }

            return normalized;
        }
    }
}
