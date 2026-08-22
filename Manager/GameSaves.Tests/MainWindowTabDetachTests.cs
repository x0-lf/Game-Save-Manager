using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using GameSaves.App.Services;
using GameSaves.App.Views;

namespace GameSaves.Tests;

public sealed class MainWindowTabDetachTests
{
    [Fact]
    public void EveryNavigationTabHeaderCarriesADetachButton()
    {
        XDocument view = XDocument.Load(FindView("MainWindow.axaml"));

        XElement tabControl = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "TabControl");

        XElement[] tabItems = tabControl
            .Elements()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToArray();

        Assert.Equal(9, tabItems.Length);

        foreach (XElement tab in tabItems)
        {
            XElement header = Assert.IsType<XElement>(
                Assert.Single(
                    tab.Elements(),
                    element => element.Name.LocalName == "TabItem.Header"));

            XElement button = Assert.Single(
                header.Descendants(),
                element => element.Name.LocalName == "Button");

            Assert.Equal("OnTabDetachClicked", (string?)button.Attribute("Click"));
            Assert.Contains(
                ((string?)button.Attribute("Classes") ?? string.Empty).Split(' '),
                value => value == "navDetach");
            Assert.Contains(
                button.Descendants(),
                element => (string?)element.Attribute("Text") == "&#xE8A7;"
                    || (string?)element.Attribute("Text") == "\uE8A7");
        }
    }

    [Fact]
    public void DetachRemovesTabAndMovesContentIntoWindow()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem tab = GetTab(navigation, 1);
        object content = tab.Content!;

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: null, showOwner: null);

        Assert.True(coordinator.IsDetached(tab));
        ItemCollection items = navigation.Items;
        Assert.Equal(2, items.Count);
        Assert.False(items.Contains(tab));
        Assert.Null(tab.Content);

        FakeDetachedWindow window = Assert.IsType<FakeDetachedWindow>(
            Assert.Single(coordinator.DetachedWindowsForTest()));
        Assert.Equal("B", window.Title);
        Assert.Same(content, window.Content);
    }

    [Fact]
    public void UserCloseReattachesTabAtOriginalIndexAndSelectsIt()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem tab = GetTab(navigation, 1);

        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);
        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: null, showOwner: null);

        window.SimulateClose();

        Assert.False(coordinator.IsDetached(tab));
        ItemCollection items = navigation.Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items.IndexOf(tab));
        Assert.Same(tab, navigation.SelectedItem);
        Assert.Null(window.Content);
        Assert.NotNull(tab.Content);
    }

    [Fact]
    public void ReattachClampsOriginalIndexWhenEarlierTabsAreDetached()
    {
        TabControl navigation = CreateNavigation("A", "B", "C", "D");
        TabItem first = GetTab(navigation, 0);
        TabItem last = GetTab(navigation, 3);

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.Detach(navigation, last, owner: null, ownerDataContext: null, showOwner: null);
        coordinator.Detach(navigation, first, owner: null, ownerDataContext: null, showOwner: null);

        ItemCollection items = navigation.Items;
        Assert.Equal(2, items.Count);

        // Reattaching "D" at recorded index 3 into a two-item collection
        // must clamp instead of throwing.
        coordinator.Reattach(navigation, last);
        items = navigation.Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.IndexOf(last));

        coordinator.Reattach(navigation, first);
        items = navigation.Items;
        Assert.Equal(4, items.Count);
        Assert.Equal(0, items.IndexOf(first));
        Assert.Equal(3, items.IndexOf(last));
    }

    [Fact]
    public void DetachingTheSameTabTwiceKeepsOneWindow()
    {
        TabControl navigation = CreateNavigation("A", "B");

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        TabItem tab = GetTab(navigation, 0);

        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: null, showOwner: null);
        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: null, showOwner: null);

        Assert.Single(coordinator.DetachedWindowsForTest());
        Assert.Single(navigation.Items.OfType<object>());
    }

    [Fact]
    public void ContentWithoutOwnDataContextInheritsOwnerDataContext()
    {
        // The Dashboard tab relies on inherited DataContext; detaching must
        // not leave its bindings orphaned in the floating window.
        TabControl navigation = CreateNavigation("A", "B");
        TabItem tab = GetTab(navigation, 0);
        object context = new();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: context, showOwner: null);

        FakeDetachedWindow window =
            Assert.IsType<FakeDetachedWindow>(Assert.Single(coordinator.DetachedWindowsForTest()));
        Assert.Same(context, window.DataContext);
    }

    [Fact]
    public void ContentWithOwnDataContextDoesNotInheritOwnerDataContext()
    {
        TabControl navigation = CreateNavigation("A", "B");
        TabItem tab = GetTab(navigation, 0);
        object ownContext = new();
        ((Control)tab.Content!).DataContext = ownContext;

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: new object(), showOwner: null);

        FakeDetachedWindow window =
            Assert.IsType<FakeDetachedWindow>(Assert.Single(coordinator.DetachedWindowsForTest()));
        Assert.Null(window.DataContext);
        Control moved = Assert.IsAssignableFrom<Control>(window.Content);
        Assert.Same(ownContext, moved.DataContext);
    }

    [Fact]
    public void CloseDuringOwnerShutdownDoesNotReattach()
    {
        TabControl navigation = CreateNavigation("A", "B");
        TabItem tab = GetTab(navigation, 0);

        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);
        coordinator.Detach(navigation, tab, owner: null, ownerDataContext: null, showOwner: null);

        coordinator.NotifyOwnerClosing();
        window.SimulateClose();

        // Shutdown must not reattach: the tab stays detached, the item count
        // stays reduced, and the content is never pulled back out.
        Assert.True(coordinator.IsDetached(tab));
        Assert.Single(navigation.Items.OfType<object>());
        Assert.Null(tab.Content);
    }

    // Ctrl+1..9 shortcut selection (accessibility slice B). Slot numbers are
    // the rail's original creation order, so a shortcut keeps meaning the
    // same section whether that tab is docked or floating.
    [Fact]
    public void SelectOrActivateSlotSelectsTheRequestedAttachedTab()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.SelectOrActivateSlot(
            navigation, navigation.Items.OfType<TabItem>().ToArray(), 3);

        Assert.Same(GetTab(navigation, 2), navigation.SelectedItem);
    }

    [Fact]
    public void SelectOrActivateSlotSurfacesADetachedTabWindowInstead()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] slots = navigation.Items.OfType<TabItem>().ToArray();

        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);
        coordinator.Detach(navigation, slots[1], owner: null, ownerDataContext: null, showOwner: null);

        coordinator.SelectOrActivateSlot(navigation, slots, 2);

        // The shortcut still addresses the same section: its floating window
        // is activated rather than a different tab being selected.
        Assert.True(window.WasActivated);
        Assert.DoesNotContain(slots[1], navigation.Items.OfType<object>());
    }

    [Fact]
    public void SelectOrActivateSlotIgnoresOutOfRangeSlots()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] slots = navigation.Items.OfType<TabItem>().ToArray();
        object? selectionBefore = navigation.SelectedItem;

        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);

        coordinator.SelectOrActivateSlot(navigation, slots, 0);
        coordinator.SelectOrActivateSlot(navigation, slots, 4);

        Assert.False(window.WasActivated);
        Assert.Empty(coordinator.DetachedWindowsForTest());
        Assert.Same(selectionBefore, navigation.SelectedItem);

        // The valid range still works after the ignored shortcuts.
        coordinator.SelectOrActivateSlot(navigation, slots, 2);
        Assert.Same(slots[1], navigation.SelectedItem);
    }

    // Rail layout slice (A6): order, per-tab visibility, and their interplay
    // with detach. Hidden tabs stay in the ItemCollection with
    // IsVisible=false, so reattachment, clamping, and canonical Ctrl+slot
    // numbering keep working unchanged.
    [Fact]
    public void ApplyTabLayout_ReordersAttachedItemsToTheGivenOrder()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        navigation.SelectedItem = tabs[1];
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.ApplyTabLayout(
            navigation,
            new[] { tabs[2], tabs[0], tabs[1] },
            Array.Empty<TabItem>());

        Assert.Equal(
            new[] { tabs[2], tabs[0], tabs[1] },
            navigation.Items.OfType<TabItem>().ToArray());

        // A still-visible selection survives the reorder.
        Assert.Same(tabs[1], navigation.SelectedItem);
    }

    [Fact]
    public void ApplyTabLayout_SkipsDetachedTabs_AndReattachInsertsByPersistedOrder()
    {
        TabControl navigation = CreateNavigation("A", "B", "C", "D");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.Detach(
            navigation, tabs[2], owner: null, ownerDataContext: null, showOwner: null);

        coordinator.ApplyTabLayout(
            navigation,
            new[] { tabs[3], tabs[2], tabs[0], tabs[1] },
            Array.Empty<TabItem>());

        // The detached tab is simply absent; the attached items follow the
        // persisted order.
        Assert.Equal(
            new[] { tabs[3], tabs[0], tabs[1] },
            navigation.Items.OfType<TabItem>().ToArray());

        coordinator.Reattach(navigation, tabs[2]);

        // Reattachment lands after every attached tab that precedes it in
        // the applied order (only D), so the rail matches the order.
        Assert.Equal(
            new[] { tabs[3], tabs[2], tabs[0], tabs[1] },
            navigation.Items.OfType<TabItem>().ToArray());
        Assert.Same(tabs[2], navigation.SelectedItem);
    }

    [Fact]
    public void ApplyTabLayout_HidesTabsInPlaceAndMovesSelectionOffThem()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        navigation.SelectedItem = tabs[0];
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.ApplyTabLayout(navigation, tabs, new[] { tabs[0] });

        // Hidden tabs keep their TabItem in the collection, flagged
        // invisible rather than removed.
        Assert.Equal(3, navigation.Items.Count);
        Assert.False(tabs[0].IsVisible);
        Assert.True(tabs[1].IsVisible);
        Assert.True(tabs[2].IsVisible);

        // A hidden tab cannot stay selected: the first visible attached tab
        // takes over, so the content area never sits on an invisible entry.
        Assert.Same(tabs[1], navigation.SelectedItem);
    }

    [Fact]
    public void ApplyTabLayout_RestoresVisibilityOfPreviouslyHiddenTabs()
    {
        TabControl navigation = CreateNavigation("A", "B");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        navigation.SelectedItem = tabs[0];
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        coordinator.ApplyTabLayout(navigation, tabs, new[] { tabs[1] });
        Assert.False(tabs[1].IsVisible);

        coordinator.ApplyTabLayout(navigation, tabs, Array.Empty<TabItem>());

        Assert.True(tabs[0].IsVisible);
        Assert.True(tabs[1].IsVisible);
    }

    [Fact]
    public void SelectOrActivateSlot_IsANoOpForHiddenTabs()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        navigation.SelectedItem = tabs[0];
        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);
        coordinator.ApplyTabLayout(navigation, tabs, new[] { tabs[1] });

        coordinator.SelectOrActivateSlot(navigation, tabs, 2);

        // The hidden middle tab's slot does nothing and the numbering never
        // shifts: slot 3 still selects the third tab.
        Assert.Same(tabs[0], navigation.SelectedItem);
        Assert.False(window.WasActivated);

        coordinator.SelectOrActivateSlot(navigation, tabs, 3);
        Assert.Same(tabs[2], navigation.SelectedItem);
    }

    [Fact]
    public void ATabHiddenWhileDetached_ReattachesInvisibleAndUnselected()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        FakeDetachedWindow window = new();
        TabDetachCoordinator coordinator = new(() => window);
        coordinator.Detach(
            navigation, tabs[1], owner: null, ownerDataContext: null, showOwner: null);
        coordinator.ApplyTabLayout(navigation, tabs, new[] { tabs[1] });
        navigation.SelectedItem = tabs[2];

        window.SimulateClose();

        Assert.Equal(3, navigation.Items.Count);
        Assert.Equal(1, navigation.Items.IndexOf(tabs[1]));
        Assert.False(tabs[1].IsVisible);

        // The hidden tab does not steal selection on reattach.
        Assert.Same(tabs[2], navigation.SelectedItem);
    }

    [Fact]
    public void DetachingTheSelectedTab_SelectsTheFirstVisibleAttachedTab()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.ApplyTabLayout(navigation, tabs, new[] { tabs[0] });
        navigation.SelectedItem = tabs[1];

        coordinator.Detach(
            navigation, tabs[1], owner: null, ownerDataContext: null, showOwner: null);

        // With a layout applied, the post-detach selection lands on the
        // first visible attached tab in order, skipping the hidden one.
        Assert.Same(tabs[2], navigation.SelectedItem);
    }

    // Workspace layouts (A7): explicit window placement on detach, snapshot
    // bounds capture, deterministic reattach-all, and the pure screen
    // clamping policy used when a layout is applied.
    [Fact]
    public void Detach_CanPlaceTheWindowAtExplicitBounds()
    {
        TabControl navigation = CreateNavigation("A", "B");
        TabItem tab = GetTab(navigation, 1);
        var bounds = new Rect(120, 64, 900, 600);

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.Detach(
            navigation,
            tab,
            owner: null,
            ownerDataContext: null,
            showOwner: null,
            bounds);

        FakeDetachedWindow window = Assert.IsType<FakeDetachedWindow>(
            Assert.Single(coordinator.DetachedWindowsForTest()));
        Assert.Equal(bounds, window.Bounds);
    }

    [Fact]
    public void GetDetachedBounds_ReturnsTheFloatingWindowPlacement()
    {
        TabControl navigation = CreateNavigation("A", "B");
        TabItem tab = GetTab(navigation, 0);
        var bounds = new Rect(-40, 10, 480, 360);

        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        // Not detached: a meaningless default, never a stale value.
        Assert.Equal(default, coordinator.GetDetachedBounds(tab));

        coordinator.Detach(
            navigation, tab, owner: null, ownerDataContext: null, showOwner: null);

        FakeDetachedWindow window = Assert.IsType<FakeDetachedWindow>(
            Assert.Single(coordinator.DetachedWindowsForTest()));
        window.Bounds = bounds;

        Assert.Equal(bounds, coordinator.GetDetachedBounds(tab));
    }

    [Fact]
    public void ReattachAll_ReattachesEveryDetachedTabInRailOrder()
    {
        TabControl navigation = CreateNavigation("A", "B", "C", "D");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.ApplyTabLayout(
            navigation,
            new[] { tabs[3], tabs[0], tabs[1], tabs[2] },
            Array.Empty<TabItem>());

        coordinator.Detach(
            navigation, tabs[1], owner: null, ownerDataContext: null, showOwner: null);
        coordinator.Detach(
            navigation, tabs[3], owner: null, ownerDataContext: null, showOwner: null);

        coordinator.ReattachAll(navigation);

        // Everything is attached again, in the applied rail order, so the
        // result is the same whatever order the tabs were detached in.
        Assert.Empty(coordinator.DetachedWindowsForTest());
        Assert.Equal(
            new[] { tabs[3], tabs[0], tabs[1], tabs[2] },
            navigation.Items.OfType<TabItem>().ToArray());
    }

    [Fact]
    public void ReattachAll_WithoutAnAppliedLayout_UsesOriginalIndexOrder()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());

        // Detach in non-index order; reattach-all still lands canonically.
        coordinator.Detach(
            navigation, tabs[2], owner: null, ownerDataContext: null, showOwner: null);
        coordinator.Detach(
            navigation, tabs[0], owner: null, ownerDataContext: null, showOwner: null);

        coordinator.ReattachAll(navigation);

        Assert.Equal(
            new[] { tabs[0], tabs[1], tabs[2] },
            navigation.Items.OfType<TabItem>().ToArray());
    }

    [Fact]
    public void ReattachAll_ThenDetachAgain_IsIdempotentForWorkspaceApply()
    {
        TabControl navigation = CreateNavigation("A", "B", "C");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();
        TabDetachCoordinator coordinator = new(() => new FakeDetachedWindow());
        coordinator.Detach(
            navigation, tabs[0], owner: null, ownerDataContext: null, showOwner: null);
        coordinator.Detach(
            navigation, tabs[2], owner: null, ownerDataContext: null, showOwner: null);

        // Applying a layout: reattach everything, then detach exactly the
        // layout's tabs at their bounds. The final state must depend only on
        // the applied layout, not on the previous detach state.
        coordinator.ReattachAll(navigation);

        var bounds = new Rect(10, 20, 800, 600);
        coordinator.Detach(
            navigation,
            tabs[1],
            owner: null,
            ownerDataContext: null,
            showOwner: null,
            bounds);

        Assert.Equal(
            new[] { tabs[0], tabs[2] },
            navigation.Items.OfType<TabItem>().ToArray());
        FakeDetachedWindow window = Assert.IsType<FakeDetachedWindow>(
            Assert.Single(coordinator.DetachedWindowsForTest()));
        Assert.Equal(bounds, window.Bounds);
    }

    [Fact]
    public void ClampToScreens_KeepsAnOnScreenPlacementInsideTheWorkingArea()
    {
        var screen = new Rect(0, 0, 1920, 1040);

        // Half-off the right and bottom edges: clamped back inside.
        Rect clamped = MainWindow.ClampToScreens(
            new Rect(1500, 900, 640, 480),
            new[] { screen },
            ownerBounds: new Rect(100, 100, 1200, 760),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.False(cascaded);
        Assert.Equal(new Rect(1280, 560, 640, 480), clamped);
    }

    [Fact]
    public void ClampToScreens_UnchangedWhenAlreadyInside()
    {
        var screen = new Rect(0, 0, 1920, 1040);
        var saved = new Rect(100, 100, 800, 600);

        Rect clamped = MainWindow.ClampToScreens(
            saved,
            new[] { screen },
            ownerBounds: new Rect(0, 0, 1200, 760),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.False(cascaded);
        Assert.Equal(saved, clamped);
    }

    [Fact]
    public void ClampToScreens_PicksTheScreenContainingTheSavedTopLeft()
    {
        Rect left = new(-1920, 0, 1920, 1040);
        Rect right = new(0, 0, 1920, 1040);

        Rect clamped = MainWindow.ClampToScreens(
            new Rect(-1920, 500, 640, 480),
            new[] { right, left },
            ownerBounds: new Rect(100, 100, 1200, 760),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.False(cascaded);
        Assert.Equal(new Rect(-1920, 500, 640, 480), clamped);
    }

    [Fact]
    public void ClampToScreens_OffAllScreens_FallsBackToTheOwnerScreen()
    {
        Rect left = new(-1920, 0, 1920, 1040);
        Rect right = new(0, 0, 1920, 1040);

        // The saved top-left (5000, 500) is on no screen; the owner lives on
        // the right screen, so the placement clamps there instead of
        // cascading.
        Rect clamped = MainWindow.ClampToScreens(
            new Rect(5000, 500, 640, 480),
            new[] { left, right },
            ownerBounds: new Rect(100, 100, 1200, 760),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.False(cascaded);
        Assert.Equal(new Rect(1280, 500, 640, 480), clamped);
    }

    [Fact]
    public void ClampToScreens_WithNoScreens_CascadesNearTheOwner()
    {
        Rect first = MainWindow.ClampToScreens(
            new Rect(-5000, -5000, 640, 480),
            Array.Empty<Rect>(),
            ownerBounds: new Rect(100, 100, 1200, 760),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.True(cascaded);
        Assert.Equal(new Rect(124, 124, 640, 480), first);

        // Successive cascades step diagonally so windows never stack.
        Rect second = MainWindow.ClampToScreens(
            new Rect(-5000, -5000, 640, 480),
            Array.Empty<Rect>(),
            ownerBounds: new Rect(100, 100, 1200, 760),
            cascadeIndex: 1,
            out cascaded);

        Assert.True(cascaded);
        Assert.Equal(new Rect(148, 148, 640, 480), second);
    }

    [Fact]
    public void ClampToScreens_ShrinksAWindowWiderThanItsScreen()
    {
        var screen = new Rect(0, 0, 1024, 768);

        Rect clamped = MainWindow.ClampToScreens(
            new Rect(200, 200, 4096, 4096),
            new[] { screen },
            ownerBounds: new Rect(0, 0, 800, 600),
            cascadeIndex: 0,
            out bool cascaded);

        Assert.False(cascaded);
        Assert.Equal(new Rect(0, 0, 1024, 768), clamped);
    }

    // A8 startup selection: the persisted startup tab is selected when it is
    // attached and visible, and anything else falls back to the Dashboard.
    private static (TabControl Navigation, Dictionary<string, TabItem> TabsByKey)
        CreateKeyedNavigation()
    {
        TabControl navigation = CreateNavigation(
            "Dashboard", "Sync", "Settings");
        TabItem[] tabs = navigation.Items.OfType<TabItem>().ToArray();

        var tabsByKey = new Dictionary<string, TabItem>(StringComparer.Ordinal)
        {
            [UiRailLayoutSettings.TabDashboard] = tabs[0],
            [UiRailLayoutSettings.TabSync] = tabs[1],
            [UiRailLayoutSettings.TabSettings] = tabs[2],
        };

        return (navigation, tabsByKey);
    }

    [Fact]
    public void ResolveStartupTab_SelectsTheSavedVisibleTab()
    {
        (TabControl navigation, var tabsByKey) = CreateKeyedNavigation();

        TabItem resolved = Assert.IsType<TabItem>(MainWindow.ResolveStartupTab(
            navigation,
            tabsByKey,
            UiRailLayoutSettings.TabSync));

        navigation.SelectedItem = resolved;

        Assert.Same(tabsByKey[UiRailLayoutSettings.TabSync], navigation.SelectedItem);
    }

    [Fact]
    public void ResolveStartupTab_FallsBackToDashboardForUnknownKeys()
    {
        (TabControl navigation, var tabsByKey) = CreateKeyedNavigation();

        foreach (string? unknown in new[] { "achievements", "", null })
        {
            Assert.Same(
                tabsByKey[UiRailLayoutSettings.TabDashboard],
                MainWindow.ResolveStartupTab(navigation, tabsByKey, unknown));
        }
    }

    [Fact]
    public void ResolveStartupTab_FallsBackToDashboardWhenTheSavedTabIsHidden()
    {
        (TabControl navigation, var tabsByKey) = CreateKeyedNavigation();
        tabsByKey[UiRailLayoutSettings.TabSync].IsVisible = false;

        Assert.Same(
            tabsByKey[UiRailLayoutSettings.TabDashboard],
            MainWindow.ResolveStartupTab(
                navigation,
                tabsByKey,
                UiRailLayoutSettings.TabSync));
    }

    [Fact]
    public void ResolveStartupTab_FallsBackToDashboardWhenTheSavedTabIsDetached()
    {
        (TabControl navigation, var tabsByKey) = CreateKeyedNavigation();
        navigation.Items.Remove(tabsByKey[UiRailLayoutSettings.TabSync]);

        Assert.Same(
            tabsByKey[UiRailLayoutSettings.TabDashboard],
            MainWindow.ResolveStartupTab(
                navigation,
                tabsByKey,
                UiRailLayoutSettings.TabSync));
    }

    private static TabControl CreateNavigation(params string[] labels)
    {
        TabControl navigation = new();
        List<TabItem> tabs = new();

        foreach (string label in labels)
        {
            TabItem tab = new()
            {
                Header = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = label, Classes = { "navLabel" } },
                    },
                },
                Content = new ContentControl(),
            };

            tabs.Add(tab);
        }

        foreach (TabItem tab in tabs)
            navigation.Items.Add(tab);

        return navigation;
    }

    private static TabItem GetTab(TabControl navigation, int index) =>
        Assert.IsType<TabItem>(navigation.Items.OfType<object>().ElementAt(index));

    private static string FindView(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return Path.Combine(directory.FullName, "GameSaves.App", "Views", fileName);

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }

    private sealed class FakeDetachedWindow : IDetachedTabWindow
    {
        public string? Title { get; set; }

        public object? Content { get; set; }

        public object? DataContext { get; set; }

        public Rect Bounds { get; set; }

        public Window? Owner { get; private set; }

        public bool WasActivated { get; private set; }

        public event EventHandler? CloseRequested;

        public void Show(Window? owner) => Owner = owner;

        public void Activate() => WasActivated = true;

        public void SimulateClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
