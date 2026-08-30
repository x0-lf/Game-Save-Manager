using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// Lays content-height panels out in rows, the way the dashboard's stat
    /// cards already sit in a three-across grid.
    ///
    /// A panel with no <see cref="WorkspacePanel.PreferredWidth"/> takes a full
    /// row — that is the page-header card, the warning banner, the guided
    /// setup card. Panels that declare one share a row, equally wide, up to
    /// <see cref="Columns"/> of them, and fall to fewer per row as the window
    /// narrows. At the shipped window width this reproduces the existing
    /// three-across arrangement exactly; below it the cards reflow instead of
    /// being squeezed past legibility, which is what the fixed grid did.
    /// </summary>
    public class WorkspaceFlowPanel : Panel
    {
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<WorkspaceFlowPanel, double>(nameof(Spacing), 10);

        public static readonly StyledProperty<int> ColumnsProperty =
            AvaloniaProperty.Register<WorkspaceFlowPanel, int>(nameof(Columns), 3);

        static WorkspaceFlowPanel()
        {
            AffectsMeasure<WorkspaceFlowPanel>(SpacingProperty, ColumnsProperty);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        /// <summary>The most panels one row may hold.</summary>
        public int Columns
        {
            get => GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        private readonly List<Row> _rows = new();
        private readonly Dictionary<Control, IDisposable> _visibilityWatches = new();

        /// <summary>
        /// Rows are built from the visible children only, so a section that
        /// hides itself leaves no gap. That makes the row map depend on a
        /// property the panel is not otherwise notified about, so each child's
        /// visibility is watched explicitly — without this a section that
        /// becomes visible after the first layout pass never appears.
        /// </summary>
        protected override void ChildrenChanged(
            object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.ChildrenChanged(sender, e);

            foreach (IDisposable watch in _visibilityWatches.Values)
                watch.Dispose();

            _visibilityWatches.Clear();

            foreach (Control child in Children)
            {
                _visibilityWatches[child] = child
                    .GetObservable(IsVisibleProperty)
                    .Subscribe(new AnonymousObserver<bool>(_ => InvalidateMeasure()));
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width)
                ? 0
                : availableSize.Width;

            BuildRows(width);

            double total = 0;

            foreach (Row row in _rows)
            {
                double cell = CellWidth(width, row.Children.Count);
                double tallest = 0;

                foreach (Control child in row.Children)
                {
                    child.Measure(new Size(cell, double.PositiveInfinity));
                    tallest = Math.Max(tallest, child.DesiredSize.Height);
                }

                row.Height = tallest;
                total += tallest;
            }

            if (_rows.Count > 1)
                total += Spacing * (_rows.Count - 1);

            return new Size(width, total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            BuildRows(finalSize.Width);

            double y = 0;

            foreach (Row row in _rows)
            {
                double cell = CellWidth(finalSize.Width, row.Children.Count);
                double x = 0;
                double tallest = 0;

                foreach (Control child in row.Children)
                {
                    child.Measure(new Size(cell, double.PositiveInfinity));
                    tallest = Math.Max(tallest, child.DesiredSize.Height);
                }

                foreach (Control child in row.Children)
                {
                    child.Arrange(new Rect(x, y, cell, tallest));
                    x += cell + Spacing;
                }

                y += tallest + Spacing;
            }

            return finalSize;
        }

        private double CellWidth(double available, int count)
        {
            if (count <= 1)
                return Math.Max(0, available);

            return Math.Max(0, (available - (Spacing * (count - 1))) / count);
        }

        // Rows are rebuilt on every pass rather than cached: the panel set and
        // the available width both change as panels are docked elsewhere, and a
        // stale row map would arrange a panel that is no longer here.
        private void BuildRows(double available)
        {
            _rows.Clear();

            Row? current = null;

            foreach (Control child in Children)
            {
                if (!child.IsVisible)
                    continue;

                double preferred = child is WorkspacePanel panel
                    ? panel.PreferredWidth
                    : double.NaN;

                // A panel with no preferred width owns its row outright.
                if (double.IsNaN(preferred) || preferred <= 0)
                {
                    _rows.Add(new Row(child));
                    current = null;
                    continue;
                }

                int fits = available > 0
                    ? Math.Clamp((int)Math.Floor((available + Spacing) / (preferred + Spacing)), 1, Columns)
                    : 1;

                if (current is null || current.Children.Count >= fits)
                {
                    current = new Row(child);
                    _rows.Add(current);
                    continue;
                }

                current.Children.Add(child);
            }
        }

        private sealed class Row
        {
            public Row(Control first)
            {
                Children = new List<Control> { first };
            }

            public List<Control> Children { get; }

            public double Height { get; set; }
        }
    }
}
