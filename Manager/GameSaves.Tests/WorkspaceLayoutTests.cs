using System.Xml.Linq;
using GameSaves.App.Models;
using GameSaves.App.Services;

namespace GameSaves.Tests;

// The workspace layout system: the immutable default catalog, the forgiving
// resolution of a saved layout onto it, and the persistence round trip. These
// are the guarantees that decide whether a user who has rearranged everything
// can still get their workspace back, so they are pinned here rather than left
// to the UI.
public sealed class WorkspaceLayoutTests
{
    private const string Dashboard = UiRailLayoutSettings.TabDashboard;

    [Fact]
    public void TheDefaultLayout_IsTheCatalog()
    {
        IReadOnlyList<UiPanelPlacement> resolved =
            WorkspaceLayoutCatalog.Resolve(Dashboard, saved: null);

        Assert.Equal(
            WorkspaceLayoutCatalog.PanelsFor(Dashboard).Select(panel => panel.Key),
            resolved.Select(placement => placement.Key));

        Assert.All(resolved, placement =>
        {
            Assert.False(placement.Hidden);
            Assert.False(placement.Collapsed);
            Assert.NotEqual(UiPanelRegion.Float, placement.Region);
        });
    }

    [Fact]
    public void EveryPanelDeclaredInXaml_ExistsInTheCatalog()
    {
        // A panel key in a view with no catalog entry resolves to nothing and
        // the section silently disappears from the page. That failure is
        // invisible in a build and easy to introduce, so it is a test.
        string[] declared = DeclaredPanelKeys();

        Assert.NotEmpty(declared);

        string[] known = WorkspaceLayoutCatalog.All
            .Select(definition => definition.Key)
            .ToArray();

        string[] orphaned = declared.Except(known).OrderBy(key => key).ToArray();

        Assert.True(
            orphaned.Length == 0,
            "Panel keys declared in XAML with no WorkspaceLayoutCatalog entry: " +
            string.Join(", ", orphaned));
    }

    [Fact]
    public void EveryCatalogPanel_IsDeclaredInXaml()
    {
        // The mirror of the test above: a catalog entry with no panel is a
        // section the user can be offered in Settings but never see.
        string[] declared = DeclaredPanelKeys();

        string[] missing = WorkspaceLayoutCatalog.All
            .Select(definition => definition.Key)
            .Except(declared)
            .OrderBy(key => key)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Catalog panels with no WorkspacePanel in any view: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void EveryCatalogPanelKey_IsPrefixedWithItsPage()
    {
        Assert.All(WorkspaceLayoutCatalog.All, definition =>
            Assert.StartsWith(definition.PageKey + ".", definition.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCatalogPage_IsAStableRailTabKey()
    {
        Assert.All(WorkspaceLayoutCatalog.Pages, page =>
            Assert.True(UiRailLayoutSettings.IsTabKey(page)));
    }

    [Fact]
    public void AnUnknownPanelKey_IsDroppedRatherThanShown()
    {
        UiPageLayout saved = Layout(
            Placement("dashboard.fromAnotherVersion", UiPanelRegion.Left, 0));

        IReadOnlyList<UiPanelPlacement> resolved =
            WorkspaceLayoutCatalog.Resolve(Dashboard, saved);

        Assert.DoesNotContain(
            resolved,
            placement => placement.Key == "dashboard.fromAnotherVersion");
        Assert.Equal(
            WorkspaceLayoutCatalog.PanelsFor(Dashboard).Count,
            resolved.Count);
    }

    [Fact]
    public void APanelMissingFromASavedLayout_FallsBackToItsDefault()
    {
        WorkspacePanelDefinition first = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[0];
        WorkspacePanelDefinition second = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[1];

        // Only the second panel was saved; a build that added the first one
        // later must still show it, at its default placement.
        UiPageLayout saved = Layout(
            Placement(second.Key, UiPanelRegion.Right, 0));

        UiPanelPlacement resolved = WorkspaceLayoutCatalog
            .Resolve(Dashboard, saved)
            .Single(placement => placement.Key == first.Key);

        Assert.Equal(first.Region, resolved.Region);
        Assert.False(resolved.Hidden);
    }

    [Fact]
    public void APinnedPanel_CannotBeHiddenByASavedLayout()
    {
        WorkspacePanelDefinition pinned = WorkspaceLayoutCatalog
            .PanelsFor(Dashboard)
            .First(definition => !definition.CanHide);

        UiPageLayout saved = Layout(
            Placement(pinned.Key, pinned.Region, 0) with { Hidden = true });

        UiPanelPlacement resolved = WorkspaceLayoutCatalog
            .Resolve(Dashboard, saved)
            .Single(placement => placement.Key == pinned.Key);

        Assert.False(resolved.Hidden);
    }

    [Fact]
    public void ResolvedOrders_AreDenseWithinEachRegion()
    {
        // Absurd and duplicated order values are what a hand-edited or
        // truncated file looks like; the result must still be one deterministic
        // arrangement rather than an ambiguous one.
        WorkspacePanelDefinition[] panels =
            WorkspaceLayoutCatalog.PanelsFor(Dashboard).ToArray();

        UiPageLayout saved = Layout(panels
            .Select(panel => Placement(panel.Key, UiPanelRegion.Left, 7))
            .ToArray());

        IReadOnlyList<UiPanelPlacement> resolved =
            WorkspaceLayoutCatalog.Resolve(Dashboard, saved);

        Assert.Equal(
            Enumerable.Range(0, panels.Length),
            resolved
                .Where(placement => placement.Region == UiPanelRegion.Left)
                .Select(placement => placement.Order));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    [InlineData(9999)]
    public void AnAbsurdPanelSize_NormalizesIntoRange(double size)
    {
        UiPanelPlacement? placement = UiPanelPlacement.TryCreate(
            "dashboard.intro", UiPanelRegion.Center, 0, size,
            collapsed: false, hidden: false, 0, 0, 400, 400);

        Assert.NotNull(placement);
        Assert.InRange(
            placement!.Size,
            UiPanelPlacement.MinSize,
            UiPanelPlacement.MaxSize);
    }

    [Fact]
    public void AnUnknownRegion_RejectsThePlacementEntirely()
    {
        Assert.Null(UiPanelPlacement.TryCreate(
            "dashboard.intro", "diagonal", 0, 1.0,
            collapsed: false, hidden: false, 0, 0, 400, 400));
    }

    [Fact]
    public void AnEmptyPanelKey_RejectsThePlacementEntirely()
    {
        Assert.Null(UiPanelPlacement.TryCreate(
            "  ", UiPanelRegion.Center, 0, 1.0,
            collapsed: false, hidden: false, 0, 0, 400, 400));
    }

    [Fact]
    public void AnUnknownPageKey_RejectsTheLayoutEntirely()
    {
        Assert.Null(UiPageLayout.TryCreate(
            "notAPage",
            new[] { Placement("dashboard.intro", UiPanelRegion.Center, 0) }));
    }

    [Fact]
    public void MovingAPanel_RenumbersBothRegionsDensely()
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        string moved = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[3].Key;
        page.MovePanel(moved, UiPanelRegion.Right, int.MaxValue);

        Assert.Equal(UiPanelRegion.Right, Find(page, moved).Region);

        foreach (string region in UiPanelRegion.DockedRegions)
        {
            int[] orders = page.Placements
                .Where(placement => placement.Region == region)
                .Select(placement => placement.Order)
                .ToArray();

            Assert.Equal(Enumerable.Range(0, orders.Length), orders);
        }
    }

    [Fact]
    public void ResettingAPage_RestoresTheDefaultAfterHeavyCustomization()
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        foreach (WorkspacePanelDefinition definition in
                 WorkspaceLayoutCatalog.PanelsFor(Dashboard))
        {
            page.MovePanel(definition.Key, UiPanelRegion.Bottom, int.MaxValue);
            page.SetCollapsed(definition.Key, true);

            if (definition.CanHide)
                page.SetHidden(definition.Key, true);
        }

        page.ResizeRegion(UiPanelRegion.Bottom, 3.5);
        page.ResetPage();

        Assert.Equal(
            WorkspaceLayoutCatalog.DefaultPlacements(Dashboard),
            page.Placements);
        Assert.Equal(
            WorkspacePage.DefaultRegionSize(UiPanelRegion.Bottom),
            page.RegionSize(UiPanelRegion.Bottom));
    }

    [Fact]
    public void ACustomizedLayout_SurvivesARestart()
    {
        var store = new InMemoryUiSettingsStore();
        string key = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[2].Key;

        WorkspacePage first = new WorkspaceLayoutService(store).Page(Dashboard);
        first.MovePanel(key, UiPanelRegion.Left, int.MaxValue);
        first.SetCollapsed(key, true);
        first.ResizeRegion(UiPanelRegion.Left, 0.75);

        // A second service over the same store is what the next launch sees.
        WorkspacePage restarted = new WorkspaceLayoutService(store).Page(Dashboard);

        UiPanelPlacement placement = Find(restarted, key);
        Assert.Equal(UiPanelRegion.Left, placement.Region);
        Assert.True(placement.Collapsed);
        Assert.Equal(0.75, restarted.RegionSize(UiPanelRegion.Left), 3);
    }

    [Fact]
    public void ACorruptSettingsFile_FallsBackToTheDefaultLayout()
    {
        var store = new InMemoryUiSettingsStore
        {
            Settings = AppUiSettings.Default with
            {
                WorkspacePages = new[]
                {
                    new UiPageLayout(
                        Dashboard,
                        new[]
                        {
                            // Everything about this entry is wrong except the key.
                            new UiPanelPlacement(
                                "dashboard.intro", "sideways", -3, double.NaN,
                                Collapsed: true, Hidden: true, 0, 0, 0, 0),
                        },
                        Array.Empty<UiRegionSize>()),
                },
            },
        };

        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        Assert.Equal(
            WorkspaceLayoutCatalog.PanelsFor(Dashboard).Count,
            page.Placements.Count);

        UiPanelPlacement intro = Find(page, "dashboard.intro");
        Assert.True(UiPanelRegion.IsRegion(intro.Region));
        Assert.False(intro.Hidden);
    }

    [Fact]
    public void ANamedLayout_CapturesAndReappliesEveryPage()
    {
        var store = new InMemoryUiSettingsStore();
        var service = new WorkspaceLayoutService(store);

        string key = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[1].Key;
        service.Page(Dashboard).MovePanel(key, UiPanelRegion.Top, int.MaxValue);

        IReadOnlyList<UiPageLayout> captured = service.Capture();

        service.ResetAll();
        Assert.NotEqual(UiPanelRegion.Top, Find(service.Page(Dashboard), key).Region);

        service.Apply(captured);
        Assert.Equal(UiPanelRegion.Top, Find(service.Page(Dashboard), key).Region);
    }

    [Fact]
    public void ApplyingALayout_ResetsPagesThatLayoutDoesNotMention()
    {
        var store = new InMemoryUiSettingsStore();
        var service = new WorkspaceLayoutService(store);

        string key = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[1].Key;
        service.Page(Dashboard).MovePanel(key, UiPanelRegion.Bottom, int.MaxValue);

        // An empty layout means "the default everywhere", not "leave it".
        service.Apply(Array.Empty<UiPageLayout>());

        Assert.Equal(
            WorkspaceLayoutCatalog.Find(Dashboard, key)!.Region,
            Find(service.Page(Dashboard), key).Region);
    }

    [Fact]
    public void ALayoutFile_CarriesNoPathsOrIdentifiers()
    {
        // Layout persistence must never become a place operational data leaks
        // to. The serialized form is checked against the panel vocabulary.
        var store = new InMemoryUiSettingsStore();
        var service = new WorkspaceLayoutService(store);
        service.Page(Dashboard).MovePanel(
            WorkspaceLayoutCatalog.PanelsFor(Dashboard)[0].Key,
            UiPanelRegion.Right,
            int.MaxValue);

        string json = System.Text.Json.JsonSerializer.Serialize(
            store.Settings.WorkspacePages);

        Assert.DoesNotContain(":\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScanAction_DefaultsToEverywhereAndPersistsWhatIsTurnedOff()
    {
        Assert.True(UiScanActionSettings.Default.ShowInNavigationRail);
        Assert.All(
            UiScanActionSettings.ScannablePages,
            page => Assert.True(UiScanActionSettings.Default.IsVisibleOn(page)));

        var settings = new UiScanActionSettings(
            ShowInNavigationRail: false,
            HiddenPages: new[] { UiRailLayoutSettings.TabInstalledGames });

        Assert.False(settings.IsVisibleOn(UiRailLayoutSettings.TabInstalledGames));
        Assert.True(settings.IsVisibleOn(UiRailLayoutSettings.TabDashboard));
    }

    [Fact]
    public void TheScanAction_DropsPagesThatOfferNoScan()
    {
        // A page key that has no scan action of its own cannot be "hidden";
        // keeping it would let a settings file grow entries the UI can never
        // show or clear.
        IReadOnlyList<string> normalized = UiScanActionSettings.NormalizeHiddenPages(
            new[]
            {
                UiRailLayoutSettings.TabSync,
                UiRailLayoutSettings.TabDashboard,
                UiRailLayoutSettings.TabDashboard,
                "notAPage",
            });

        Assert.Equal(new[] { UiRailLayoutSettings.TabDashboard }, normalized);
    }

    [Fact]
    public void SettingsSectionRows_TrackTheLayoutInBothDirections()
    {
        // Settings and the page's own panel menu edit one piece of state, so a
        // section hidden from either surface must read as hidden on the other.
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        WorkspacePanelDefinition definition = WorkspaceLayoutCatalog
            .PanelsFor(Dashboard)
            .First(candidate => candidate.CanHide);

        var group = new WorkspaceSectionGroup(Dashboard, "Dashboard", page);
        WorkspaceSectionOption row = group.Sections.Single(section =>
            section.Key == definition.Key);

        Assert.True(row.IsVisible);

        // Settings -> layout
        row.IsVisible = false;
        Assert.True(Find(page, definition.Key).Hidden);

        // Layout (the panel menu) -> Settings
        page.SetHidden(definition.Key, false);
        Assert.True(row.IsVisible);
    }

    [Fact]
    public void SettingsSectionRows_NeverOfferAPinnedSection()
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);
        var group = new WorkspaceSectionGroup(Dashboard, "Dashboard", page);

        foreach (WorkspaceSectionOption row in group.Sections)
            Assert.True(WorkspaceLayoutCatalog.Find(Dashboard, row.Key)!.CanHide);
    }

    [Fact]
    public void FloatingAPanel_RemembersWhereItCameFromAndReturnsThere()
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        WorkspacePanelDefinition definition = WorkspaceLayoutCatalog
            .PanelsFor(Dashboard)
            .First(candidate => candidate.CanFloat);

        // Move it somewhere that is NOT its catalog home first, so "returns
        // there" is a real claim and not an accident of the default.
        page.MovePanel(definition.Key, UiPanelRegion.Right, int.MaxValue);
        page.FloatPanel(definition.Key, 120, 80, 640, 520);

        UiPanelPlacement floating = Find(page, definition.Key);
        Assert.True(floating.IsFloating);
        Assert.Equal(120, floating.Left);
        Assert.Equal(80, floating.Top);
        Assert.Equal(640, floating.Width);
        Assert.Equal(520, floating.Height);

        page.DockPanel(definition.Key);

        UiPanelPlacement docked = Find(page, definition.Key);
        Assert.False(docked.IsFloating);
        Assert.Equal(UiPanelRegion.Right, docked.Region);

        // The bounds survive docking, so re-floating reopens where it was.
        Assert.Equal(640, docked.Width);
        Assert.Equal(520, docked.Height);
    }

    [Fact]
    public void AFloatingPanel_SurvivesARestartStillFloating()
    {
        var store = new InMemoryUiSettingsStore();
        string key = WorkspaceLayoutCatalog.PanelsFor(Dashboard)
            .First(definition => definition.CanFloat).Key;

        new WorkspaceLayoutService(store).Page(Dashboard)
            .FloatPanel(key, 200, 150, 700, 480);

        UiPanelPlacement restored =
            Find(new WorkspaceLayoutService(store).Page(Dashboard), key);

        Assert.True(restored.IsFloating);
        Assert.Equal(200, restored.Left);
        Assert.Equal(700, restored.Width);
    }

    [Fact]
    public void APanelTheCatalogPinsDown_CannotBeFloated()
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        WorkspacePanelDefinition? pinned = WorkspaceLayoutCatalog
            .PanelsFor(Dashboard)
            .FirstOrDefault(definition => !definition.CanFloat);

        if (pinned is null)
            return;

        page.FloatPanel(pinned.Key, 0, 0, 500, 500);

        Assert.False(Find(page, pinned.Key).IsFloating);
    }

    [Fact]
    public void TheDockPreview_ShowsTheGeometryADropActuallyProduces()
    {
        // A preview that shows one share while the drop produces another is
        // worse than no preview: it teaches the wrong thing about the control.
        // The assertions below are about the RELATIONSHIPS the layout has to
        // honour, not a restatement of the formula that produced them.
        var surface = new Avalonia.Size(1000, 800);
        var shares = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [UiPanelRegion.Left] = 0.4,
            [UiPanelRegion.Right] = 0.25,
            [UiPanelRegion.Top] = 0.2,
            [UiPanelRegion.Bottom] = 0.1,
        };

        double Share(string region) =>
            shares.TryGetValue(region, out double value) ? value : 0;

        Avalonia.Rect Preview(string region) =>
            GameSaves.App.Views.Workspace.WorkspaceDockOverlay
                .PreviewBounds(region, surface, Share);

        Avalonia.Rect top = Preview(UiPanelRegion.Top);
        Avalonia.Rect bottom = Preview(UiPanelRegion.Bottom);
        Avalonia.Rect left = Preview(UiPanelRegion.Left);
        Avalonia.Rect right = Preview(UiPanelRegion.Right);
        Avalonia.Rect centre = Preview(UiPanelRegion.Center);

        // The bands span the full width and sit against their own edge.
        Assert.Equal(surface.Width, top.Width, 1);
        Assert.Equal(0, top.Y, 1);
        Assert.Equal(surface.Width, bottom.Width, 1);
        Assert.Equal(surface.Height, bottom.Bottom, 1);

        // The rails live BETWEEN the bands, so they must not claim band space.
        Assert.Equal(top.Bottom, left.Y, 1);
        Assert.Equal(bottom.Y, left.Bottom, 1);
        Assert.Equal(left.Y, right.Y, 1);
        Assert.Equal(left.Bottom, right.Bottom, 1);

        // The rails sit against opposite edges and each takes its OWN share,
        // which is the bug this test exists for: one shared value was being
        // used for both.
        Assert.Equal(0, left.X, 1);
        Assert.Equal(surface.Width, right.Right, 1);
        Assert.NotEqual(left.Width, right.Width);

        // The centre is exactly what the rails leave behind.
        Assert.Equal(left.Right, centre.X, 1);
        Assert.Equal(right.X, centre.Right, 1);
        Assert.Equal(left.Y, centre.Y, 1);
        Assert.Equal(left.Bottom, centre.Bottom, 1);
    }

    [Fact]
    public void AnEmptyRegionsPreviewShare_ComesFromTheWeightADropWouldGiveIt()
    {
        // The share must be the fraction of the surface the region will take,
        // derived from its star weight against the centre.
        double share = GameSaves.App.Views.Workspace.WorkspaceDockOverlay
            .ShareFromWeight(WorkspacePage.DefaultRegionSize(UiPanelRegion.Left));

        Assert.InRange(share, 0.35, 0.45);
        Assert.Equal(0, GameSaves.App.Views.Workspace.WorkspaceDockOverlay.ShareFromWeight(0));
    }

    [Theory]
    // What a GridSplitter leaves behind: the new pixel extent, written into
    // the definition as its star value. Divided by the reference definition's
    // extent it is the proportion the user chose; stored raw it saturated at
    // MaxSize on the first drag, after which the region owned the whole page
    // and the equality guard made every later drag a silent no-op. A live
    // settings file was found holding history/left = 10 from exactly this.
    [InlineData(420.0, 680.0, 0.618)]
    [InlineData(680.0, 420.0, 1.619)]
    [InlineData(500.0, 500.0, 1.0)]
    public void AResizedRegion_StoresAProportionRatherThanAPixelExtent(
        double extent, double reference, double expected)
    {
        double weight = UiPanelPlacement.WeightFromExtent(extent, reference);

        Assert.Equal(expected, weight, 3);
        Assert.InRange(weight, UiPanelPlacement.MinSize, UiPanelPlacement.MaxSize);
        Assert.NotEqual(UiPanelPlacement.MaxSize, weight);
    }

    [Theory]
    [InlineData(double.NaN, 500.0)]
    [InlineData(500.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 500.0)]
    [InlineData(500.0, 0.0)]
    [InlineData(500.0, -1.0)]
    public void AnUnusableSplitterResult_FallsBackToTheDefaultWeight(
        double extent, double reference)
    {
        Assert.Equal(
            UiPanelPlacement.DefaultSize,
            UiPanelPlacement.WeightFromExtent(extent, reference));
    }

    [Theory]
    // A pixel magnitude from a splitter drag, a weight carried over from a
    // much wider display, and a collapsed-to-nothing rail. None may survive
    // restoration as-is: the first two pin the rail open and squeeze the
    // centre onto its minimum, the third makes the rail unusable. The expected
    // value is pinned rather than range-checked, because a clamp satisfies any
    // range by construction and would pass even sending everything one way.
    [InlineData(3840.0, UiRegionSize.MaxWeight)]
    [InlineData(10.0, UiRegionSize.MaxWeight)]
    [InlineData(0.0, UiRegionSize.MinWeight)]
    [InlineData(-5.0, UiRegionSize.MinWeight)]
    [InlineData(double.NaN, UiPanelPlacement.DefaultSize)]
    [InlineData(1.5, 1.5)]
    public void AnUnusableRegionWeight_IsClampedBackIntoTheUsableBand(
        double stored, double expected)
    {
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        page.ResizeRegion(UiPanelRegion.Left, stored);

        Assert.Equal(expected, page.RegionSize(UiPanelRegion.Left), 3);
    }

    [Fact]
    public void ASavedLayoutHoldingASaturatedWeight_ComesBackUsable()
    {
        // Exactly what a live settings file was found holding after one
        // splitter drag on the pre-fix build: the clamp ceiling itself, which
        // left the rail owning the page and the centre on its minimum.
        var store = new InMemoryUiSettingsStore
        {
            Settings = AppUiSettings.Default with
            {
                WorkspacePages = new[]
                {
                    new UiPageLayout(
                        Dashboard,
                        WorkspaceLayoutCatalog.DefaultPlacements(Dashboard),
                        new[] { new UiRegionSize(UiPanelRegion.Left, 10.0) }),
                },
            },
        };

        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        Assert.Equal(
            UiRegionSize.MaxWeight, page.RegionSize(UiPanelRegion.Left), 3);
    }

    [Fact]
    public void ResettingOnePage_LeavesEveryOtherPageAlone()
    {
        var store = new InMemoryUiSettingsStore();
        var service = new WorkspaceLayoutService(store);

        WorkspacePage dashboard = service.Page(Dashboard);
        WorkspacePage sync = service.Page(UiRailLayoutSettings.TabSync);

        string dashboardPanel = WorkspaceLayoutCatalog.PanelsFor(Dashboard)[2].Key;
        string syncPanel =
            WorkspaceLayoutCatalog.PanelsFor(UiRailLayoutSettings.TabSync)[2].Key;

        dashboard.MovePanel(dashboardPanel, UiPanelRegion.Left, int.MaxValue);
        sync.MovePanel(syncPanel, UiPanelRegion.Right, int.MaxValue);
        sync.SetHidden(syncPanel, true);
        sync.ResizeRegion(UiPanelRegion.Right, 0.5);

        dashboard.ResetPage();

        Assert.Equal(
            WorkspaceLayoutCatalog.DefaultPlacements(Dashboard),
            dashboard.Placements);

        // The other page keeps everything the user did to it.
        UiPanelPlacement kept = Find(sync, syncPanel);
        Assert.Equal(UiPanelRegion.Right, kept.Region);
        Assert.True(kept.Hidden);
        Assert.Equal(0.5, sync.RegionSize(UiPanelRegion.Right), 3);
    }

    [Fact]
    public void EverySectionOnAPage_ComesBackAfterHidingAllOfThem()
    {
        // The state the rail's layout menu has to recover from: nothing left
        // on the page to carry a per-section menu.
        var store = new InMemoryUiSettingsStore();
        WorkspacePage page = new WorkspaceLayoutService(store).Page(Dashboard);

        foreach (WorkspacePanelDefinition definition in
                 WorkspaceLayoutCatalog.PanelsFor(Dashboard))
        {
            page.SetHidden(definition.Key, true);
        }

        Assert.Contains(page.Placements, placement => placement.Hidden);

        // What "Show all sections" does.
        foreach (WorkspacePanelDefinition definition in
                 WorkspaceLayoutCatalog.PanelsFor(Dashboard))
        {
            page.SetHidden(definition.Key, false);
        }

        Assert.DoesNotContain(page.Placements, placement => placement.Hidden);

        // And it survives the restart, so recovery is not a session-only fix.
        WorkspacePage restarted = new WorkspaceLayoutService(store).Page(Dashboard);
        Assert.DoesNotContain(restarted.Placements, placement => placement.Hidden);
    }

    [Fact]
    public void EveryPageWithAConfigurableLayout_IsAKnownRailTab()
    {
        // The rail's layout action is shown only for these pages, so the two
        // lists have to agree or the button appears on a page it cannot serve.
        foreach (string pageKey in WorkspaceLayoutCatalog.Pages)
        {
            Assert.True(
                UiRailLayoutSettings.IsTabKey(pageKey),
                $"{pageKey} has a layout but is not a rail tab.");
            Assert.NotEmpty(WorkspaceLayoutCatalog.PanelsFor(pageKey));
        }

        // Settings has no movable sections, so the action must stay hidden there.
        Assert.DoesNotContain(
            UiRailLayoutSettings.TabSettings, WorkspaceLayoutCatalog.Pages);
    }

    private static UiPanelPlacement Find(IWorkspaceLayoutPage page, string key) =>
        page.Placements.Single(placement =>
            string.Equals(placement.Key, key, StringComparison.Ordinal));

    private static UiPanelPlacement Placement(string key, string region, int order) =>
        new(key, region, order, 1.0, Collapsed: false, Hidden: false, 0, 0, 480, 480);

    private static UiPageLayout Layout(params UiPanelPlacement[] panels) =>
        new(Dashboard, panels, Array.Empty<UiRegionSize>());

    // Every WorkspacePanel declared across the app's views, by PanelKey.
    private static string[] DeclaredPanelKeys()
    {
        string viewsRoot = Path.Combine(FindManagerRoot(), "GameSaves.App");

        return Directory
            .EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !HasSegment(path, "bin") && !HasSegment(path, "obj"))
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Where(element => element.Name.LocalName == "WorkspacePanel")
            .Select(element => (string?)element.Attribute("PanelKey"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static string FindManagerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Manager.sln by walking up from the test output directory.");
    }

    private sealed class InMemoryUiSettingsStore : IUiSettingsStore
    {
        public AppUiSettings Settings { get; set; } = AppUiSettings.Default;

        public string FilePath => "memory://ui-settings.json";

        public AppUiSettings Load() => Settings;

        public void Save(AppUiSettings settings) => Settings = settings;
    }
}
