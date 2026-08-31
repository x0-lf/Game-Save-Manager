using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;

namespace GameSaves.App.Views
{
    public partial class MainWindow : Window, IAppNavigationHost
    {
        // Marks the placeholder the sections submenu is built into, so the
        // handler finds it without depending on the menu item order.
        private const string SectionsMenuTag = "sections";

        private readonly TabDetachCoordinator _tabDetach = new(() => new DetachedWindow());

        // The rail's original tab order. Captured once because detaching
        // removes tabs from the TabControl; the Ctrl+1..9 shortcuts keep
        // addressing these slots so a shortcut means the same section
        // whether that tab is docked or floating.
        private readonly IReadOnlyList<TabItem> _navigationTabs;

        // Stable rail tab keys to TabItems, following the canonical creation
        // order that the persisted RailLayout keys address.
        private readonly Dictionary<string, TabItem> _tabsByKey;

        // The settings view model whose rail state is applied live. Null
        // until a MainWindowViewModel data context is attached.
        private SettingsViewModel? _railSettings;

        // The persisted startup tab is applied exactly once, after the rail
        // layout, and never on later rail edits.
        private bool _startupSelectionApplied;

        // The workspace-layout bridge handed to the settings view model once
        // this window (its owner, coordinator, and tab keys) exists.
        public IWorkspaceLayoutHost WorkspaceHost { get; }

        // Canonical creation-order tabs, for the workspace snapshot glue.
        internal IReadOnlyList<TabItem> NavigationTabs => _navigationTabs;

        public MainWindow()
        {
            InitializeComponent();

            _navigationTabs = MainNavigation.Items.OfType<TabItem>().ToArray();

            _tabsByKey = new Dictionary<string, TabItem>(StringComparer.Ordinal)
            {
                [UiRailLayoutSettings.TabDashboard] = _navigationTabs[0],
                [UiRailLayoutSettings.TabInstalledGames] = _navigationTabs[1],
                [UiRailLayoutSettings.TabProfiles] = _navigationTabs[2],
                [UiRailLayoutSettings.TabTransferPreview] = _navigationTabs[3],
                [UiRailLayoutSettings.TabManualBackup] = _navigationTabs[4],
                [UiRailLayoutSettings.TabBackups] = _navigationTabs[5],
                [UiRailLayoutSettings.TabSync] = _navigationTabs[6],
                [UiRailLayoutSettings.TabHistory] = _navigationTabs[7],
                [UiRailLayoutSettings.TabSettings] = _navigationTabs[8],
            };

            WorkspaceHost = new MainWindowWorkspaceLayoutHost(
                this,
                _tabDetach,
                _tabsByKey);

            // The keyboard shortcut gestures are declared in MainWindow.axaml;
            // their commands are attached here because they need the
            // navigation control and the detach coordinator, which are view
            // concerns. Digit bindings carry their slot as CommandParameter;
            // the Ctrl+Comma binding has none and opens Settings.
            ICommand selectTab = new RelayCommand<string>(SelectNavigationSlot);
            ICommand openSettings = new RelayCommand(
                () => _tabDetach.SelectOrActivate(MainNavigation, SettingsTab));
            foreach (KeyBinding binding in KeyBindings)
                binding.Command = binding.CommandParameter is null ? openSettings : selectTab;

            // The rail's layout and scan actions belong to whichever page is
            // selected, so they follow the selection rather than being wired
            // once.
            MainNavigation.SelectionChanged += (_, _) => UpdateRailChrome();
            UpdateRailChrome();

            // Shift+F10 / Menu key on a focused tab opens its context menu,
            // so the detach action is reachable without a mouse.
            MainNavigation.AddHandler(
                InputElement.KeyDownEvent,
                OnNavigationKeyDown,
                RoutingStrategies.Tunnel);

            // Below 900px the navigation rail collapses to icons; styles in
            // MainWindow.axaml key off the compactNav class.
            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 840;

                if (Classes.Contains("compactNav") != compact)
                {
                    if (compact)
                        Classes.Add("compactNav");
                    else
                        Classes.Remove("compactNav");
                }
            };

            // The persisted rail layout (position, collapse state, tab order,
            // tab visibility) is owned by the settings view model; apply it
            // whenever that state arrives or changes.
            DataContextChanged += OnDataContextChanged;
        }

        // Owned detached windows close together with this window; the
        // coordinator must not fight application shutdown by reattaching
        // tabs into a closing window.
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _tabDetach.NotifyOwnerClosing();
            base.OnClosing(e);
        }

        // The header gear button opens the Settings tab, which owns every
        // settings surface; there is no separate flyout anymore. If the tab
        // is currently floating, surface that window instead.
        private void OnSettingsClicked(object? sender, RoutedEventArgs e)
        {
            _tabDetach.SelectOrActivate(MainNavigation, SettingsTab);
        }

        private void OnTabDetachClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Control control &&
                control.GetVisualAncestors().OfType<TabItem>().FirstOrDefault() is { } tab)
            {
                _tabDetach.Detach(MainNavigation, tab, this);
            }
        }

        // Right-clicking a rail entry offers that page's sections, so section
        // visibility is reachable without opening Settings and without the rail
        // needing extra chrome — which is what keeps it working identically on
        // the left, right, and top. The list is built each time the menu opens,
        // because the layout it reflects can change from the panel menus too.
        private void OnTabContextMenuOpening(object? sender, CancelEventArgs e)
        {
            if (sender is not ContextMenu { PlacementTarget: TabItem tab } menu ||
                DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item =>
                    string.Equals(item.Tag as string, SectionsMenuTag, StringComparison.Ordinal)) is not { } sections)
            {
                return;
            }

            string? tabKey = _tabsByKey
                .FirstOrDefault(pair => ReferenceEquals(pair.Value, tab))
                .Key;

            IReadOnlyList<Control> items = BuildSectionItems(tabKey, viewModel);

            // A page with nothing hideable gets a disabled entry rather than an
            // empty submenu that looks broken.
            sections.IsEnabled = items.Count > 0;
            sections.ItemsSource = items.Count > 0 ? items : null;
        }

        /// <summary>
        /// One checked row per hideable section on a page, writing straight
        /// through to that page's live layout. Shared by the rail's layout menu
        /// and the rail tab's context menu, so the two routes into section
        /// visibility can never drift into offering different sections.
        /// </summary>
        private IReadOnlyList<Control> BuildSectionItems(
            string? tabKey,
            MainWindowViewModel viewModel)
        {
            if (tabKey is null)
                return Array.Empty<Control>();

            IReadOnlyList<WorkspacePanelDefinition> definitions =
                WorkspaceLayoutCatalog.PanelsFor(tabKey)
                    .Where(definition => definition.CanHide)
                    .ToArray();

            if (definitions.Count == 0)
                return Array.Empty<Control>();

            IWorkspaceLayoutPage layout = viewModel.WorkspacePageFor(tabKey);
            var items = new List<Control>();

            foreach (WorkspacePanelDefinition definition in definitions)
            {
                bool hidden = layout.Placements.Any(placement =>
                    string.Equals(placement.Key, definition.Key, StringComparison.Ordinal) &&
                    placement.Hidden);

                var item = new MenuItem
                {
                    Header = definition.Title,
                    Icon = new CheckBox
                    {
                        IsChecked = !hidden,
                        IsHitTestVisible = false,
                        Focusable = false,
                    },
                };

                string key = definition.Key;
                bool showing = !hidden;
                item.Click += (_, _) => layout.SetHidden(key, showing);

                // The row toggles, so it must announce what it will do rather
                // than always claiming to show the section.
                AutomationProperties.SetName(
                    item,
                    showing
                        ? $"Hide the {definition.Title} section"
                        : $"Show the {definition.Title} section");
                items.Add(item);
            }

            return items;
        }

        /// <summary>
        /// The page-level layout menu, opened from the navigation rail.
        ///
        /// This is the only route into a page whose sections are all hidden:
        /// the per-section menus live on panel headers, and in that state there
        /// are no headers left. Living on the rail also means it keeps working
        /// with the rail docked left, right, or top, and while the rail is
        /// collapsed to glyphs.
        /// </summary>
        private void OnRailLayoutClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control target ||
                DataContext is not MainWindowViewModel viewModel ||
                SelectedTabKey() is not { } tabKey ||
                !WorkspaceLayoutCatalog.Pages.Contains(tabKey))
            {
                return;
            }

            IWorkspaceLayoutPage layout = viewModel.WorkspacePageFor(tabKey);

            var reset = new MenuItem { Header = "Reset this page layout" };
            reset.Click += (_, _) => layout.ResetPage();
            AutomationProperties.SetName(
                reset, "Reset this page's layout to the default arrangement");

            var items = new List<Control> { reset };
            IReadOnlyList<Control> sections = BuildSectionItems(tabKey, viewModel);

            if (sections.Count > 0)
            {
                var showAll = new MenuItem { Header = "Show all sections" };
                showAll.Click += (_, _) =>
                {
                    foreach (WorkspacePanelDefinition definition in
                        WorkspaceLayoutCatalog.PanelsFor(tabKey))
                    {
                        layout.SetHidden(definition.Key, false);
                    }
                };
                AutomationProperties.SetName(
                    showAll, "Show every section on this page");

                items.Add(showAll);
                items.Add(new Separator());
                items.AddRange(sections);
            }

            new MenuFlyout { ItemsSource = items }.ShowAt(target);
        }

        private string? SelectedTabKey() =>
            _tabsByKey
                .FirstOrDefault(pair =>
                    ReferenceEquals(pair.Value, MainNavigation.SelectedItem))
                .Key;

        // The rail actions that depend on which page is selected. The layout
        // button is hidden on a page with no configurable layout, so the rail
        // never offers an action that would do nothing. The scan action needs
        // only the page key: the view model maps that to the page's own
        // refresh command and to the wording that describes it.
        private void UpdateRailChrome()
        {
            string? key = SelectedTabKey();

            RailLayoutButton.IsVisible =
                key is not null && WorkspaceLayoutCatalog.Pages.Contains(key);

            if (key is not null && DataContext is MainWindowViewModel viewModel)
                viewModel.ActiveTabKey = key;
        }

        // Context-menu path for detach (right-click or Shift+F10 on a tab).
        // The menu item lives in a popup, so the tab is resolved through the
        // context menu's placement target rather than visual ancestors.
        private void OnTabDetachMenuClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: TabItem tab } })
                _tabDetach.Detach(MainNavigation, tab, this);
        }

        // Ctrl+1..9 handler: select the tab when attached, surface its
        // floating window when detached, ignore out-of-range slots.
        private void SelectNavigationSlot(string? slot)
        {
            if (int.TryParse(slot, out int number))
                _tabDetach.SelectOrActivateSlot(MainNavigation, _navigationTabs, number);
        }

        private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;

            bool menuKey = e.Key == Key.Apps ||
                (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (!menuKey || e.Source is not Visual source)
                return;

            TabItem? tab = source as TabItem ??
                source.GetVisualAncestors().OfType<TabItem>().FirstOrDefault();
            if (tab?.ContextMenu is { } menu)
            {
                e.Handled = true;
                menu.Open(tab);
            }
        }

        // Rail layout plumbing: subscribe to the settings view model's rail
        // state when it arrives, unsubscribe when it is replaced, and apply
        // the full layout on every change (the apply is idempotent).
        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_railSettings is not null)
            {
                _railSettings.PropertyChanged -= OnRailSettingsPropertyChanged;
                _railSettings.RailTabs.CollectionChanged -= OnRailTabsChanged;

                foreach (RailTabOption option in _railSettings.RailTabs)
                    option.PropertyChanged -= OnRailTabOptionChanged;

                _railSettings.WorkspaceHost = null;
                _railSettings.NavigationHost = null;
                _railSettings = null;
            }

            if (DataContext is MainWindowViewModel { Settings: { } settings })
            {
                _railSettings = settings;
                settings.PropertyChanged += OnRailSettingsPropertyChanged;
                settings.RailTabs.CollectionChanged += OnRailTabsChanged;

                foreach (RailTabOption option in settings.RailTabs)
                    option.PropertyChanged += OnRailTabOptionChanged;

                // The workspace host is this window's own bridge; it is
                // assigned whenever the settings view model is attached so
                // snapshot/apply commands act on the live window.
                settings.WorkspaceHost = WorkspaceHost;
                settings.NavigationHost = this;
            }

            ApplyNavigationLayout();
            ApplyStartupSelection();

            // The startup selection only raises SelectionChanged when it moves
            // the selection, so push the rail's page-dependent state once here
            // for the case where the restored page is already the selected one.
            UpdateRailChrome();
        }

        private void OnRailSettingsPropertyChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SettingsViewModel.RailPosition)
                or nameof(SettingsViewModel.RailCollapsed))
            {
                ApplyNavigationLayout();
            }
        }

        private void OnRailTabsChanged(
            object? sender,
            NotifyCollectionChangedEventArgs e) => ApplyNavigationLayout();

        private void OnRailTabOptionChanged(
            object? sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RailTabOption.IsVisible))
                ApplyNavigationLayout();
        }

        // Reads the live rail state and applies it: placement, collapse
        // class, tab order, and tab visibility. Attached tabs are reordered
        // in place; detached tabs stay floating and reattach into this order
        // later. The detach coordinator owns the ItemCollection policy.
        private void ApplyNavigationLayout()
        {
            if (_railSettings is null)
                return;

            MainNavigation.TabStripPlacement = _railSettings.RailPosition switch
            {
                UiRailLayoutSettings.PositionRight => Dock.Right,
                UiRailLayoutSettings.PositionTop => Dock.Top,
                _ => Dock.Left,
            };

            // The rail's own chrome — the collapse toggle and the Scan action —
            // sits on the rail's edge, so it follows the rail to whichever side
            // it is on instead of staying stranded at the top left.
            RailChrome.HorizontalAlignment =
                _railSettings.RailPosition == UiRailLayoutSettings.PositionRight
                    ? Avalonia.Layout.HorizontalAlignment.Right
                    : Avalonia.Layout.HorizontalAlignment.Left;

            bool topRail = _railSettings.RailPosition == UiRailLayoutSettings.PositionTop;

            if (Classes.Contains("railTop") != topRail)
            {
                if (topRail)
                    Classes.Add("railTop");
                else
                    Classes.Remove("railTop");
            }

            bool collapsed = _railSettings.RailCollapsed;

            if (Classes.Contains("railCollapsed") != collapsed)
            {
                if (collapsed)
                    Classes.Add("railCollapsed");
                else
                    Classes.Remove("railCollapsed");
            }

            var orderedTabs = new List<TabItem>(_railSettings.RailTabs.Count);

            foreach (RailTabOption option in _railSettings.RailTabs)
            {
                if (_tabsByKey.TryGetValue(option.Key, out TabItem? tab))
                    orderedTabs.Add(tab);
            }

            var hiddenTabs = new List<TabItem>();

            foreach (RailTabOption option in _railSettings.RailTabs)
            {
                if (!option.IsVisible && _tabsByKey.TryGetValue(option.Key, out TabItem? tab))
                    hiddenTabs.Add(tab);
            }

            _tabDetach.ApplyTabLayout(MainNavigation, orderedTabs, hiddenTabs);
        }

        // The persisted startup tab, applied once after the first rail layout
        // and never again (later rail edits must not jump the selection).
        private void ApplyStartupSelection()
        {
            if (_startupSelectionApplied || _railSettings is null)
                return;

            _startupSelectionApplied = true;

            TabItem? startupTab = ResolveStartupTab(
                MainNavigation,
                _tabsByKey,
                _railSettings.StartupTabKey);

            if (startupTab is not null)
                MainNavigation.SelectedItem = startupTab;
        }

        // Picks the tab to open on: the saved startup tab when it is still
        // attached and visible, otherwise Dashboard (pinned visible by
        // construction), otherwise nothing. Static so the selection policy
        // is unit-testable without a windowing platform.
        internal static TabItem? ResolveStartupTab(
            TabControl navigation,
            IReadOnlyDictionary<string, TabItem> tabsByKey,
            string? startupTabKey)
        {
            if (IsSelectable(navigation, tabsByKey, startupTabKey, out TabItem? saved))
                return saved;

            if (IsSelectable(navigation, tabsByKey, UiRailLayoutSettings.TabDashboard, out TabItem? dashboard))
                return dashboard;

            return null;
        }

        private static bool IsSelectable(
            TabControl navigation,
            IReadOnlyDictionary<string, TabItem> tabsByKey,
            string? key,
            out TabItem? tab)
        {
            tab = key is not null &&
                tabsByKey.TryGetValue(key, out TabItem? candidate) &&
                navigation.Items.IndexOf(candidate) >= 0 &&
                candidate.IsVisible
                    ? candidate
                    : null;

            return tab is not null;
        }

        // Settings' provider rows route here. SelectOrActivate rather than a
        // plain selection, because the Sync section may have been floated into
        // its own window, in which case that window must be surfaced instead of
        // the click doing nothing. Selecting the provider kind is what reveals
        // its existing configuration panel; no capability is enabled here.
        public void ShowSyncProviderConfiguration(SyncProviderKind kind)
        {
            if (!_tabsByKey.TryGetValue(UiRailLayoutSettings.TabSync, out TabItem? syncTab))
                return;

            _tabDetach.SelectOrActivate(MainNavigation, syncTab);

            if (DataContext is MainWindowViewModel { Sync: { } sync })
                sync.SelectedProviderKind = kind;
        }

        // The rail's collapse toggle binds IsChecked straight to the settings
        // view model, which persists it and whose change notification
        // re-applies the layout, so no click handler is needed here.

        // Keeps a saved window placement on a visible screen when a layout is
        // applied: a saved top-left that still lies in some current working
        // area is clamped inside that area (and shrunk if it is wider); a
        // placement no screen contains cascades near the owner instead, so an
        // applied layout can never strand a window off-screen. Pure so the
        // policy is unit-testable without a windowing platform.
        internal static Rect ClampToScreens(
            Rect saved,
            IReadOnlyList<Rect> workingAreas,
            Rect ownerBounds,
            int cascadeIndex,
            out bool cascaded)
        {
            cascaded = false;

            Rect? target = null;

            foreach (Rect area in workingAreas)
            {
                if (area.Contains(saved.TopLeft))
                {
                    target = area;
                    break;
                }
            }

            if (target is null)
            {
                Point ownerCenter = new(
                    ownerBounds.X + ownerBounds.Width / 2,
                    ownerBounds.Y + ownerBounds.Height / 2);

                foreach (Rect area in workingAreas)
                {
                    if (area.Contains(ownerCenter))
                    {
                        target = area;
                        break;
                    }
                }
            }

            if (target is null)
            {
                cascaded = true;
                double offset = 24 * (cascadeIndex + 1);

                return new Rect(
                    ownerBounds.X + offset,
                    ownerBounds.Y + offset,
                    saved.Width,
                    saved.Height);
            }

            Rect screen = target.Value;
            double width = Math.Min(saved.Width, screen.Width);
            double height = Math.Min(saved.Height, screen.Height);
            double left = Math.Clamp(
                saved.X,
                screen.X,
                Math.Max(screen.X, screen.Right - width));
            double top = Math.Clamp(
                saved.Y,
                screen.Y,
                Math.Max(screen.Y, screen.Bottom - height));

            return new Rect(left, top, width, height);
        }
    }
}
