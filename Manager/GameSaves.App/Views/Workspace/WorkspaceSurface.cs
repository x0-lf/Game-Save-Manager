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
        /// The gutter a scrolling region leaves for its scrollbar, so the bar
        /// sits beside the cards rather than on top of them.
        /// </summary>
        private const double ScrollGutter = 14;

        public static readonly StyledProperty<IWorkspaceLayoutPage?> LayoutProperty =
            AvaloniaProperty.Register<WorkspaceSurface, IWorkspaceLayoutPage?>(nameof(Layout));

        /// <summary>The most panels a flowing region puts on one row.</summary>
        public static readonly StyledProperty<int> FlowColumnsProperty =
            AvaloniaProperty.Register<WorkspaceSurface, int>(nameof(FlowColumns), 3);

        private readonly Grid _root = new();
        private readonly WorkspaceDockOverlay _overlay = new();
        private readonly Dictionary<string, WorkspacePanel> _panelsByKey = new(StringComparer.Ordinal);
        private readonly HashSet<WorkspacePanel> _menuWired = new();
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
            Children.Add(_root);
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
                    new AnonymousObserver<bool>(_ => Rebuild()));

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
            if (_attachedLayout is null || Panels.Count == 0 || _rebuilding)
                return;

            _rebuilding = true;

            try
            {
                RebuildCore();
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
        private void RebuildCore()
        {
            IndexPanels();

            foreach (WorkspacePanel panel in Panels)
                Detach(panel);

            _root.Children.Clear();
            _root.ColumnDefinitions.Clear();
            _root.RowDefinitions.Clear();

            var byRegion = new Dictionary<string, List<WorkspacePanel>>(StringComparer.Ordinal);

            foreach (UiPanelPlacement placement in _attachedLayout!.Placements)
            {
                if (_panelsByKey.TryGetValue(placement.Key, out WorkspacePanel? known))
                    known.Region = placement.Region;

                if (placement.Hidden ||
                    placement.Region == UiPanelRegion.Float ||
                    known is null ||
                    !known.IsVisible)
                {
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

            AddAt(_root, BuildBody(byRegion), 2, 0);

            if (hasBottom)
            {
                AddAt(_root, BuildSplitter(GridResizeDirection.Rows, UiPanelRegion.Bottom), 3, 0);
                AddAt(_root, BuildRegion(UiPanelRegion.Bottom, byRegion[UiPanelRegion.Bottom]), 4, 0);
            }

            SyncFloatingWindows();
        }

        // Left rail, centre, right rail. The rails are full height between the
        // top and bottom bands, which is what the split pages already looked
        // like before the bands existed.
        private Control BuildBody(Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            bool hasLeft = byRegion.ContainsKey(UiPanelRegion.Left);
            bool hasRight = byRegion.ContainsKey(UiPanelRegion.Right);

            var body = new Grid();
            body.ColumnDefinitions.Add(RailColumn(hasLeft, UiPanelRegion.Left, byRegion));
            body.ColumnDefinitions.Add(GapColumn(hasLeft));
            body.ColumnDefinitions.Add(CentreColumn(byRegion));
            body.ColumnDefinitions.Add(GapColumn(hasRight));
            body.ColumnDefinitions.Add(RailColumn(hasRight, UiPanelRegion.Right, byRegion));

            if (hasLeft)
            {
                AddAt(body, BuildRegion(UiPanelRegion.Left, byRegion[UiPanelRegion.Left]), 0, 0);
                AddAt(body, BuildSplitter(GridResizeDirection.Columns, UiPanelRegion.Left), 0, 1);
            }

            if (byRegion.TryGetValue(UiPanelRegion.Center, out List<WorkspacePanel>? centre))
                AddAt(body, BuildRegion(UiPanelRegion.Center, centre), 0, 2);

            if (hasRight)
            {
                AddAt(body, BuildSplitter(GridResizeDirection.Columns, UiPanelRegion.Right), 0, 3);
                AddAt(body, BuildRegion(UiPanelRegion.Right, byRegion[UiPanelRegion.Right]), 0, 4);
            }

            return body;
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

                AddGap(grid, resizable: false, panelKey: null, ref first);
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
                AddGap(grid, previousFills || previousIsRun, panel.PanelKey, ref first);

                grid.RowDefinitions.Add(
                    new RowDefinition(new GridLength(SizeOf(panel), GridUnitType.Star))
                    {
                        MinHeight = panel.MinPanelHeight,
                    });
                AddAt(grid, panel, grid.RowDefinitions.Count - 1, 0);
                previousFills = true;
                previousIsRun = false;
            }

            CloseRun();

            grid.Tag = region;

            // A region that contains a filling panel is BOUNDED: the filling
            // panel is the thing that absorbs the slack, and it needs a real
            // height so a table inside it scrolls internally with its column
            // headers pinned. Wrapping it in a scroller would measure it with
            // unbounded height, the table would grow to its full content
            // height, and both the pinned headers and the page's own scrolling
            // would be lost. This is why the two pages that deliberately had no
            // page-level scroller are the two whose panels fill.
            if (panels.Any(FillsSpace))
                return grid;

            // A region of only content-height cards scrolls, which is the
            // page-level scroller every stacked-card page already had. The
            // right margin keeps the bar in the page gutter instead of over
            // the cards.
            grid.Margin = new Thickness(0, 0, ScrollGutter, 0);

            var scroller = new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            scroller.Classes.Add("pageScroll");

            return scroller;
        }

        // The gutter or splitter between two blocks. Nothing is emitted before
        // the first block, so a region never opens with a stray gap.
        private void AddGap(Grid grid, bool resizable, string? panelKey, ref bool first)
        {
            if (first)
            {
                first = false;
                return;
            }

            grid.RowDefinitions.Add(
                new RowDefinition(new GridLength(
                    resizable ? SplitterExtent : PanelGutter)));

            if (resizable && panelKey is not null)
            {
                AddAt(
                    grid,
                    BuildPanelSplitter(GridResizeDirection.Rows, panelKey),
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

        private ColumnDefinition RailColumn(
            bool present,
            string region,
            Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            if (!present)
                return new ColumnDefinition(new GridLength(0));

            List<WorkspacePanel> panels = byRegion[region];

            var column = new ColumnDefinition(
                new GridLength(_attachedLayout!.RegionSize(region), GridUnitType.Star))
            {
                MinWidth = panels.Max(panel => panel.MinPanelWidth),
            };

            // A rail that declares a maximum keeps it, so a wide window cannot
            // stretch a form column past the measure it was designed for.
            double max = panels.Max(panel => panel.MaxPanelWidth);

            if (!double.IsNaN(max) && max > 0)
                column.MaxWidth = max;

            return column;
        }

        // The centre takes the slack, but never below the widest minimum its
        // own panels declare — otherwise a splitter drag could squeeze the
        // page's main pane to nothing.
        private static ColumnDefinition CentreColumn(
            Dictionary<string, List<WorkspacePanel>> byRegion)
        {
            var column = new ColumnDefinition(new GridLength(1, GridUnitType.Star));

            if (byRegion.TryGetValue(UiPanelRegion.Center, out List<WorkspacePanel>? panels) &&
                panels.Count > 0)
            {
                column.MinWidth = panels.Max(panel => panel.MinPanelWidth);
            }

            return column;
        }

        private static ColumnDefinition GapColumn(bool present) =>
            new(new GridLength(present ? SplitterExtent : 0));

        private static RowDefinition GapRow(bool present) =>
            new(new GridLength(present ? SplitterExtent : 0));

        // The app's existing splitter idiom: an 8px transparent grab strip with
        // a 1px margin, so the gutter reads as space rather than a rule.
        private GridSplitter BuildSplitter(GridResizeDirection direction, string region)
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = direction,
                Background = Avalonia.Media.Brushes.Transparent,
                Margin = direction == GridResizeDirection.Columns
                    ? new Thickness(1, 0)
                    : new Thickness(0, 1),
            };

            AutomationProperties.SetName(
                splitter, $"Resize the {UiPanelRegion.DisplayName(region).ToLowerInvariant()} region");

            splitter.DragCompleted += (_, _) => PersistRegionSize(splitter, direction, region);
            return splitter;
        }

        private GridSplitter BuildPanelSplitter(GridResizeDirection direction, string panelKey)
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = direction,
                Background = Avalonia.Media.Brushes.Transparent,
                Margin = direction == GridResizeDirection.Columns
                    ? new Thickness(1, 0)
                    : new Thickness(0, 1),
            };

            splitter.DragCompleted += (_, _) => PersistPanelSize(splitter, direction, panelKey);
            return splitter;
        }

        // A splitter drag rewrites the grid definition's star value in place;
        // reading it back after the drag is the only way to learn the ratio the
        // user chose. Nothing is persisted mid-drag, so a cancelled drag leaves
        // the saved layout untouched.
        private void PersistRegionSize(
            GridSplitter splitter,
            GridResizeDirection direction,
            string region)
        {
            if (splitter.Parent is not Grid grid)
                return;

            int index = direction == GridResizeDirection.Columns
                ? Grid.GetColumn(splitter)
                : Grid.GetRow(splitter);

            // The region definition is the one the splitter borders: the
            // definition before it for left/top, after it for right/bottom.
            bool leading = region is UiPanelRegion.Left or UiPanelRegion.Top;
            int target = leading ? index - 1 : index + 1;

            if (target < 0)
                return;

            GridLength length = direction == GridResizeDirection.Columns
                ? grid.ColumnDefinitions[target].Width
                : grid.RowDefinitions[target].Height;

            if (length.IsStar)
                _attachedLayout?.ResizeRegion(region, length.Value);
        }

        private void PersistPanelSize(
            GridSplitter splitter,
            GridResizeDirection direction,
            string panelKey)
        {
            if (splitter.Parent is not Grid grid)
                return;

            int index = Grid.GetRow(splitter);

            foreach (int target in new[] { index - 1, index + 1 })
            {
                if (target < 0 || target >= grid.RowDefinitions.Count)
                    continue;

                Control? neighbour = grid.Children
                    .OfType<Control>()
                    .FirstOrDefault(child => Grid.GetRow(child) == target);

                if (neighbour is not WorkspacePanel panel)
                    continue;

                GridLength length = grid.RowDefinitions[target].Height;

                if (length.IsStar)
                    _attachedLayout?.ResizePanel(panel.PanelKey, length.Value);
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
