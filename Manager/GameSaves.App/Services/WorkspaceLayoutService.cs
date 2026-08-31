using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Owns every page's live workspace arrangement and its persistence.
    ///
    /// The service is the only thing that writes panel layout to
    /// <see cref="IUiSettingsStore"/>, and it writes presentation state only —
    /// panel keys, regions, orders, weights, and window rectangles. No path,
    /// credential, profile, run identifier, or any other operational value can
    /// reach a layout file, because none of those exist in the record types it
    /// serializes.
    ///
    /// Pages are created lazily and cached, so the Dashboard's arrangement is
    /// the same object whether it is reached from the rail, from a detached
    /// window, or from the Settings section-visibility list.
    /// </summary>
    public sealed class WorkspaceLayoutService
    {
        private readonly IUiSettingsStore _store;
        private readonly Dictionary<string, WorkspacePage> _pages =
            new(StringComparer.Ordinal);

        public WorkspaceLayoutService(IUiSettingsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>The live layout for one page; created from the saved state on first use.</summary>
        public WorkspacePage Page(string pageKey)
        {
            if (_pages.TryGetValue(pageKey, out WorkspacePage? page))
                return page;

            page = new WorkspacePage(pageKey, this, LoadPage(pageKey));
            _pages[pageKey] = page;
            return page;
        }

        /// <summary>
        /// Every page's current arrangement, for a named layout snapshot. Pages
        /// that still sit at their defaults are included, so applying the
        /// snapshot later restores those defaults rather than leaving whatever
        /// the user had drifted to.
        /// </summary>
        public IReadOnlyList<UiPageLayout> Capture() =>
            UiPageLayout.NormalizeList(
                WorkspaceLayoutCatalog.Pages
                    .Select(pageKey => Page(pageKey).ToLayout()));

        /// <summary>
        /// Applies a named layout's page arrangements. A page the layout does
        /// not mention is reset to its default rather than left alone, so
        /// applying a layout always produces the same workspace.
        /// </summary>
        public void Apply(IReadOnlyList<UiPageLayout> pages)
        {
            var byPage = new Dictionary<string, UiPageLayout>(StringComparer.Ordinal);

            foreach (UiPageLayout page in pages)
            {
                if (page.Normalized() is { } normalized)
                    byPage[normalized.PageKey] = normalized;
            }

            foreach (string pageKey in WorkspaceLayoutCatalog.Pages)
            {
                byPage.TryGetValue(pageKey, out UiPageLayout? layout);
                Page(pageKey).Replace(layout);
            }

            Persist();
        }

        /// <summary>Restores every page's immutable default arrangement.</summary>
        public void ResetAll()
        {
            foreach (string pageKey in WorkspaceLayoutCatalog.Pages)
                Page(pageKey).Replace(null);

            Persist();
        }

        private UiPageLayout? LoadPage(string pageKey)
        {
            foreach (UiPageLayout page in _store.Load().WorkspacePages)
            {
                if (string.Equals(page.PageKey, pageKey, StringComparison.Ordinal))
                    return page;
            }

            return null;
        }

        // Written as a full settings record, the same read-modify-write the
        // rest of the settings surface uses, so the stored and live states
        // cannot drift apart.
        internal void Persist()
        {
            IReadOnlyList<UiPageLayout> pages = UiPageLayout.NormalizeList(
                _pages.Values.Select(page => page.ToLayout()));

            AppUiSettings settings = _store.Load();
            _store.Save(settings with { WorkspacePages = pages });
        }
    }

    /// <summary>
    /// One page's live arrangement. Every mutation resolves against
    /// <see cref="WorkspaceLayoutCatalog"/> first, so an intent that the
    /// catalog forbids is a no-op rather than a corrupt layout, and the result
    /// is always a complete, densely ordered set of the page's panels.
    /// </summary>
    public sealed class WorkspacePage : IWorkspaceLayoutPage
    {
        private readonly WorkspaceLayoutService _service;
        private readonly Dictionary<string, double> _regionSizes =
            new(StringComparer.Ordinal);

        private IReadOnlyList<UiPanelPlacement> _placements;

        internal WorkspacePage(
            string pageKey,
            WorkspaceLayoutService service,
            UiPageLayout? saved)
        {
            PageKey = pageKey;
            _service = service;
            _placements = WorkspaceLayoutCatalog.Resolve(pageKey, saved);
            LoadRegionSizes(saved);
        }

        public string PageKey { get; }

        public IReadOnlyList<UiPanelPlacement> Placements => _placements;

        public event EventHandler? PlacementsChanged;

        public double RegionSize(string region) =>
            _regionSizes.TryGetValue(region, out double size)
                ? size
                : DefaultRegionSize(region);

        // The centre is the reference the other regions are measured against,
        // so it always weighs 1 and only the sides are resizable.
        internal static double DefaultRegionSize(string region) => region switch
        {
            // Two parts rail to three parts centre — the same 40/60 split the
            // pages that had a side pane already used.
            UiPanelRegion.Left or UiPanelRegion.Right => 2.0 / 3.0,
            UiPanelRegion.Top or UiPanelRegion.Bottom => 0.40,
            _ => UiPanelPlacement.DefaultSize,
        };

        public void MovePanel(string panelKey, string region, int order)
        {
            if (!UiPanelRegion.IsRegion(region) || region == UiPanelRegion.Float)
                return;

            if (Find(panelKey) is not { } current)
                return;

            if (current.Region == region && current.Order == order)
                return;

            // Ordering is expressed by sorting, then densified: giving the
            // moved panel a half-step places it exactly between its new
            // neighbours whatever their current numbering is.
            var reordered = _placements
                .Select(placement => ReferenceEquals(placement, current)
                    ? placement with { Region = region, Order = order }
                    : placement)
                .OrderBy(placement => placement.Order)
                .ToArray();

            Commit(WorkspaceLayoutCatalog.Renumber(reordered));
        }

        public void NudgePanel(string panelKey, int offset)
        {
            if (Find(panelKey) is not { } current || offset == 0)
                return;

            // A half-step past the neighbour on the requested side, so the
            // dense renumber lands the panel on the other side of it.
            MovePanel(panelKey, current.Region, current.Order + (offset > 0 ? 1 : -2));
        }

        public void SetCollapsed(string panelKey, bool collapsed) =>
            Update(panelKey, placement => placement with { Collapsed = collapsed });

        public void SetHidden(string panelKey, bool hidden) =>
            Update(panelKey, placement => placement with { Hidden = hidden });

        public void FloatPanel(
            string panelKey, double left, double top, double width, double height)
        {
            if (WorkspaceLayoutCatalog.Find(PageKey, panelKey) is not { CanFloat: true })
                return;

            Update(panelKey, placement => placement with
            {
                // Remember where it was docked so closing its window puts it
                // back there, not wherever the catalog would have put it.
                DockedRegion = placement.IsFloating
                    ? placement.DockedRegion
                    : placement.Region,
                Region = UiPanelRegion.Float,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
            });
        }

        public void DockPanel(string panelKey)
        {
            if (Find(panelKey) is not { IsFloating: true })
                return;

            // Back to where the user had it. The catalog default is only the
            // fallback, for a panel that has never been docked anywhere else.
            string fallback = WorkspaceLayoutCatalog.Find(PageKey, panelKey)?.Region
                ?? UiPanelRegion.Center;

            Update(panelKey, placement => placement with
            {
                Region = placement.DockedRegion is { } home &&
                    UiPanelRegion.IsRegion(home) &&
                    home != UiPanelRegion.Float
                        ? home
                        : fallback,
                DockedRegion = null,
            });
        }

        public void ResizePanel(string panelKey, double size) =>
            Update(panelKey, placement => placement with
            {
                Size = UiPanelPlacement.NormalizeSize(size),
            });

        public void ResizeRegion(string region, double size)
        {
            if (!UiPanelRegion.IsRegion(region) || region == UiPanelRegion.Float)
                return;

            double normalized = UiRegionSize.NormalizeWeight(size);

            if (_regionSizes.TryGetValue(region, out double current) &&
                Math.Abs(current - normalized) < 0.001)
            {
                return;
            }

            _regionSizes[region] = normalized;
            Commit(_placements);
        }

        public void ResetPage()
        {
            Replace(null);
            _service.Persist();
        }

        /// <summary>Replaces this page's whole arrangement without persisting.</summary>
        internal void Replace(UiPageLayout? saved)
        {
            _placements = WorkspaceLayoutCatalog.Resolve(PageKey, saved);
            LoadRegionSizes(saved);
            PlacementsChanged?.Invoke(this, EventArgs.Empty);
        }

        internal UiPageLayout ToLayout() => new(
            PageKey,
            _placements,
            _regionSizes
                .Select(pair => new UiRegionSize(pair.Key, pair.Value))
                .ToArray());

        private void LoadRegionSizes(UiPageLayout? saved)
        {
            _regionSizes.Clear();

            if (saved is null)
                return;

            // Re-clamped on the way in, not trusted. A layout can arrive from
            // the settings file, an import, or a record built by another build,
            // and only this path is common to all three — so an unusable weight
            // is corrected here however it was produced.
            foreach (UiRegionSize region in saved.Regions)
                _regionSizes[region.Region] = UiRegionSize.NormalizeWeight(region.Size);
        }

        private UiPanelPlacement? Find(string panelKey) =>
            _placements.FirstOrDefault(placement =>
                string.Equals(placement.Key, panelKey, StringComparison.Ordinal));

        private void Update(
            string panelKey,
            Func<UiPanelPlacement, UiPanelPlacement> change)
        {
            if (Find(panelKey) is not { } current)
                return;

            UiPanelPlacement updated = change(current);

            if (updated == current)
                return;

            Commit(_placements
                .Select(placement => ReferenceEquals(placement, current) ? updated : placement)
                .ToArray());
        }

        // Every accepted change goes through the catalog once more, so the
        // constraints that protect a page from a bad saved file protect it
        // from a bad intent too.
        private void Commit(IReadOnlyList<UiPanelPlacement> placements)
        {
            _placements = WorkspaceLayoutCatalog.Resolve(
                PageKey,
                UiPageLayout.TryCreate(PageKey, placements));

            PlacementsChanged?.Invoke(this, EventArgs.Empty);
            _service.Persist();
        }
    }
}
