using System.Xml.Linq;

namespace GameSaves.Tests;

// Accessibility slice B: screen-reader names, keyboard shortcuts, the
// detach context menu, and focus rings. These tests parse XAML only;
// the Ctrl+1..9 shortcut selection tests live in MainWindowTabDetachTests
// because they construct real Avalonia controls, which must happen on the
// single thread that owns the process-wide Avalonia UI dispatcher.
public sealed class MainWindowAccessibilityTests
{
    [Fact]
    public void EveryNavigationTabCarriesNamesTooltipAndDetachContextMenu()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

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
            Assert.False(
                string.IsNullOrWhiteSpace((string?)tab.Attribute("AutomationProperties.Name")),
                "Every tab must carry a screen-reader name that survives compact-nav " +
                "mode, where the visible label is hidden.");

            XElement header = Assert.IsType<XElement>(
                Assert.Single(
                    tab.Elements(),
                    element => element.Name.LocalName == "TabItem.Header"));

            XElement button = Assert.Single(
                header.Descendants(),
                element => element.Name.LocalName == "Button");

            Assert.StartsWith(
                "Detach ",
                (string?)button.Attribute("AutomationProperties.Name"));
            Assert.False(
                string.IsNullOrWhiteSpace(
                    (string?)button.Attribute("AutomationProperties.HelpText")));
            Assert.False(
                string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip.Tip")));

            XElement menuContainer = Assert.IsType<XElement>(
                Assert.Single(
                    tab.Elements(),
                    element => element.Name.LocalName == "TabItem.ContextMenu"));

            XElement menuItem = Assert.Single(
                menuContainer.Descendants(),
                element => element.Name.LocalName == "MenuItem");

            Assert.Equal("Detach tab", (string?)menuItem.Attribute("Header"));
            Assert.Equal(
                "Detach tab",
                (string?)menuItem.Attribute("AutomationProperties.Name"));
            Assert.Equal("OnTabDetachMenuClicked", (string?)menuItem.Attribute("Click"));
        }
    }

    [Fact]
    public void SettingsGearIsNamedAndUsesTheIconButtonClass()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement gear = Assert.Single(
            view.Descendants(),
            element => element.Name.LocalName == "Button" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name" &&
                    attribute.Value == "SettingsButton"));

        Assert.Equal("Settings", (string?)gear.Attribute("AutomationProperties.Name"));
        Assert.False(
            string.IsNullOrWhiteSpace((string?)gear.Attribute("ToolTip.Tip")));
        // Chrome comes from the iconButton class style so the focus ring can
        // rebalance padding; inline padding on the button would defeat it.
        Assert.DoesNotContain("Padding", gear.Attributes().Select(a => a.Name.LocalName));
        Assert.Contains(
            ((string?)gear.Attribute("Classes") ?? string.Empty).Split(' '),
            value => value == "iconButton");
    }

    [Fact]
    public void WindowDeclaresSlotAndSettingsShortcutKeyBindings()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "MainWindow.axaml"));

        XElement bindings = Assert.IsType<XElement>(
            Assert.Single(
                view.Root!.Elements(),
                element => element.Name.LocalName == "Window.KeyBindings"));

        XElement[] keyBindings = bindings
            .Elements()
            .Where(element => element.Name.LocalName == "KeyBinding")
            .ToArray();

        Assert.Equal(10, keyBindings.Length);

        for (int slot = 1; slot <= 9; slot++)
        {
            Assert.Contains(
                keyBindings,
                binding =>
                    (string?)binding.Attribute("Gesture") == $"Ctrl+D{slot}" &&
                    (string?)binding.Attribute("CommandParameter") == slot.ToString());
        }

        Assert.Contains(
            keyBindings,
            binding => (string?)binding.Attribute("Gesture") == "Ctrl+OemComma");
    }

    [Fact]
    public void DetachedWindowDeclaresEscapeToClose()
    {
        XDocument view = XDocument.Load(FindAppFile("Views", "DetachedWindow.axaml"));

        XElement bindings = Assert.IsType<XElement>(
            Assert.Single(
                view.Root!.Elements(),
                element => element.Name.LocalName == "Window.KeyBindings"));

        Assert.Contains(
            bindings.Elements(),
            binding =>
                binding.Name.LocalName == "KeyBinding" &&
                (string?)binding.Attribute("Gesture") == "Escape");
    }

    [Fact]
    public void IconOnlyButtonsHaveFocusVisibleAccentRings()
    {
        XDocument controls = XDocument.Load(FindAppFile("Themes", "Controls.axaml"));

        AssertFocusRing(controls, "Button.navDetach:focus-visible");
        AssertFocusRing(controls, "Button.iconButton:focus-visible");

        // The detach button's hit target is at least 24x24 while its glyph
        // keeps its pixel position (verified by measurement against the
        // pre-change geometry).
        XElement detachBase = Assert.Single(
            controls.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                (string?)element.Attribute("Selector") == "Button.navDetach");

        Assert.Equal("24", SetterValue(detachBase, "MinWidth"));
        Assert.Equal("24", SetterValue(detachBase, "MinHeight"));
        Assert.Equal("-4,0,0,0", SetterValue(detachBase, "Margin"));

        static void AssertFocusRing(XDocument document, string selector)
        {
            XElement style = Assert.Single(
                document.Descendants(),
                element =>
                    element.Name.LocalName == "Style" &&
                    (string?)element.Attribute("Selector") == selector);

            Assert.Contains("AccentBrush", SetterValue(style, "BorderBrush"));
            Assert.Equal("2", SetterValue(style, "BorderThickness"));
        }
    }

    private static string? SetterValue(XElement style, string property)
    {
        XElement? setter = style
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Setter" &&
                (string?)element.Attribute("Property") == property);

        return (string?)setter?.Attribute("Value");
    }

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
}
