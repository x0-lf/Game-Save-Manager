using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using GameSaves.App.Services;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// How a panel claims space inside its region. Content-height panels flow
    /// and scroll, exactly as the app's stacked cards already do; filling
    /// panels absorb the slack and are the ones a splitter can resize.
    /// </summary>
    public enum WorkspacePanelSizeMode
    {
        Auto,
        Fill,
    }

    /// <summary>
    /// One movable unit of a page: a card that carries the app's existing
    /// chrome plus a header that doubles as a drag handle and hosts the
    /// collapse chevron and the panel menu.
    ///
    /// A panel is a meaningful page section — a list pane, a results table, a
    /// form group — never an individual label or button. Its
    /// <see cref="PanelKey"/> is the stable identity that a saved layout
    /// records; it must match a key declared in
    /// <see cref="WorkspaceLayoutCatalog"/>, which owns the immutable default
    /// placement for every panel in the app.
    /// </summary>
    public class WorkspacePanel : ContentControl
    {
        public static readonly StyledProperty<string> PanelKeyProperty =
            AvaloniaProperty.Register<WorkspacePanel, string>(
                nameof(PanelKey), string.Empty);

        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<WorkspacePanel, string>(
                nameof(Title), string.Empty);

        public static readonly StyledProperty<string?> SubtitleProperty =
            AvaloniaProperty.Register<WorkspacePanel, string?>(nameof(Subtitle));

        /// <summary>
        /// An optional Segoe Fluent Icons glyph shown before the title. Used by
        /// the sections whose card already led with one — the Steam-missing
        /// banner, the sync warnings — so converting them to panels does not
        /// quietly drop their icon.
        /// </summary>
        public static readonly StyledProperty<string?> GlyphProperty =
            AvaloniaProperty.Register<WorkspacePanel, string?>(nameof(Glyph));

        /// <summary>
        /// Section-scoped actions shown in the header, to the left of the
        /// collapse and menu buttons. These are the page's own buttons — the
        /// panel never invents chrome the section did not already have.
        /// </summary>
        public static readonly StyledProperty<object?> HeaderActionsProperty =
            AvaloniaProperty.Register<WorkspacePanel, object?>(nameof(HeaderActions));

        public static readonly StyledProperty<bool> IsCollapsedProperty =
            AvaloniaProperty.Register<WorkspacePanel, bool>(nameof(IsCollapsed));

        public static readonly StyledProperty<bool> CanCollapseProperty =
            AvaloniaProperty.Register<WorkspacePanel, bool>(nameof(CanCollapse), true);

        public static readonly StyledProperty<bool> CanHideProperty =
            AvaloniaProperty.Register<WorkspacePanel, bool>(nameof(CanHide), true);

        public static readonly StyledProperty<bool> CanFloatProperty =
            AvaloniaProperty.Register<WorkspacePanel, bool>(nameof(CanFloat), true);

        /// <summary>
        /// The smallest the panel may be squeezed to in its region. Region
        /// minimums are the largest minimum among the panels docked there, so
        /// a drag can never crush a pane past legibility on a 1080p display.
        /// </summary>
        public static readonly StyledProperty<double> MinPanelWidthProperty =
            AvaloniaProperty.Register<WorkspacePanel, double>(
                nameof(MinPanelWidth), 240);

        public static readonly StyledProperty<double> MinPanelHeightProperty =
            AvaloniaProperty.Register<WorkspacePanel, double>(
                nameof(MinPanelHeight), 96);

        /// <summary>
        /// The width this panel would like when it shares a row with its
        /// siblings in a flowing region. Left unset (NaN) the panel takes a
        /// full row, which is what a page-header card or a banner wants.
        /// </summary>
        public static readonly StyledProperty<double> PreferredWidthProperty =
            AvaloniaProperty.Register<WorkspacePanel, double>(
                nameof(PreferredWidth), double.NaN);

        /// <summary>
        /// The widest this panel should ever be made when it is the only thing
        /// in a side rail. Left unset (NaN) the rail simply takes its share.
        /// Used by the pages whose original layout capped a form column so it
        /// could not stretch past its reading measure.
        /// </summary>
        public static readonly StyledProperty<double> MaxPanelWidthProperty =
            AvaloniaProperty.Register<WorkspacePanel, double>(
                nameof(MaxPanelWidth), double.NaN);

        public static readonly StyledProperty<WorkspacePanelSizeMode> SizeModeProperty =
            AvaloniaProperty.Register<WorkspacePanel, WorkspacePanelSizeMode>(
                nameof(SizeMode), WorkspacePanelSizeMode.Auto);

        /// <summary>
        /// The region the surface has placed this panel in. Set by
        /// <see cref="WorkspaceSurface"/> only; the panel menu reads it to
        /// mark the current region and to disable the no-op moves.
        /// </summary>
        public static readonly DirectProperty<WorkspacePanel, string> RegionProperty =
            AvaloniaProperty.RegisterDirect<WorkspacePanel, string>(
                nameof(Region),
                panel => panel.Region);

        private string _region = UiPanelRegion.Center;

        static WorkspacePanel()
        {
            IsCollapsedProperty.Changed.AddClassHandler<WorkspacePanel>(
                (panel, _) => panel.UpdatePseudoClasses());

            // A panel is a landmark: a screen reader should announce the
            // section by name when focus enters it, and should say whether its
            // content is currently folded away.
            TitleProperty.Changed.AddClassHandler<WorkspacePanel>(
                (panel, _) => panel.UpdateAutomation());

            // Whether the panel has a body at all. This is a pseudo-class and
            // not a binding on the content presenter for one decisive reason:
            // a {Binding} inside a ControlTemplate applies at LocalValue
            // priority, which outranks every Style setter — including the one
            // that hides the body when the panel is collapsed. Expressing both
            // as pseudo-classes puts them in the same tier, so collapsing
            // actually collapses.
            ContentProperty.Changed.AddClassHandler<WorkspacePanel>(
                (panel, _) => panel.UpdatePseudoClasses());
        }

        public WorkspacePanel()
        {
            UpdatePseudoClasses();
        }

        public string PanelKey
        {
            get => GetValue(PanelKeyProperty);
            set => SetValue(PanelKeyProperty, value);
        }

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string? Subtitle
        {
            get => GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public string? Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public object? HeaderActions
        {
            get => GetValue(HeaderActionsProperty);
            set => SetValue(HeaderActionsProperty, value);
        }

        public bool IsCollapsed
        {
            get => GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        public bool CanCollapse
        {
            get => GetValue(CanCollapseProperty);
            set => SetValue(CanCollapseProperty, value);
        }

        public bool CanHide
        {
            get => GetValue(CanHideProperty);
            set => SetValue(CanHideProperty, value);
        }

        public bool CanFloat
        {
            get => GetValue(CanFloatProperty);
            set => SetValue(CanFloatProperty, value);
        }

        public double MinPanelWidth
        {
            get => GetValue(MinPanelWidthProperty);
            set => SetValue(MinPanelWidthProperty, value);
        }

        public double MinPanelHeight
        {
            get => GetValue(MinPanelHeightProperty);
            set => SetValue(MinPanelHeightProperty, value);
        }

        public double PreferredWidth
        {
            get => GetValue(PreferredWidthProperty);
            set => SetValue(PreferredWidthProperty, value);
        }

        public double MaxPanelWidth
        {
            get => GetValue(MaxPanelWidthProperty);
            set => SetValue(MaxPanelWidthProperty, value);
        }

        public WorkspacePanelSizeMode SizeMode
        {
            get => GetValue(SizeModeProperty);
            set => SetValue(SizeModeProperty, value);
        }

        /// <summary>
        /// Raised when the panel menu button is pressed. The surface builds the
        /// menu, because the set of legal moves depends on the page's layout,
        /// not on the panel.
        /// </summary>
        internal event EventHandler? MenuRequested;

        internal void RequestMenu() => MenuRequested?.Invoke(this, EventArgs.Empty);

        public string Region
        {
            get => _region;
            internal set
            {
                if (SetAndRaise(RegionProperty, ref _region, value))
                    UpdatePseudoClasses();
            }
        }

        /// <summary>
        /// The header element, resolved from the template. The surface listens
        /// on it for the drag gesture, so the drag handle is the header and
        /// never the panel body.
        /// </summary>
        internal Control? HeaderHandle { get; private set; }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            HeaderHandle = e.NameScope.Find<Control>("PART_Header");

            if (e.NameScope.Find<Button>("PART_MenuButton") is { } menu)
                menu.Click += (_, _) => RequestMenu();
        }

        /// <summary>
        /// Marks the panel as the one currently being dragged, so the theme can
        /// fade it while the docking guide is showing. Owned by
        /// <see cref="WorkspaceSurface"/>.
        /// </summary>
        internal void SetDragging(bool dragging) =>
            PseudoClasses.Set(":dragging", dragging);

        private void UpdatePseudoClasses()
        {
            PseudoClasses.Set(":collapsed", IsCollapsed);
            PseudoClasses.Set(":floating", Region == UiPanelRegion.Float);
            PseudoClasses.Set(":bodyless", Content is null);
            UpdateAutomation();
        }

        private void UpdateAutomation()
        {
            if (string.IsNullOrEmpty(Title))
                return;

            AutomationProperties.SetName(this, Title);
            AutomationProperties.SetHelpText(
                this,
                IsCollapsed
                    ? "Section, collapsed. Use the section layout menu to move, expand or hide it."
                    : "Section. Use the section layout menu to move, collapse, float or hide it.");
        }
    }
}
