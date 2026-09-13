using System.Xml.Linq;
using GameSaves.App.Services;
using GameSaves.App.Views;
using GameSaves.App.Views.Workspace;

namespace GameSaves.Tests;

/// <summary>
/// Verifies the navigation rail redesign (OBS-020):
/// 1. Rail chrome internalizes Collapse, Scan, Layout, and direct Reset Page Layout controls.
/// 2. Outer rail border encompasses action buttons in Left, Right, and Top docks.
/// 3. Top dock renders a deliberate two-line layout (Line 1: actions with subtle divider, Line 2: tabs).
/// 4. Direct Reset Page Layout restores default panel arrangements.
/// </summary>
public sealed class NavigationRailRedesignTests
{
    [Fact]
    public void MainWindow_DeclaresResetPageLayoutButtonInRailChrome()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement chrome = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "StackPanel" &&
                Named(element, "RailChrome"));

        XElement reset = Assert.Single(
            chrome.Descendants(),
            element => element.Name.LocalName == "Button" &&
                Named(element, "RailResetLayoutButton"));

        Assert.Equal("railChrome", (string?)reset.Attribute("Classes"));
        Assert.Equal("OnRailResetLayoutClicked", (string?)reset.Attribute("Click"));
        Assert.Equal("Reset page layout", (string?)reset.Attribute("AutomationProperties.Name"));
        Assert.Equal(
            "Reset this page's layout to the default arrangement",
            (string?)reset.Attribute("AutomationProperties.HelpText"));
        Assert.Equal(
            "Reset page layout — restore default panel arrangement",
            (string?)reset.Attribute("ToolTip.Tip"));

        XElement glyph = Assert.Single(
            reset.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Classes"))?.Contains("railChromeGlyph") == true);

        Assert.True(
            (string?)glyph.Attribute("Text") == "\uE777" ||
            (string?)glyph.Attribute("Text") == "&#xE777;");

        XElement label = Assert.Single(
            reset.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Classes"))?.Contains("railChromeLabel") == true);

        Assert.Equal("Reset", (string?)label.Attribute("Text"));
    }

    [Fact]
    public void MainWindow_RailBorderEncompassesActionsAndTabsAcrossDockPositions()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement rail = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "Border" &&
                Named(element, "PART_NavigationRail"));

        Assert.Contains("NavigationSurfaceBrush", (string?)rail.Attribute("Background"));
        Assert.Contains("SubtleBorderBrush", (string?)rail.Attribute("BorderBrush"));
        Assert.Equal("0,0,1,0", (string?)rail.Attribute("BorderThickness"));

        // Both chrome actions and tab items are inside PART_NavigationRail
        XElement stack = Assert.Single(
            rail.Elements(),
            element => element.Name.LocalName == "StackPanel" &&
                (string?)element.Attribute("Orientation") == "Vertical");

        Assert.Equal(
            new[] { "ContentPresenter", "ItemsPresenter" },
            stack.Elements().Select(element => element.Name.LocalName).ToArray());

        XElement chromeHost = stack.Elements().First();
        Assert.True(Named(chromeHost, "PART_RailChromeHost"));
        Assert.Equal("{TemplateBinding Tag}", (string?)chromeHost.Attribute("Content"));

        // Verify border thickness styles across all dock orientations
        var styles = view.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => new
            {
                Selector = (string?)element.Attribute("Selector"),
                Element = element
            })
            .ToArray();

        // Right dock border (thickness 1,0,0,0 on left edge)
        Assert.Contains(styles, s =>
            s.Selector == "TabControl#MainNavigation[TabStripPlacement=Right] /template/ Border#PART_NavigationRail" &&
            FindSetterValue(s.Element, "BorderThickness") == "1,0,0,0");

        Assert.Contains(styles, s =>
            s.Selector == "Window.railRight TabControl#MainNavigation /template/ Border#PART_NavigationRail" &&
            FindSetterValue(s.Element, "BorderThickness") == "1,0,0,0");

        // Top dock border (thickness 0,0,0,1 on bottom edge)
        Assert.Contains(styles, s =>
            s.Selector == "TabControl#MainNavigation[TabStripPlacement=Top] /template/ Border#PART_NavigationRail" &&
            FindSetterValue(s.Element, "BorderThickness") == "0,0,0,1");

        Assert.Contains(styles, s =>
            s.Selector == "Window.railTop TabControl#MainNavigation /template/ Border#PART_NavigationRail" &&
            FindSetterValue(s.Element, "BorderThickness") == "0,0,0,1");
    }

    [Fact]
    public void MainWindow_TopDockRendersTwoLinesWithDistinctActionAndTabStrips()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        var styles = view.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => new
            {
                Selector = (string?)element.Attribute("Selector"),
                Element = element
            })
            .ToArray();

        // Line 1 divider under actions in Top dock
        Assert.Contains(styles, s =>
            s.Selector == "Window.railTop TabControl#MainNavigation /template/ ContentPresenter#PART_RailChromeHost" &&
            FindSetterValue(s.Element, "BorderThickness") == "0,0,0,1" &&
            FindSetterValue(s.Element, "BorderBrush")?.Contains("SubtleBorderBrush") == true);

        Assert.Contains(styles, s =>
            s.Selector == "TabControl#MainNavigation[TabStripPlacement=Top] /template/ ContentPresenter#PART_RailChromeHost" &&
            FindSetterValue(s.Element, "BorderThickness") == "0,0,0,1");

        // Line 2 horizontal WrapPanel tab strip in Top dock
        var topPanelStyle = Assert.Single(styles, s =>
            s.Selector == "Window.railTop TabControl#MainNavigation");

        XElement panelSetter = Assert.Single(
            topPanelStyle.Element.Descendants(),
            e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "ItemsPanel");

        XElement wrapPanel = Assert.Single(
            panelSetter.Descendants(),
            e => e.Name.LocalName == "WrapPanel");

        Assert.Equal("Horizontal", (string?)wrapPanel.Attribute("Orientation"));

        // Top dock tab item geometry (horizontal strip item spacing)
        var tabItemStyle = Assert.Single(styles, s =>
            s.Selector == "Window.railTop TabControl#MainNavigation TabItem");

        Assert.Equal("4,3", FindSetterValue(tabItemStyle.Element, "Margin"));
        Assert.Equal("10,6", FindSetterValue(tabItemStyle.Element, "Padding"));
        Assert.Equal("34", FindSetterValue(tabItemStyle.Element, "Height"));
        Assert.Equal("Left", FindSetterValue(tabItemStyle.Element, "HorizontalContentAlignment"));
    }

    [Fact]
    public void MainWindow_CollapsedRailStylesHideLabelsForLayoutAndResetButtons()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        var styles = view.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => new
            {
                Selector = (string?)element.Attribute("Selector"),
                Element = element
            })
            .ToArray();

        var collapsedChrome = Assert.Single(styles, s =>
            s.Selector == "Window.railCollapsed StackPanel#RailChrome TextBlock.railChromeLabel");

        Assert.Equal("False", FindSetterValue(collapsedChrome.Element, "IsVisible"));
    }

    [Fact]
    public void MainWindow_IsPageLayoutConfigurable_MatchesWorkspaceLayoutCatalog()
    {
        foreach (string page in WorkspaceLayoutCatalog.Pages)
        {
            Assert.True(
                MainWindow.IsPageLayoutConfigurable(page),
                $"Page '{page}' is registered in WorkspaceLayoutCatalog and must be configurable.");
        }

        Assert.False(MainWindow.IsPageLayoutConfigurable("settings"));
        Assert.False(MainWindow.IsPageLayoutConfigurable("unknownPage"));
        Assert.False(MainWindow.IsPageLayoutConfigurable(null));
    }

    [Fact]
    public void WorkspaceLayout_ResetPage_RestoresDefaultPlacementsDirectly()
    {
        string pageKey = "dashboard";
        IReadOnlyList<WorkspacePanelDefinition> definitions = WorkspaceLayoutCatalog.PanelsFor(pageKey);
        Assert.NotEmpty(definitions);

        // Mutate page layout: hide the first panel that can be hidden
        WorkspacePanelDefinition hideable = Assert.Single(
            definitions.Where(d => d.CanHide).Take(1));

        var store = new InMemoryUiSettingsStore();
        WorkspaceLayoutService service = new(store);
        IWorkspaceLayoutPage page = service.Page(pageKey);

        // Hide the panel
        page.SetHidden(hideable.Key, true);
        Assert.True(page.Placements.First(p => p.Key == hideable.Key).Hidden);

        // Direct reset restores defaults
        page.ResetPage();
        Assert.False(page.Placements.First(p => p.Key == hideable.Key).Hidden);

        // Verify order and placements match default catalog placements
        Assert.Equal(
            WorkspaceLayoutCatalog.DefaultPlacements(pageKey),
            page.Placements);
    }

    private static string? FindSetterValue(XElement styleElement, string propertyName)
    {
        XElement? setter = styleElement
            .Descendants()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Setter" &&
                (string?)e.Attribute("Property") == propertyName);

        return (string?)setter?.Attribute("Value");
    }

    private static bool Named(XElement element, string name) =>
        element.Attributes().Any(a =>
            a.Name.LocalName == "Name" && a.Value == name);

    private static string FindAppFile(string folder, string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return Path.Combine(directory.FullName, "GameSaves.App", folder, fileName);

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }

    private sealed class InMemoryUiSettingsStore : IUiSettingsStore
    {
        private AppUiSettings _settings = AppUiSettings.Default;

        public string FilePath => "memory://ui-settings.json";

        public AppUiSettings Load() => _settings;

        public void Save(AppUiSettings settings) => _settings = settings;
    }
}
