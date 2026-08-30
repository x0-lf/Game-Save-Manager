using System;
using System.Collections.Generic;

namespace GameSaves.App.Services
{
    /// <summary>
    /// The seam between a page's workspace surface (a view) and the state that
    /// owns its arrangement (a view model). Every layout action a user can take
    /// arrives here as an intent; the implementation validates it against the
    /// catalog, persists it, and raises <see cref="PlacementsChanged"/>. The
    /// surface never decides policy and never touches the settings store, which
    /// keeps the repository's layering rule intact.
    /// </summary>
    public interface IWorkspaceLayoutPage
    {
        /// <summary>One of the stable rail tab keys.</summary>
        string PageKey { get; }

        /// <summary>
        /// The effective placement of every panel the catalog declares for
        /// this page, defaults overlaid with the user's saved changes, in
        /// region/order sequence. Never empty for a known page.
        /// </summary>
        IReadOnlyList<UiPanelPlacement> Placements { get; }

        /// <summary>The stored star weight for a docked region.</summary>
        double RegionSize(string region);

        /// <summary>Raised after any accepted change; the surface rebuilds.</summary>
        event EventHandler? PlacementsChanged;

        /// <summary>
        /// Docks a panel into a region at an insertion index. Orders of the
        /// other panels in both the source and target regions are renumbered
        /// so the result is always a dense 0..n-1 sequence.
        /// </summary>
        void MovePanel(string panelKey, string region, int order);

        /// <summary>Moves a panel one slot earlier or later inside its region.</summary>
        void NudgePanel(string panelKey, int offset);

        void SetCollapsed(string panelKey, bool collapsed);

        void SetHidden(string panelKey, bool hidden);

        /// <summary>Floats a panel at the given desktop bounds (DIPs).</summary>
        void FloatPanel(string panelKey, double left, double top, double width, double height);

        /// <summary>Returns a floating panel to the region it last occupied.</summary>
        void DockPanel(string panelKey);

        /// <summary>Persists a splitter drag between panels inside one region.</summary>
        void ResizePanel(string panelKey, double size);

        /// <summary>Persists a splitter drag between a region and the centre.</summary>
        void ResizeRegion(string region, double size);

        /// <summary>Restores this page's immutable default arrangement.</summary>
        void ResetPage();
    }
}
