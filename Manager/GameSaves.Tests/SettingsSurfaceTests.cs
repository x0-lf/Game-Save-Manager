using System.Xml.Linq;

namespace GameSaves.Tests;

// A8 Settings surface categories: the Behaviour, Providers, Data and
// Diagnostics inner tabs are pinned structurally (tab headers, automation
// names, bindings, trimming) exactly like the navigation tests above pin the
// rail section. These tests parse XAML only; the behavior tests live in
// SettingsViewModelTests and MainWindowTabDetachTests.
public sealed class SettingsSurfaceTests
{
    [Fact]
    public void SettingsView_DeclaresTheSevenInnerTabsInOrder()
    {
        string?[] headers = SettingsTabs()
            .Select(tab => (string?)tab.Attribute("Header"))
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Appearance",
                "Accessibility",
                "Behaviour",
                "Layout",
                "Providers",
                "Data",
                "Diagnostics",
            },
            headers);
    }

    [Fact]
    public void SettingsView_DeclaresTheNineStartupTabRadios()
    {
        XElement behaviourTab = FindTab("Behaviour");

        XElement[] radios = behaviourTab.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton" &&
                (string?)element.Attribute("GroupName") == "SettingsStartupTab")
            .ToArray();

        (string Content, string AutomationName, string ParameterMember)[] expected = new[]
        {
            ("Dashboard", "Start on Dashboard", "TabDashboard"),
            ("Installed games", "Start on Installed games", "TabInstalledGames"),
            ("Profiles", "Start on Profiles", "TabProfiles"),
            ("Transfer preview", "Start on Transfer preview", "TabTransferPreview"),
            ("Manual backup", "Start on Manual backup", "TabManualBackup"),
            ("Backups", "Start on Backups", "TabBackups"),
            ("Sync", "Start on Sync", "TabSync"),
            ("History", "Start on History", "TabHistory"),
            ("Settings", "Start on Settings", "TabSettings"),
        };

        Assert.Equal(expected.Length, radios.Length);

        for (int index = 0; index < expected.Length; index++)
        {
            XElement radio = radios[index];

            Assert.Equal(expected[index].Content, (string?)radio.Attribute("Content"));
            Assert.Equal(
                expected[index].AutomationName,
                (string?)radio.Attribute("AutomationProperties.Name"));

            string? isChecked = (string?)radio.Attribute("IsChecked");
            Assert.Contains("StartupTabKey", isChecked);
            Assert.Contains(expected[index].ParameterMember, isChecked);
        }
    }

    [Fact]
    public void SettingsView_DeclaresTheProviderStatusListFromTheCatalogRows()
    {
        XElement providersTab = FindTab("Providers");

        XElement list = Assert.Single(
            providersTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl" &&
                ((string?)element.Attribute("ItemsSource") ?? string.Empty)
                    .Contains("ProviderStatuses"));

        XElement template = Assert.IsType<XElement>(
            Assert.Single(
                list.Descendants(),
                element => element.Name.LocalName == "DataTemplate"));

        Assert.Equal(
            "models:ProviderStatusOption",
            template.Attributes()
                .Single(attribute => attribute.Name.LocalName == "DataType")
                .Value);

        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                (string?)element.Attribute("Text") == "{Binding Name}");
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "TextBlock" &&
                (string?)element.Attribute("Text") == "{Binding Status}");

        // Each row now carries a real setup action. It is navigation only — it
        // opens the panel that already exists on the Sync page — and it is
        // gated on the catalog's own implemented flag, so a provider this build
        // cannot use is never offered a route it could not honour.
        XElement setUp = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button");

        Assert.Equal("Set up", (string?)setUp.Attribute("Content"));
        Assert.Equal("{Binding IsConfigurable}", (string?)setUp.Attribute("IsEnabled"));
        Assert.Contains(
            "ConfigureProviderCommand",
            (string?)setUp.Attribute("Command") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.NotNull((string?)setUp.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void SettingsView_DeclaresTheThreeDataLocationRows()
    {
        XElement dataTab = FindTab("Data");

        (string Binding, string AutomationName)[] expected = new[]
        {
            ("DatabasePath", "Database location"),
            ("UiSettingsPath", "Interface settings location"),
            ("SyncSettingsPath", "Sync settings location"),
        };

        foreach ((string binding, string automationName) in expected)
        {
            XElement row = Assert.Single(
                dataTab.Descendants(),
                element => element.Name.LocalName == "TextBlock" &&
                    (string?)element.Attribute("Text") == "{Binding " + binding + "}");

            // Paths trim from the front so the distinguishing tail stays
            // readable; nothing here browses, edits, or copies.
            Assert.Equal(
                "PrefixCharacterEllipsis",
                (string?)row.Attribute("TextTrimming"));
            Assert.Equal(automationName, (string?)row.Attribute("AutomationProperties.Name"));
        }

        Assert.DoesNotContain(
            dataTab.Descendants(),
            element => element.Name.LocalName is "Button" or "TextBox");
    }

    [Fact]
    public void SettingsView_DeclaresTheDiagnosticsRows()
    {
        XElement diagnosticsTab = FindTab("Diagnostics");

        string[] expectedBindings = new[]
        {
            "ApplicationVersion",
            "Platform",
            "OperatingSystemVersion",
            "RuntimeDescription",
        };

        foreach (string binding in expectedBindings)
        {
            Assert.Contains(
                diagnosticsTab.Descendants(),
                element => element.Name.LocalName == "TextBlock" &&
                    ((string?)element.Attribute("Text") ?? string.Empty)
                        .Contains("{Binding " + binding, StringComparison.Ordinal));
        }

        // The former About rows moved here and to Data; no About tab remains.
        Assert.DoesNotContain(
            SettingsTabs(),
            tab => (string?)tab.Attribute("Header") == "About");
    }

    private static XElement[] SettingsTabs()
    {
        XDocument view = XDocument.Load(FindAppFile("SettingsView.axaml"));

        return view.Descendants()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToArray();
    }

    private static XElement FindTab(string header)
    {
        return Assert.IsType<XElement>(
            Assert.Single(
                SettingsTabs(),
                element => (string?)element.Attribute("Header") == header));
    }

    private static string FindAppFile(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                return Path.Combine(
                    directory.FullName,
                    "GameSaves.App",
                    "Views",
                    fileName);

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
