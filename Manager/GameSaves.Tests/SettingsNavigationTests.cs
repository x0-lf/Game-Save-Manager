using System.Xml.Linq;

namespace GameSaves.Tests;

// A6 navigation rail: the Settings Navigation section and the collapsed-rail
// affordances are pinned structurally (automation names, bindings, style
// selectors). These tests parse XAML only; the behavior tests live in
// MainWindowTabDetachTests and SettingsViewModelTests.
public sealed class SettingsNavigationTests
{
    [Fact]
    public void SettingsView_DeclaresTheRailPositionRadioGroup()
    {
        XElement layoutTab = FindLayoutTab();

        XElement[] radios = layoutTab.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton" &&
                (string?)element.Attribute("GroupName") == "SettingsRailPosition")
            .ToArray();

        Assert.Equal(3, radios.Length);

        (string Content, string AutomationName, string ParameterMember)[] expected = new[]
        {
            ("Left", "Left rail position", "PositionLeft"),
            ("Right", "Right rail position", "PositionRight"),
            ("Top", "Top rail position", "PositionTop"),
        };

        for (int index = 0; index < expected.Length; index++)
        {
            XElement radio = radios[index];

            Assert.Equal(expected[index].Content, (string?)radio.Attribute("Content"));
            Assert.Equal(
                expected[index].AutomationName,
                (string?)radio.Attribute("AutomationProperties.Name"));

            // The ConverterParameter lives inside the IsChecked binding
            // string; each radio must bind the matching constant.
            string? isChecked = (string?)radio.Attribute("IsChecked");
            Assert.Contains("RailPosition", isChecked);
            Assert.Contains(expected[index].ParameterMember, isChecked);
        }
    }

    [Fact]
    public void SettingsView_DeclaresTheCollapseToggle()
    {
        XElement layoutTab = FindLayoutTab();

        XElement collapse = Assert.Single(
            layoutTab.Descendants(),
            element => element.Name.LocalName == "ToggleSwitch" &&
                ((string?)element.Attribute("IsChecked") ?? string.Empty)
                    .Contains("RailCollapsed"));

        Assert.Equal(
            "Collapse navigation rail",
            (string?)collapse.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void SettingsView_DeclaresPerTabVisibilityAndReorderRows()
    {
        XElement layoutTab = FindLayoutTab();

        XElement list = Assert.Single(
            layoutTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl" &&
                ((string?)element.Attribute("ItemsSource") ?? string.Empty)
                    .Contains("RailTabs"));

        XElement template = Assert.IsType<XElement>(
            Assert.Single(
                list.Descendants(),
                element => element.Name.LocalName == "DataTemplate"));

        XElement checkbox = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "CheckBox");

        Assert.Contains(
            "IsVisible",
            (string?)checkbox.Attribute("IsChecked"));
        Assert.Contains(
            "CanHide",
            (string?)checkbox.Attribute("IsEnabled"));
        Assert.Contains(
            "Show {0} tab",
            (string?)checkbox.Attribute("AutomationProperties.Name"));

        Button expectedUp = new(
            "MoveRailTabUpCommand",
            "{Binding CanMoveUp}",
            "{Binding Header, StringFormat='Move {0} tab up'}");
        Button expectedDown = new(
            "MoveRailTabDownCommand",
            "{Binding CanMoveDown}",
            "{Binding Header, StringFormat='Move {0} tab down'}");

        Assert.Equal(
            new[] { expectedUp, expectedDown },
            template.Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .Select(element => new Button(
                    ExtractBinding(element.Attribute("Command")?.Value),
                    element.Attribute("IsEnabled")?.Value,
                    (string?)element.Attribute("AutomationProperties.Name")))
                .ToArray());
    }

    [Fact]
    public void SettingsView_ExplainsThePinnedTabs()
    {
        XElement layoutTab = FindLayoutTab();

        Assert.Contains(
            layoutTab.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Text") ?? string.Empty)
                    .StartsWith("Dashboard and Settings always stay"));
    }

    [Fact]
    public void MainWindow_DeclaresACollapseToggleOnTheRailItself()
    {
        // The rail's collapse control used to be an expand-only button that
        // appeared only once the rail was already collapsed, which left no
        // in-rail way to collapse it in the first place. It is now a toggle
        // that is always present, so the control works in both directions and
        // in all three rail positions.
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement toggle = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "ToggleButton" &&
                Named(element, "CollapseNavigationButton"));

        Assert.Equal(
            "{Binding Settings.RailCollapsed, Mode=TwoWay}",
            (string?)toggle.Attribute("IsChecked"));

        // Never conditionally hidden: it is the only way back from a collapsed
        // rail, so it must not depend on the collapse state it controls.
        Assert.Null((string?)toggle.Attribute("IsVisible"));
        Assert.NotNull((string?)toggle.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void MainWindow_DeclaresTheScanActionOnTheRail()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement scan = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "Button" &&
                Named(element, "RailScanButton"));

        // Every one of these follows the active page. The view names no page
        // and no page command: the whole mapping lives in the view model, so
        // the button cannot drift out of step with the page it is sitting on.
        Assert.Equal("{Binding RailScanCommand}", (string?)scan.Attribute("Command"));
        Assert.Equal("{Binding IsRailScanVisible}", (string?)scan.Attribute("IsVisible"));
        Assert.Equal(
            "{Binding RailScanDescription}",
            (string?)scan.Attribute("AutomationProperties.Name"));

        // The accessible name and the tooltip say the same thing, so the
        // action stays self-describing while the rail is collapsed and the
        // visible label is not rendered.
        Assert.Equal(
            "{Binding RailScanDescription}", (string?)scan.Attribute("ToolTip.Tip"));

        XElement label = Assert.Single(
            scan.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Classes"))?.Contains("railChromeLabel") == true);

        Assert.Equal("{Binding RailScanLabel}", (string?)label.Attribute("Text"));
    }

    [Fact]
    public void MainWindow_RailChromeIsOutsideTheTabStripSoItSurvivesEveryRailPosition()
    {
        // The chrome sits in its own row above the TabControl rather than
        // inside the generated tab strip, which is what keeps it reachable
        // when the rail moves to the right edge or to the top.
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement chrome = Assert.Single(
            view.Descendants(),
            element => Named(element, "RailChrome"));

        Assert.DoesNotContain(
            chrome.Ancestors(),
            ancestor => ancestor.Name.LocalName == "TabControl");
    }

    [Fact]
    public void PrimaryAndSettingsNavigation_UseTheOpaqueSemanticSurface()
    {
        XDocument main = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));
        XDocument settings = XDocument.Load(FindAppFile("Views", "SettingsView.axaml"));

        XElement chrome = Assert.Single(
            main.Descendants(), element => Named(element, "RailChrome"));
        Assert.Contains(
            "NavigationSurfaceBrush",
            (string?)chrome.Attribute("Background"));

        AssertOpaqueTabStrip(main, "MainNavigation");
        AssertOpaqueTabStrip(settings, "SettingsCategories");
    }

    [Fact]
    public void PopupNavigation_UsesTheOpaqueSemanticSurface()
    {
        XDocument controls = XDocument.Load(
            FindAppFile("Themes", "Controls.axaml"));

        XElement popupStyle = Assert.Single(
            controls.Descendants(),
            element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute("Selector") ==
                    "ContextMenu, MenuFlyoutPresenter, ToolTip");

        XElement background = Assert.Single(
            popupStyle.Elements(),
            element => element.Name.LocalName == "Setter" &&
                (string?)element.Attribute("Property") == "Background");

        Assert.Contains(
            "NavigationSurfaceBrush",
            (string?)background.Attribute("Value"));
    }

    [Fact]
    public void MainWindow_CollapsedRailStylesHideLabelsAndDetachButtons()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        string?[] collapsedSelectors = view.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => (string?)element.Attribute("Selector"))
            .Where(selector => selector!.StartsWith("Window.railCollapsed"))
            .ToArray();

        Assert.Contains(
            collapsedSelectors,
            selector => selector!.EndsWith("TextBlock.navLabel"));
        Assert.Contains(
            collapsedSelectors,
            selector => selector!.EndsWith("Button.navDetach"));

        // The collapsed TabControl geometry must come from styles, because a
        // local Padding on the TabControl would defeat them.
        XElement tabControl = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "TabControl");
        Assert.DoesNotContain(
            "Padding",
            tabControl.Attributes().Select(attribute => attribute.Name.LocalName));
    }

    private static XElement FindLayoutTab()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "SettingsView.axaml"));

        return Assert.IsType<XElement>(
            Assert.Single(
                view.Descendants(),
                element => element.Name.LocalName == "TabItem" &&
                    (string?)element.Attribute("Header") == "Layout"));
    }

    private static void AssertOpaqueTabStrip(XDocument document, string name)
    {
        Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "TabControl" && Named(element, name));

        XElement style = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "Style" &&
                ((string?)element.Attribute("Selector"))?.Contains(
                    $"TabControl#{name} /template/ " +
                    "ItemsPresenter#PART_ItemsPresenter > WrapPanel") == true);

        Assert.Contains(
            "NavigationSurfaceBrush",
            (string?)style.Descendants()
                .Single(element => element.Name.LocalName == "Setter" &&
                    (string?)element.Attribute("Property") == "Background")
                .Attribute("Value"));

        foreach (string alignment in new[]
            { "HorizontalAlignment", "VerticalAlignment" })
        {
            Assert.Contains(
                style.Elements(),
                element => element.Name.LocalName == "Setter" &&
                    (string?)element.Attribute("Property") == alignment &&
                    (string?)element.Attribute("Value") == "Stretch");
        }
    }

    // Compiled-binding paths carry casts like
    // "$parent[UserControl].((vm:SettingsViewModel)DataContext).X"; the
    // command name is the last segment of the path.
    private static string? ExtractBinding(string? path) =>
        path?.TrimEnd('}').Split('.').LastOrDefault();


    // x:Name lives in the XAML namespace, so XLinq cannot find it by the
    // prefixed string; every lookup here goes through the local name.
    private static bool Named(XElement element, string name) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name);

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

    private sealed record Button(string? Command, string? IsEnabled, string? AutomationName);
}
