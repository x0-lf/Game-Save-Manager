using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GameSaves.App.Services;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// The docking guide shown while a panel is being dragged: five targets in
    /// the IDE diamond arrangement, and a preview rectangle covering exactly
    /// the space the panel would occupy if dropped.
    ///
    /// The preview is the honest part of the interaction — it shows the real
    /// resulting geometry, not a generic highlight, which is what makes a drop
    /// predictable. All colour comes from theme classes declared in
    /// <c>Themes/Workspace.axaml</c>, so the guide follows the theme variant
    /// and the chosen accent like the rest of the product rather than looking
    /// like an IDE skin dropped on top.
    /// </summary>
    internal sealed class WorkspaceDockOverlay : Canvas
    {
        private const double TargetSize = 44;
        private const double TargetGap = 6;

        // The share of the surface each region would actually claim. Supplied
        // by the surface from the live region weights rather than guessed: a
        // preview that shows 30% where the drop produces 40% is worse than no
        // preview, because it teaches the wrong thing.
        private readonly Dictionary<string, double> _shares = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Border> _targets = new(StringComparer.Ordinal);
        private readonly Border _preview;

        private string? _active;
        private Size _surface;

        public WorkspaceDockOverlay()
        {
            IsHitTestVisible = false;
            IsVisible = false;
            Opacity = 0;

            // Reduce Motion swaps this collection for an empty one, so the
            // guide appears instantly instead of fading. Same mechanism the
            // scrollbar fade uses.
            this.Bind(
                TransitionsProperty,
                this.GetResourceObservable(
                    GameSaves.App.Services.ThemeService.WorkspaceOverlayTransitionsKey)
                    .ToBinding());

            _preview = new Border { IsVisible = false };
            _preview.Classes.Add("dockPreview");
            Children.Add(_preview);

            foreach (string region in UiPanelRegion.DockedRegions)
            {
                Border target = BuildTarget(region);
                _targets[region] = target;
                Children.Add(target);
            }
        }

        private static Border BuildTarget(string region)
        {
            var glyph = new TextBlock
            {
                Text = GlyphFor(region),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.Classes.Add("dockTargetGlyph");

            var target = new Border
            {
                Width = TargetSize,
                Height = TargetSize,
                Child = glyph,
                IsVisible = false,
            };
            target.Classes.Add("dockTarget");

            AutomationProperties.SetName(
                target, $"Dock to the {UiPanelRegion.DisplayName(region).ToLowerInvariant()}");

            return target;
        }

        // Segoe Fluent Icons: dock-left, dock-right, dock-top, dock-bottom and
        // a full-page glyph for the centre.
        private static string GlyphFor(string region) => region switch
        {
            UiPanelRegion.Left => "",
            UiPanelRegion.Right => "",
            UiPanelRegion.Top => "",
            UiPanelRegion.Bottom => "",
            _ => "",
        };

        /// <summary>Shows the guide for a drag over a surface of the given size.</summary>
        public void Begin(Size surface, IReadOnlyDictionary<string, double> shares)
        {
            _surface = surface;
            _shares.Clear();

            foreach ((string region, double share) in shares)
                _shares[region] = share;
            _active = null;
            IsVisible = true;
            Opacity = 1;

            double centreX = surface.Width / 2;
            double centreY = surface.Height / 2;
            double step = TargetSize + TargetGap;

            Place(UiPanelRegion.Center, centreX, centreY);
            Place(UiPanelRegion.Left, centreX - step, centreY);
            Place(UiPanelRegion.Right, centreX + step, centreY);
            Place(UiPanelRegion.Top, centreX, centreY - step);
            Place(UiPanelRegion.Bottom, centreX, centreY + step);

            foreach (Border target in _targets.Values)
            {
                target.IsVisible = true;
                target.Classes.Remove("active");
            }

            _preview.IsVisible = false;
        }

        private void Place(string region, double centreX, double centreY)
        {
            Border target = _targets[region];
            SetLeft(target, centreX - TargetSize / 2);
            SetTop(target, centreY - TargetSize / 2);
        }

        /// <summary>Updates the highlighted target and the preview rectangle.</summary>
        public void Track(Point position)
        {
            string? hit = HitTarget(position) ?? EdgeRegion(position);

            if (hit == _active)
                return;

            _active = hit;

            foreach ((string region, Border target) in _targets)
                target.Classes.Set("active", region == hit);

            if (hit is null)
            {
                _preview.IsVisible = false;
                return;
            }

            Rect preview = PreviewBounds(hit, _surface, Share);
            SetLeft(_preview, preview.X);
            SetTop(_preview, preview.Y);
            _preview.Width = preview.Width;
            _preview.Height = preview.Height;
            _preview.IsVisible = true;
        }

        private string? HitTarget(Point position)
        {
            foreach ((string region, Border target) in _targets)
            {
                double left = GetLeft(target);
                double top = GetTop(target);

                if (position.X >= left && position.X <= left + TargetSize &&
                    position.Y >= top && position.Y <= top + TargetSize)
                {
                    return region;
                }
            }

            return null;
        }

        // Away from the diamond, the surface edges are themselves targets, so a
        // user who drags straight at an edge gets the obvious result.
        private string? EdgeRegion(Point position)
        {
            if (_surface.Width <= 0 || _surface.Height <= 0)
                return null;

            double edge = Math.Min(80, Math.Min(_surface.Width, _surface.Height) * 0.15);

            if (position.X < edge)
                return UiPanelRegion.Left;

            if (position.X > _surface.Width - edge)
                return UiPanelRegion.Right;

            if (position.Y < edge)
                return UiPanelRegion.Top;

            if (position.Y > _surface.Height - edge)
                return UiPanelRegion.Bottom;

            return null;
        }

        /// <summary>
        /// The geometry a drop into <paramref name="region"/> actually produces,
        /// from the same weights the surface lays the region out with.
        /// </summary>
        internal static Rect PreviewBounds(
            string region,
            Size surface,
            Func<string, double> share)
        {
            // The bands span the full width and the rails sit between them, so
            // a rail preview must not claim the height the bands already own.
            double top = surface.Height * share(UiPanelRegion.Top);
            double bottom = surface.Height * share(UiPanelRegion.Bottom);
            double bodyHeight = Math.Max(0, surface.Height - top - bottom);

            return region switch
            {
                UiPanelRegion.Left => new Rect(
                    0, top, surface.Width * share(UiPanelRegion.Left), bodyHeight),
                UiPanelRegion.Right => new Rect(
                    surface.Width * (1 - share(UiPanelRegion.Right)), top,
                    surface.Width * share(UiPanelRegion.Right), bodyHeight),
                UiPanelRegion.Top => new Rect(0, 0, surface.Width, top),
                UiPanelRegion.Bottom => new Rect(
                    0, surface.Height - bottom, surface.Width, bottom),
                _ => new Rect(
                    surface.Width * share(UiPanelRegion.Left), top,
                    surface.Width * (1 - share(UiPanelRegion.Left) - share(UiPanelRegion.Right)),
                    bodyHeight),
            };
        }

        private double Share(string region) =>
            _shares.TryGetValue(region, out double share) ? share : 0;

        /// <summary>
        /// Turns a region star weight against the centre into the fraction of
        /// the surface that region occupies.
        /// </summary>
        internal static double ShareFromWeight(double weight) =>
            weight <= 0 ? 0 : weight / (weight + UiPanelPlacement.DefaultSize);

        /// <summary>Hides the guide and reports the region under the pointer.</summary>
        public string? End()
        {
            string? region = _active;
            Cancel();
            return region;
        }

        public void Cancel()
        {
            _active = null;
            Opacity = 0;
            IsVisible = false;
            _preview.IsVisible = false;

            foreach (Border target in _targets.Values)
            {
                target.IsVisible = false;
                target.Classes.Remove("active");
            }
        }
    }
}
