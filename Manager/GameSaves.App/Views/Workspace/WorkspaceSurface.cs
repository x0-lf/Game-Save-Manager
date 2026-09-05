using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Reactive;
using GameSaves.App.Services;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// Hosts one page's <see cref="WorkspacePanel"/>s in the five docked
    /// regions of a workspace layout and owns the drag-to-dock interaction.
    ///
    /// The surface is a pure view: it renders whatever placement its
    /// <see cref="Layout"/> reports and sends every user gesture back as an
    /// intent. It never decides whether a move is legal, never persists, and
    /// never invents a placement — <see cref="IWorkspaceLayoutPage"/> owns all
    /// of that, so a page's arrangement is testable without a window.
    ///
    /// Panels are reparented, never rebuilt, so a rearrangement preserves
    /// scroll offsets, selections, and every other piece of live view state.
    /// </summary>
    public class WorkspaceSurface : Panel
    {
        /// <summary>The gutter between two panels that cannot be resized against each other.</summary>
        private const double PanelGutter = 10;

        /// <summary>The app's splitter idiom: an 8px grab strip with a 1px breathing margin.</summary>
        private const double SplitterExtent = 8;

        /// <summary>Pointer travel before a header press becomes a drag.</summary>
        private const double DragThreshold = 6;

        /// <summary>
        /// Below this width the side rails fold into the centre column. Matches
        /// the threshold the pages that used ResponsiveSplitGrid already had.
        /// </summary>
        private const double NarrowThreshold = 760;

        /// <summary>
        /// Room kept free on the right for the page scrollbar, which paints
        /// over the content rather than beside it.
        /// </summary>
        private const double ScrollBarGutter = 12;

        public static readonly StyledProperty<IWorkspaceLayoutPage?> LayoutProperty =
            AvaloniaProperty.Register<WorkspaceSurface, IWorkspaceLayoutPage?>(nameof(Layout));

        /// <summary>The most panels a flowing region puts on one row.</summary>
        public static readonly StyledProperty<int> FlowColumnsProperty =
            AvaloniaProperty.Register<WorkspaceSurface, int>(nameof(FlowColumns), 3);

        private readonly Grid _root = new();
        private readonly ScrollViewer _pageScroll = new();
        private readonly WorkspacePageHost _pageHost;
        private readonly Panel _parked = new();
        private readonly WorkspaceDockOverlay _overlay = new();
        private readonly Dictionary<string, WorkspacePanel> _panelsByKey = new(StringComparer.Ordinal);
        private readonly HashSet<WorkspacePanel> _menuWired = new();

        // The panels currently taking star space. Held so the page's minimum
        // height can be probed with them pinned to their declared minimum.
        private readonly List<WorkspacePanel> _fillPanels = new();
        /// <summary>How many times one rebuild may re-run itself before giving up.</summary>
        private const int RebuildPassLimit = 4;

        private bool _rebuilding;

        // Whether the side rails are currently folded into the centre column.
        private bool _folded;
        private readonly Dictionary<string, WorkspaceFloatingWindow> _floating =
            new(StringComparer.Ordinal);

        private IWorkspaceLayoutPage? _attachedLayout;
        private WorkspacePanel? _pressedPanel;
        private Point _pressOrigin;
        private bool _dragging;

        public WorkspaceSurface()
        {
            Panels = new AvaloniaList<WorkspacePanel>();
            Panels.CollectionChanged += OnPanelsChanged;

            _overlay.IsVisible = false;

            // One scroller for the whole page. The regions inside it do not
            // scroll themselves, which is what keeps a section from hiding
            // behind its own scrollbar three levels down.
            //
            // The page scrollbar is an overlay: it is painted over the content
            // rather than beside it, so without this gutter it covered the
            // right edge of every card — the card border, the last table
            // column, and the right-most button in a header row all lost a
            // few pixels to it on every page.
            _pageHost = new WorkspacePageHost(_fillPanels)
            {
                Child = _root,
                Margin = new Thickness(0, 0, ScrollBarGutter, 0),
            };
            _pageScroll.Content = _pageHost;
            _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _pageScroll.Classes.Add("pageScroll");

            // Sections whose own bindings currently hide them are parked here
            // rather than left with no parent. A detached panel inherits no
            // DataContext, so its IsVisible binding stops evaluating and it
            // reports itself visible again — which put an empty section back
            // into a region and let that region keep a full column of the page.
            _parked.IsVisible = false;

            Children.Add(_pageScroll);
            Children.Add(_parked);
            Children.Add(_overlay);
        }

        /// <summary>
        /// The page's panels, declared in XAML through explicit
        /// <c>&lt;WorkspaceSurface.Panels&gt;</c> element syntax. They are not
        /// direct children of the surface: the surface parents each one into
        /// the region its placement names.
        /// </summary>
        public AvaloniaList<WorkspacePanel> Panels { get; }

        public IWorkspaceLayoutPage? Layout
        {
            get => GetValue(LayoutProperty);
            set => SetValue(LayoutProperty, value);
        }

        public int FlowColumns
        {
            get => GetValue(FlowColumnsProperty);
            set => SetValue(FlowColumnsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == LayoutProperty)
                AttachLayout(change.GetNewValue<IWorkspaceLayoutPage?>());
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Rebuild();
        }


        protected override Size MeasureOverride(Size availableSize)
        {
            // The height the page should fill. Only a resize changes it, and a
            // resize always measures the surface, so reading it here is enough.
            _pageHost.ViewportHeight = availableSize.Height;
            return base.MeasureOverride(availableSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // The first rebuild happens before the surface has been measured, so
            // it sees a width of zero and cannot know whether the side rails
            // should be folded. Re-render only when the answer actually changes,
            // never on every resize.
            bool narrow = finalSize.Width > 0 && finalSize.Width < NarrowThreshold;

            if (narrow != _folded)
            {
                _folded = narrow;
                Rebuild();
            }

            return base.ArrangeOverride(finalSize);
        }

        /// <summary>
        /// Sits between the page scroller and the page, and decides how tall
        /// the page actually is.
        ///
        /// A ScrollViewer offers its content infinite height, and a Grid's star
        /// rows measured against infinity behave like Auto rows — so the page
        /// would be as tall as its content wanted, a table would grow to its
        /// full row count, and the page would never simply fill the window.
        ///
        /// So the page is measured twice. The first pass asks what it needs at
        /// minimum, with the filling panels pinned to their declared minimum so
        /// a long table cannot make the page grow without bound. The second
        /// gives it the greater of that minimum and the viewport: it fills the
        /// window while it fits, and grows past it only when the panels' own
        /// minimums no longer do — which is exactly when the scroller should
        /// have something to scroll, and what stops a section being arranged
        /// past the bottom of the window with no way to reach it.
        ///
        /// This lives in the content rather than in the surface because a
        /// ScrollViewer is a measure boundary: content that grows on its own —
        /// a card gaining a line when data loads — re-measures this host but
        /// never reaches the surface, and a minimum computed up there would go
        /// stale exactly when it mattered.
        /// </summary>
        private sealed class WorkspacePageHost : Decorator
        {
            private readonly IReadOnlyList<WorkspacePanel> _fills;
            private double _width;
            private double _height;

            public WorkspacePageHost(IReadOnlyList<WorkspacePanel> fills) =>
                _fills = fills;

            /// <summary>The height the page should fill, set by the surface.</summary>
            public double ViewportHeight { get; set; }

            protected override Size MeasureOverride(Size availableSize)
            {
                if (Child is not { } child)
                    return default;

                _width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
                _height = PageHeight(child, _width);

                child.Measure(new Size(_width, _height));
                return new Size(_width, _height);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                Child?.Arrange(new Rect(finalSize));

                if (Child is not { } child)
                    return finalSize;

                // Content can grow at an unchanged window size — a card gaining
                // lines when a preview fills in. A Grid clamps its own desired
                // height to whatever it was measured against, so that growth
                // never reaches this host as an invalidation: it is absorbed by
                // squeezing the filling pane onto its minimum, and once that is
                // spent the rest of the page is arranged past the bottom of the
                // window with nothing able to scroll to it. Re-checking the
                // minimum after the arrange is what turns the growth into a
                // re-measure. It settles in one extra pass, because the next
                // measure adopts the height this just computed.
                double target = PageHeight(child, _width);

                if (Math.Abs(target - _height) > 0.5)
                    InvalidateMeasure();
                else
                    child.Measure(new Size(_width, _height));

                return finalSize;
            }

            private double PageHeight(Control child, double width)
            {
                double viewport = double.IsFinite(ViewportHeight) && ViewportHeight > 0
                    ? ViewportHeight
                    : 0;

                double height = Math.Max(viewport, Minimum(child, width));

                return double.IsFinite(height) ? height : viewport;
            }

            // The height the page cannot go below. Filling panels are pinned to
            // their declared minimum for the measure: left free they would
            // report a table's full row count and the page would grow without
            // bound instead of ever simply filling the window.
            private double Minimum(Control child, double width)
            {
                // Snapshot: a rebuild reached from inside the measure would
                // otherwise repopulate the live list and strand the panels
                // pinned at their minimum for good.
                WorkspacePanel[] fills = _fills.ToArray();
                var restore = new double[fills.Length];

                for (int index = 0; index < fills.Length; index++)
                {
                    restore[index] = fills[index].MaxHeight;
                    fills[index].MaxHeight = fills[index].MinPanelHeight;
                }

                try
                {
                    child.Measure(new Size(width, double.PositiveInfinity));
                    return child.DesiredSize.Height;
                }
                finally
                {
                    for (int index = 0; index < fills.Length; index++)
                        fills[index].MaxHeight = restore[index];
                }
            }
        }

        private void AttachLayout(IWorkspaceLayoutPage? layout)
        {
            if (_attachedLayout is not null)
                _attachedLayout.PlacementsChanged -= OnPlacementsChanged;

            _attachedLayout = layout;

            if (_attachedLayout is not null)
                _attachedLayout.PlacementsChanged += OnPlacementsChanged;

            Rebuild();
        }

        private void OnPlacementsChanged(object? sender, EventArgs e) => Rebuild();

        private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (WorkspacePanel panel in Panels)
            {
                if (!_menuWired.Add(panel))
                    continue;

                panel.MenuRequested += OnPanelMenuRequested;

                // A section that shows or hides itself changes which regions
                // have content and how the remaining space is divided, so the
                // surface rebuilds rather than leaving a star-sized gap where
                // the section would have been.
                panel.GetObservable(IsVisibleProperty).Subscribe(
                    new AnonymousObserver<bool>(_ =>
                    {
                        if (!_rebuilding)
                            Rebuild();
                    }));

                // A panel's collapse state can also be driven from the page's
                // own view model — the Sync sections bind theirs two-way to
                // properties that predate this system. Writing it back to the
                // layout keeps one source of truth: without this the next
                // rebuild would reassign the stored value and silently undo the
                // user's click.
                WorkspacePanel captured = panel;
                panel.GetObservable(WorkspacePanel.IsCollapsedProperty).Subscribe(
                    new AnonymousObserver<bool>(collapsed =>
                        _attachedLayout?.SetCollapsed(captured.PanelKey, collapsed)));
            }

            Rebuild();
        }

        // Panel keys are read here, not when the collection changed: the XAML
        // loader may add a panel to the collection before applying its
        // PanelKey, and a panel indexed under an empty key would disappear from
        // its page with no error anywhere.
        private void IndexPanels()
        {
            _panelsByKey.Clear();

            foreach (WorkspacePanel panel in Panels)
            {
                if (!string.IsNullOrEmpty(panel.PanelKey))
                    _panelsByKey[panel.PanelKey] = panel;
            }
        }

        // What fraction of the surface each region currently occupies. A region
        // with nothing in it occupies none, so the guide never previews a strip
        // where no strip would appear.
        private IReadOnlyDictionary<string, double> RegionShares()
        {
            var shares = new Dictionary<string, double>(StringComparer.Ordinal);
            var occupied = new HashSet<string>(StringComparer.Ordinal);

            if (_attachedLayout is not null)
            {
                foreach (UiPanelPlacement placement in _attachedLayout.Placements)
                {
                    if (!placement.Hidden && !placement.IsFloating)
                        occupied.Add(placement.Region);
                }
            }

            foreach (string region in UiPanelRegion.DockedRegions)
            {
                if (region == UiPanelRegion.Center)
                    continue;

                // A drop creates the region if it is empty, so preview the share
                // it would then take.
                double weight = _attachedLayout?.RegionSize(region)
                    ?? WorkspacePage.DefaultRegionSize(region);

                shares[region] = WorkspaceDockOverlay.ShareFromWeight(weight);
            }

            return shares;
        }

        private void OnPanelMenuRequested(object? sender, EventArgs e)
        {
            if (_attachedLayout is null || sender is not WorkspacePanel panel)
                return;

            MenuFlyout flyout = WorkspacePanelMenu.Build(
                _attachedLayout,
                panel,
                WorkspaceLayoutCatalog.Find(_attachedLayout.PageKey, panel.PanelKey));

            flyout.ShowAt(panel.HeaderHandle ?? panel);
        }

        // ---- layout construction -------------------------------------------------

        /// <summary>
        /// Re-renders the page from the current placements. Subscribing to a
        /// panel property fires immediately, so a rebuild can re-enter through
        /// its own wiring; one pass is enough.
        /// </summary>
        private void Rebuild()
        {
            if (_attachedLayout is null || Panels.Count == 0)
                return;

            // Reparenting re-evaluates the bindings that decide whether a
            // section is shown at all, so one pass can change the answer it was
            // built from. A nested request is therefore folded into the pass
            // already running rather than dropped — dropping it left the page
            // rendering a region whose panels had all since become invisible:
            // an empty rail still holding its share of the width, which is the
            // blank half-page in the reports.
            if (_rebuilding)
                return;

            _rebuilding = true;

            try
            {
                // Re-run only while the set of visible sections actually
                // changed. Testing that, rather than "did anything ask again",
                // matters because detaching a panel drops its DataContext and
                // parking it restores it — so something always asks again, and
                // a request-counting loop burned every pass on every rebuild.
                // Bounded anyway: a binding that flipped on every pass would
                // otherwise spin here instead of drawing anything at all.
                for (int pass = 0; pass < RebuildPassLimit; pass++)
                {
                    if (!RebuildCore())
                        break;
                }
            }
            finally
            {
                _rebuilding = false;
            }
        }

        /// <summary>
        /// Rebuilds the region grids from the current placements. Every panel is
        /// detached from its old parent first, so a panel can move between
        /// regions without ever being owned by two grids at once.
        ///
        /// The shape is the one every page in this app already had: a full-width
        /// band at the top and bottom, and between them a row of left rail,
        /// centre, and right rail. A page header dropped in the top band spans
        /// the page exactly as its card used to.
        /// </summary>
        /// <returns>
        /// True when reparenting changed which sections are visible, so the
        /// page it just drew no longer matches the answer it was built from.
        /// </returns>
        private bool RebuildCore()
        {
            IndexPanels();

            // Read before anything moves. A panel inherits its DataContext from
            // its parent, so once it is detached its IsVisible binding stops
            // evaluating and it reports itself visible again — which is how an
            // empty section kept claiming a full column of the page.
            var visible = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (WorkspacePanel panel in Panels)
                visible[panel.PanelKey] = panel.IsVisible;

            // A panel that is staying in its own window must not be detached.
            // SyncFloatingWindows only ever reparents into a window it has just
            // created, so detaching one that was already floating left the
            // panel in no visual tree at all: a blank floating window, no card
            // on the page, and no way back except closing the window.
            //
            // The test is "still floating", not "currently in a window": a
            // panel being docked back is leaving its window this pass and has
            // to be released, or placing it into a region would hand a control
            // that still has a visual parent to a second one.
            var stayFloating = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiPanelPlacement placement in _attachedLayout!.Placements)
            {
                if (placement.IsFloating && !placement.Hidden)
                    stayFloating.Add(placement.Key);
            }

            foreach (WorkspacePanel panel in Panels)
            {
                if (panel.Parent is WorkspaceFloatingWindow &&
                    stayFloating.Contains(panel.PanelKey))
                {
                    continue;
                }

                Detach(panel);
            }

            _root.Children.Clear();
            _root.ColumnDefinitions.Clear();
            _root.RowDefinitions.Clear();
            _fillPanels.Clear();

            var byRegion = new Dictionary<string, List<WorkspacePanel>>(StringComparer.Ordinal);

            foreach (UiPanelPlacement placement in _attachedLayout!.Placements)
            {
                if (_panelsByKey.TryGetValue(placement.Key, out WorkspacePanel? known))
                    known.Region = placement.Region;

                if (placement.Hidden ||
                    placement.Region == UiPanelRegion.Float ||
                    known is null)
                {
                    continue;
                }

                // Not placed, but kept in a parented home so its bindings go on
                // deciding whether it should come back.
                if (!visible.GetValueOrDefault(placement.Key, true))
                {
                    _parked.Children.Add(known);
                    continue;
                }

                known.IsCollapsed = placement.Collapsed;

                if (!byRegion.TryGetValue(placement.Region, out List<WorkspacePanel>? list))
                    byRegion[placement.Region] = list = new List<WorkspacePanel>();

                list.Add(known);
            }

            // Below the narrow threshold the side rails fold into the centre
            // column, in region order, rather than squeezing three columns into
            // a phone-width window. This is the behaviour the pages that used
            // ResponsiveSplitGrid had, generalised to every page.
            if (_folded)
                FoldSideRegions(byRegion);

            bool hasTop = byRegion.ContainsKey(UiPanelRegion.Top);
            bool hasBottom = byRegion.ContainsKey(UiPanelRegion.Bottom);

            _root.RowDefinitions.Add(BandRow(hasTop, UiPanelRegion.Top, byRegion));
            _root.RowDefinitions.Add(GapRow(hasTop));
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            _root.RowDefinitions.Add(GapRow(hasBottom));
            _root.RowDefinitions.Add(BandRow(hasBottom, UiPanelRegion.Bottom, byRegion));

            if (hasTop)
            {
                AddAt(_root, BuildRegion(UiPanelRegion.Top, byRegion[UiPanelRegion.Top]), 0, 0);
                AddAt(_root, BuildSplitter(GridResizeDirection.Rows, UiPanelRegion.Top), 1, 0);
            }

            // A band's weight is measured against the body, so the body is the
            // reference the root grid's splitters normalize against — the same
            // role the centre column plays inside the body.
            Control body = BuildBody(byRegion);
            body.Tag = UiPanelRegion.Center;
            AddAt(_root, body, 2, 0);

            if (hasBottom)
            {
                AddAt(_root, BuildSplitter(GridResizeDirection.Rows, UiPanelRegion.Bottom), 3, 0);
                AddAt(_root, BuildRegion(UiPanelRegion.Bottom, byRegion[UiPanelRegion.Bottom]), 4, 0);
            }

            SyncFloatingWindows();

            // Did placing the panels change the answer? Reparenting hands a
            // panel its DataContext back, so a section whose own binding hides
            // it only reports that once it is somewhere in the tree.
            foreach (WorkspacePanel panel in Panels)
            {
                if (visible.GetValueOrDefault(panel.PanelKey, true) != panel.IsVisible)
                    return true;
            }

            return false;
        }

        // Left rail, centre, right rail. The rails are full height between the
        // top and bottom bands, which is what the split pages already looked
        // like before the bands existed.
        private Control BuildBody(Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            var body = new Grid();

            // Only the regions that actually hold a panel become columns. An
            // empty centre used to keep a full star column between the two
            // rails, which is what left a third of the page blank whenever a
            // user moved everything out to the sides.
            var occupied = new List<string>();

            foreach (string region in new[]
                { UiPanelRegion.Left, UiPanelRegion.Center, UiPanelRegion.Right })
            {
                if (byRegion.TryGetValue(region, out List<WorkspacePanel>? present) &&
                    present.Count > 0)
                {
                    occupied.Add(region);
                }
            }

            for (int index = 0; index < occupied.Count; index++)
            {
                if (index > 0)
                {
                    body.ColumnDefinitions.Add(GapColumn(true));
                    AddAt(
                        body,
                        BuildSplitter(
                            GridResizeDirection.Columns, SplitterRegion(occupied, index)),
                        0,
                        body.ColumnDefinitions.Count - 1);
                }

                string region = occupied[index];
                List<WorkspacePanel> panels = byRegion[region];

                body.ColumnDefinitions.Add(BodyColumn(region, panels, occupied.Count));
                AddAt(body, BuildRegion(region, panels), 0, body.ColumnDefinitions.Count - 1);
            }

            return body;
        }

        // Which region's stored weight a splitter writes. The centre is the
        // reference every other weight is measured against, so a splitter that
        // borders it persists the rail on the other side of itself.
        private static string SplitterRegion(IReadOnlyList<string> occupied, int index) =>
            occupied[index] == UiPanelRegion.Center ? occupied[index - 1] : occupied[index];

        // A column's share of the body. The centre is the reference weight; a
        // rail takes the share the layout stored for it.
        private ColumnDefinition BodyColumn(
            string region,
            List<WorkspacePanel> panels,
            int columnCount)
        {
            double weight = region == UiPanelRegion.Center
                ? UiPanelPlacement.DefaultSize
                : _attachedLayout!.RegionSize(region);

            var column = new ColumnDefinition(new GridLength(weight, GridUnitType.Star))
            {
                MinWidth = panels.Max(panel => panel.MinPanelWidth),
            };

            // A rail's declared maximum only applies while another column can
            // absorb the slack, and only when every panel in the rail declares
            // one: Enumerable.Max skips NaN, so a rail holding one capped panel
            // beside an uncapped one used to inherit the cap and strand the
            // rest of the page as dead space.
            if (region != UiPanelRegion.Center &&
                columnCount > 1 &&
                panels.All(panel => !double.IsNaN(panel.MaxPanelWidth)))
            {
                double max = panels.Max(panel => panel.MaxPanelWidth);

                if (max > 0 && max >= column.MinWidth)
                    column.MaxWidth = max;
            }

            return column;
        }

        // Narrow windows get one column: the rails' panels join the centre in
        // reading order, so nothing disappears and nothing is crushed.
        private static void FoldSideRegions(Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            var centre = new List<WorkspacePanel>();

            if (byRegion.TryGetValue(UiPanelRegion.Left, out List<WorkspacePanel>? left))
                centre.AddRange(left);

            if (byRegion.TryGetValue(UiPanelRegion.Center, out List<WorkspacePanel>? existing))
                centre.AddRange(existing);

            if (byRegion.TryGetValue(UiPanelRegion.Right, out List<WorkspacePanel>? right))
                centre.AddRange(right);

            byRegion.Remove(UiPanelRegion.Left);
            byRegion.Remove(UiPanelRegion.Right);

            if (centre.Count > 0)
                byRegion[UiPanelRegion.Center] = centre;
        }

        /// <summary>
        /// One region's content. A region mixes two kinds of block, in
        /// declaration order: a run of content-height panels, which flows (so
        /// two cards that declare a preferred width sit side by side), and a
        /// filling panel, which takes star space and can be resized against
        /// another filling panel.
        ///
        /// The whole region scrolls, with its content held to at least the
        /// viewport height. That is what lets a page have both a table that
        /// absorbs the slack and a stack of cards that can overflow — the two
        /// things every page here needs and the plain grid could not give.
        /// </summary>
        private Control BuildRegion(string region, IReadOnlyList<WorkspacePanel> panels)
        {
            if (panels.Count == 0)
                return new Grid();

            var grid = new Grid();
            var run = new List<WorkspacePanel>();
            bool previousFills = false;
            bool first = true;

            // A run of cards followed by a filling pane is the shape most split
            // pages have, and the boundary between them was a drag handle
            // before this system existed. Remembering that the last block was a
            // run lets that handle come back.
            bool previousIsRun = false;

            void CloseRun()
            {
                if (run.Count == 0)
                    return;

                AddGap(grid, resizable: false, panel: null, ref first);
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                AddAt(grid, BuildFlowBlock(run), grid.RowDefinitions.Count - 1, 0);
                run.Clear();
                previousFills = false;
                previousIsRun = true;
            }

            foreach (WorkspacePanel panel in panels)
            {
                if (!FillsSpace(panel))
                {
                    run.Add(panel);
                    continue;
                }

                CloseRun();

                // A filling pane can be resized against whatever is above it —
                // another filling pane, or a run of cards whose Auto row the
                // drag converts to a fixed height. Anything else gets the
                // page's ordinary card gutter, because a drag handle that
                // resizes nothing is a lie about what it does.
                AddGap(grid, previousFills || previousIsRun, panel, ref first);

                grid.RowDefinitions.Add(
                    new RowDefinition(new GridLength(SizeOf(panel), GridUnitType.Star))
                    {
                        MinHeight = panel.MinPanelHeight,
                    });
                AddAt(grid, panel, grid.RowDefinitions.Count - 1, 0);
                _fillPanels.Add(panel);
                previousFills = true;
                previousIsRun = false;
            }

            CloseRun();

            grid.Tag = region;

            // A region never scrolls on its own any more. The page owns the
            // vertical overflow (see UpdatePageMinimum), which is what stops a
            // section ending up behind a scrollbar three levels down and lets
            // a filling panel keep a real height so a table inside it scrolls
            // internally with its column headers pinned.
            return grid;
        }

        // The gutter or splitter between two blocks. Nothing is emitted before
        // the first block, so a region never opens with a stray gap.
        private void AddGap(Grid grid, bool resizable, WorkspacePanel? panel, ref bool first)
        {
            if (first)
            {
                first = false;
                return;
            }

            grid.RowDefinitions.Add(
                new RowDefinition(new GridLength(
                    resizable ? SplitterExtent : PanelGutter)));

            if (resizable && panel is not null)
            {
                AddAt(
                    grid,
                    BuildPanelSplitter(GridResizeDirection.Rows, panel),
                    grid.RowDefinitions.Count - 1,
                    0);
            }
        }

        // A run of content-height panels, laid out by the flow panel so a pair
        // that declares a preferred width sits side by side.
        private Control BuildFlowBlock(IReadOnlyList<WorkspacePanel> panels)
        {
            var flow = new WorkspaceFlowPanel { Columns = FlowColumns };

            foreach (WorkspacePanel panel in panels)
                flow.Children.Add(panel);

            return flow;
        }

        private static bool FillsSpace(WorkspacePanel panel) =>
            panel.SizeMode == WorkspacePanelSizeMode.Fill && !panel.IsCollapsed;

        private double SizeOf(WorkspacePanel panel)
        {
            foreach (UiPanelPlacement placement in _attachedLayout!.Placements)
            {
                if (string.Equals(placement.Key, panel.PanelKey, StringComparison.Ordinal))
                    return placement.Size;
            }

            return UiPanelPlacement.DefaultSize;
        }

        // A band sizes to its content unless something in it fills, in which
        // case it takes its saved share of the page. A page header band must
        // never steal a fixed slice of the window the way a star row would.
        private RowDefinition BandRow(
            bool present,
            string region,
            Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            if (!present)
                return new RowDefinition(new GridLength(0));

            List<WorkspacePanel> panels = byRegion[region];

            if (!panels.Any(FillsSpace))
                return new RowDefinition(GridLength.Auto);

            return new RowDefinition(
                new GridLength(_attachedLayout!.RegionSize(region), GridUnitType.Star))
            {
                MinHeight = panels.Max(panel => panel.MinPanelHeight),
            };
        }

        private static ColumnDefinition GapColumn(bool present) =>
            new(new GridLength(present ? SplitterExtent : 0));

        private static RowDefinition GapRow(bool present) =>
            new(new GridLength(present ? SplitterExtent : 0));

        // The app's splitter idiom: an 8px grab strip with a 1px breathing
        // margin, so the gutter reads as space rather than as a rule. It
        // carries the resize cursor for its direction; the hover tint and the
        // focus ring come from the GridSplitter styles in Themes/Controls.axaml
        // — which is why no Background is set here, since a local value would
        // outrank those style triggers and neither would ever show.
        private static GridSplitter NewSplitter(GridResizeDirection direction) =>
            new()
            {
                ResizeDirection = direction,
                Margin = direction == GridResizeDirection.Columns
                    ? new Thickness(1, 0)
                    : new Thickness(0, 1),
                Cursor = new Cursor(direction == GridResizeDirection.Columns
                    ? StandardCursorType.SizeWestEast
                    : StandardCursorType.SizeNorthSouth),
            };

        private GridSplitter BuildSplitter(GridResizeDirection direction, string region)
        {
            GridSplitter splitter = NewSplitter(direction);

            AutomationProperties.SetName(
                splitter, $"Resize the {UiPanelRegion.DisplayName(region).ToLowerInvariant()} region");

            splitter.DragCompleted += (_, _) =>
            {
                if (splitter.Parent is Grid grid)
                    PersistRegionSizes(grid, direction);
            };

            return splitter;
        }

        private GridSplitter BuildPanelSplitter(
            GridResizeDirection direction,
            WorkspacePanel panel)
        {
            GridSplitter splitter = NewSplitter(direction);

            AutomationProperties.SetName(
                splitter,
                string.IsNullOrEmpty(panel.Title)
                    ? "Resize this section"
                    : $"Resize {panel.Title}");

            splitter.DragCompleted += (_, _) =>
            {
                if (splitter.Parent is Grid grid)
                    PersistPanelSizes(grid);
            };

            return splitter;
        }

        /// <summary>
        /// Rewrites this grid's stored region weights after a drag.
        ///
        /// A <see cref="GridSplitter"/> resizes by writing the new *pixel*
        /// extent into the bordering definitions as their star value, so the
        /// number left behind is a measurement, not the ratio the layout
        /// stores. Persisting it raw drove the weight straight to its clamp
        /// ceiling on the very first drag — a live settings file showed
        /// <c>history / left = 10</c> — after which the rail took the whole
        /// page and the equality guard made every later drag a silent no-op.
        ///
        /// Every star definition in the grid is therefore re-expressed as a
        /// ratio against the centre, which is what the weight means. Rewriting
        /// all of them rather than only the two the splitter touched also stops
        /// the untouched regions keeping stale weights on a different scale.
        /// Nothing is written mid-drag, so a cancelled drag changes nothing.
        ///
        /// A band of content-height cards is an Auto row, and Avalonia resizes
        /// an Auto definition by rewriting it in pixels rather than touching
        /// the star beside it. There is no ratio to recover in that case, so
        /// dragging the edge of such a band holds for the session and is not
        /// persisted — the early return below is where that is decided.
        /// </summary>
        private void PersistRegionSizes(Grid grid, GridResizeDirection direction)
        {
            if (_attachedLayout is null)
                return;

            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            double reference = 0;

            foreach (Control child in grid.Children.OfType<Control>())
            {
                if (child.Tag is not string region || !UiPanelRegion.IsRegion(region))
                    continue;

                int index = direction == GridResizeDirection.Columns
                    ? Grid.GetColumn(child)
                    : Grid.GetRow(child);

                GridLength length = direction == GridResizeDirection.Columns
                    ? grid.ColumnDefinitions[index].Width
                    : grid.RowDefinitions[index].Height;

                if (!length.IsStar || length.Value <= 0)
                    continue;

                weights[region] = length.Value;

                if (region == UiPanelRegion.Center)
                    reference = length.Value;
            }

            if (weights.Count == 0)
                return;

            // A page with everything in the rails has no centre to measure
            // against, so the largest share becomes the unit; the ratio
            // between the rails is what the user actually set.
            if (reference <= 0)
                reference = weights.Values.Max();

            foreach ((string region, double value) in weights)
            {
                if (region != UiPanelRegion.Center)
                {
                    _attachedLayout.ResizeRegion(
                        region, UiPanelPlacement.WeightFromExtent(value, reference));
                }
            }
        }

        /// <summary>
        /// The same correction for the panel weights inside one region. They
        /// are normalized to a mean of one, so a region dragged repeatedly
        /// keeps proportions that stay inside the stored range instead of
        /// drifting to the clamp.
        /// </summary>
        private void PersistPanelSizes(Grid grid)
        {
            if (_attachedLayout is null)
                return;

            var sizes = new List<(string Key, double Value)>();

            foreach (Control child in grid.Children.OfType<Control>())
            {
                if (child is not WorkspacePanel panel)
                    continue;

                GridLength length = grid.RowDefinitions[Grid.GetRow(child)].Height;

                if (length.IsStar && length.Value > 0)
                    sizes.Add((panel.PanelKey, length.Value));
            }

            // Fewer than two star rows means nothing was proportioned against
            // anything: Avalonia resizes an Auto row against a star one by
            // rewriting only the Auto side, so the star value is unchanged and
            // normalising it against itself would silently reset a pane the
            // user had sized to 1.0 — while still not persisting the drag.
            if (sizes.Count < 2)
                return;

            double mean = sizes.Average(entry => entry.Value);

            if (mean <= 0)
                return;

            foreach ((string key, double value) in sizes)
            {
                _attachedLayout.ResizePanel(
                    key, UiPanelPlacement.WeightFromExtent(value, mean));
            }
        }

        private static void AddAt(Grid grid, Control child, int row, int column)
        {
            Grid.SetRow(child, row);
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }

        private static void Detach(WorkspacePanel panel)
        {
            switch (panel.Parent)
            {
                // Covers both the region grids and the flowing regions, which
                // are Panels of a different shape.
                case Panel host:
                    host.Children.Remove(panel);
                    break;
                case ContentControl content when ReferenceEquals(content.Content, panel):
                    content.Content = null;
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, panel):
                    decorator.Child = null;
                    break;
            }
        }

        // ---- floating panels -----------------------------------------------------

        /// <summary>
        /// Opens a window for every panel the layout floats and closes the ones
        /// it no longer does. The panel control itself moves into and out of the
        /// window, so floating a pane never resets what is selected or scrolled
        /// inside it.
        /// </summary>
        private void SyncFloatingWindows()
        {
            if (_attachedLayout is null)
                return;

            var shouldFloat = new HashSet<string>(StringComparer.Ordinal);

            foreach (UiPanelPlacement placement in _attachedLayout.Placements)
            {
                if (placement.IsFloating && !placement.Hidden)
                    shouldFloat.Add(placement.Key);
            }

            foreach (string key in _floating.Keys.ToArray())
            {
                if (shouldFloat.Contains(key))
                    continue;

                if (_floating.Remove(key, out WorkspaceFloatingWindow? stale))
                {
                    // Release the panel before the window goes, so the panel's
                    // logical parent is never two things at once.
                    stale.Content = null;
                    stale.CloseRequested -= OnFloatingWindowClosed;
                    stale.Close();
                }
            }

            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;

            foreach (UiPanelPlacement placement in _attachedLayout.Placements)
            {
                if (!shouldFloat.Contains(placement.Key) ||
                    _floating.ContainsKey(placement.Key) ||
                    !_panelsByKey.TryGetValue(placement.Key, out WorkspacePanel? panel))
                {
                    continue;
                }

                var window = new WorkspaceFloatingWindow
                {
                    Title = string.IsNullOrEmpty(panel.Title)
                        ? "Section"
                        : panel.Title,
                    Tag = placement.Key,
                };

                // A saved position from a screen that is no longer attached
                // would strand the window off-screen, so it is clamped onto a
                // currently visible working area first — the same policy the
                // detached tab windows use.
                if (placement.Left != 0 || placement.Top != 0)
                {
                    window.PlacementBounds = MainWindow.ClampToScreens(
                        new Rect(placement.Left, placement.Top, placement.Width, placement.Height),
                        WorkingAreas(owner),
                        OwnerBounds(owner),
                        cascadeIndex: _floating.Count,
                        out _);
                }
                else
                {
                    window.Width = placement.Width;
                    window.Height = placement.Height;
                }

                // The panel stops inheriting the page view model the moment it
                // is reparented into another window, which would silently break
                // every binding inside it — including a confirmation checkbox
                // and the IsEnabled gate on the destructive action it guards.
                // TabDetachCoordinator solves this the same way for detached
                // tabs; a floated section must not be the exception.
                if (panel.DataContext is null)
                    window.DataContext = DataContext;

                window.Content = panel;
                window.CloseRequested += OnFloatingWindowClosed;
                _floating[placement.Key] = window;
                window.Show(owner);
            }
        }

        private static IReadOnlyList<Rect> WorkingAreas(Window owner)
        {
            double scale = owner.RenderScaling;
            var areas = new Rect[owner.Screens.All.Count];

            for (int index = 0; index < areas.Length; index++)
                areas[index] = owner.Screens.All[index].WorkingArea.ToRectWithDpi(scale);

            return areas;
        }

        private static Rect OwnerBounds(Window owner)
        {
            double scale = owner.RenderScaling;

            return new Rect(
                owner.Position.X / scale,
                owner.Position.Y / scale,
                owner.Width,
                owner.Height);
        }

        // Closing a floating panel's window docks it back, exactly as closing a
        // detached tab's window reattaches the tab. The last placement is saved
        // first so re-floating reopens where the user left it.
        private void OnFloatingWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not WorkspaceFloatingWindow window ||
                window.Tag is not string key ||
                _attachedLayout is null)
            {
                return;
            }

            if (!_floating.Remove(key))
                return;

            Rect bounds = window.PlacementBounds;
            window.Content = null;
            window.CloseRequested -= OnFloatingWindowClosed;

            _attachedLayout.FloatPanel(key, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _attachedLayout.DockPanel(key);
        }

        // ---- drag to dock --------------------------------------------------------

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_attachedLayout is null ||
                !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Only a press that starts on a panel header begins a move; a press
            // anywhere in a panel's body belongs to that panel's own content.
            _pressedPanel = (e.Source as Visual)?
                .GetSelfAndVisualAncestors()
                .OfType<WorkspacePanel>()
                .FirstOrDefault();

            if (_pressedPanel is not null &&
                !IsWithinHeader(_pressedPanel, e.Source as Visual))
            {
                _pressedPanel = null;
            }

            _pressOrigin = e.GetPosition(this);
            _dragging = false;
        }

        private static bool IsWithinHeader(WorkspacePanel panel, Visual? source)
        {
            if (panel.HeaderHandle is not { } header || source is null)
                return false;

            // Buttons inside the header (collapse, menu, the section's own
            // actions) keep their click; only bare header chrome starts a drag.
            foreach (Visual visual in source.GetSelfAndVisualAncestors())
            {
                if (ReferenceEquals(visual, panel))
                    return false;

                if (visual is Button or ToggleButton)
                    return false;

                if (ReferenceEquals(visual, header))
                    return true;
            }

            return false;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_pressedPanel is null || _attachedLayout is null)
                return;

            Point position = e.GetPosition(this);

            if (!_dragging)
            {
                double dx = position.X - _pressOrigin.X;
                double dy = position.Y - _pressOrigin.Y;

                if (Math.Sqrt((dx * dx) + (dy * dy)) < DragThreshold)
                    return;

                _dragging = true;
                e.Pointer.Capture(this);
                _pressedPanel.SetDragging(true);
                // The guide previews the real geometry, so its shares come
                // from the same weights the layout is built with — one per
                // region, because the two rails and the two bands can each
                // carry a different weight.
                _overlay.Begin(Bounds.Size, RegionShares());
            }

            _overlay.Track(position);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_pressedPanel is null)
                return;

            WorkspacePanel panel = _pressedPanel;
            _pressedPanel = null;

            if (!_dragging)
                return;

            _dragging = false;
            e.Pointer.Capture(null);
            panel.SetDragging(false);

            string? region = _overlay.End();

            if (region is not null && _attachedLayout is not null)
            {
                // Dropping onto a panel places the dragged one before or after
                // it, so a drag can reorder within a region and not just append.
                _attachedLayout.MovePanel(
                    panel.PanelKey,
                    region,
                    InsertionOrder(panel, region, e.GetPosition(this)));
            }
        }

        /// <summary>
        /// Where in the target region a drop lands. Dropping over the upper
        /// half of an existing panel inserts before it and the lower half after
        /// it; dropping over empty space appends. Half-steps are used because
        /// the layout renumbers densely afterwards, so a half-step reliably
        /// lands the panel on one side of its new neighbour.
        /// </summary>
        private int InsertionOrder(WorkspacePanel dragged, string region, Point position)
        {
            if (_attachedLayout is null)
                return int.MaxValue;

            foreach (UiPanelPlacement placement in _attachedLayout.Placements)
            {
                if (placement.Hidden ||
                    !string.Equals(placement.Region, region, StringComparison.Ordinal) ||
                    string.Equals(placement.Key, dragged.PanelKey, StringComparison.Ordinal) ||
                    !_panelsByKey.TryGetValue(placement.Key, out WorkspacePanel? target) ||
                    !target.IsVisible)
                {
                    continue;
                }

                Rect bounds = target.Bounds;

                if (target.TranslatePoint(new Point(0, 0), this) is not { } origin)
                    continue;

                var area = new Rect(origin, bounds.Size);

                if (!area.Contains(position))
                    continue;

                bool before = position.Y < area.Y + (area.Height / 2);
                return before ? placement.Order - 1 : placement.Order + 1;
            }

            return int.MaxValue;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Escape abandons a drag in progress without changing anything,
            // exactly as a docking drag in an IDE does.
            if (e.Key == Key.Escape && _dragging && _pressedPanel is not null)
            {
                _pressedPanel.SetDragging(false);
                _pressedPanel = null;
                _dragging = false;
                _overlay.Cancel();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }
    }
}

