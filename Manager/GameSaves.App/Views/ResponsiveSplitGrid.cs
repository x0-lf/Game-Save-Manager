using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameSaves.App.Views
{
    // Collapses a [left | splitter | right] three-column grid into stacked
    // rows when the grid is narrower than the threshold, and restores the
    // columns above it. The splitter is hidden while stacked, because a
    // vertical drag between rows is not the interaction the layout promises.
    // This exists because three views share the exact same split shape and
    // all three amputated their right pane at 800x600.
    internal static class ResponsiveSplitGrid
    {
        public static void Attach(Grid grid, double threshold)
        {
            List<(GridLength Width, double MinWidth, double MaxWidth)> original =
                grid.ColumnDefinitions
                    .Select(column => (column.Width, column.MinWidth, column.MaxWidth))
                    .ToList();

            if (original.Count != 3)
                throw new InvalidOperationException(
                    "ResponsiveSplitGrid expects exactly left, splitter, right columns.");

            Control left = grid.Children.OfType<Control>()
                .First(child => Grid.GetColumn(child) == 0);
            Control splitter = grid.Children.OfType<Control>()
                .First(child => Grid.GetColumn(child) == 1);
            Control right = grid.Children.OfType<Control>()
                .First(child => Grid.GetColumn(child) == 2);

            double originalLeftMaxHeight = left.MaxHeight;
            bool collapsed = false;

            grid.SizeChanged += (_, e) =>
            {
                bool shouldCollapse = e.NewSize.Width > 0 && e.NewSize.Width < threshold;

                if (shouldCollapse == collapsed)
                    return;

                collapsed = shouldCollapse;

                grid.ColumnDefinitions.Clear();
                grid.RowDefinitions.Clear();

                if (collapsed)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

                    Grid.SetColumn(left, 0);
                    Grid.SetRow(left, 0);
                    left.MaxHeight = 260;

                    splitter.IsVisible = false;

                    Grid.SetColumn(right, 0);
                    Grid.SetRow(right, 1);
                }
                else
                {
                    foreach ((GridLength width, double minWidth, double maxWidth) in original)
                    {
                        grid.ColumnDefinitions.Add(new ColumnDefinition(width)
                        {
                            MinWidth = minWidth,
                            MaxWidth = maxWidth,
                        });
                    }

                    Grid.SetRow(left, 0);
                    Grid.SetColumn(left, 0);
                    left.MaxHeight = originalLeftMaxHeight;

                    splitter.IsVisible = true;

                    Grid.SetRow(right, 0);
                    Grid.SetColumn(right, 2);
                }
            };
        }
    }
}
