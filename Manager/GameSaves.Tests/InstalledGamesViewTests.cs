using System.Xml.Linq;

namespace GameSaves.Tests;

public sealed class InstalledGamesViewTests
{
    [Fact]
    public void TableUsesOneReorderableSharedColumnSurface()
    {
        XDocument view = XDocument.Load(FindView("InstalledGamesView.axaml"));
        XElement grid = Assert.Single(
            view.Descendants(),
            element =>
                element.Name.LocalName == "DataGrid" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name" &&
                    attribute.Value == "GamesGrid"));

        Assert.Equal("True", (string?)grid.Attribute("CanUserReorderColumns"));
        Assert.Equal("True", (string?)grid.Attribute("CanUserResizeColumns"));
        // Wave 42: "Visible" drew a full-width track with no thumb in it at
        // 1400x900, where all ten columns already fit. The intent this guard
        // protects is that the table stays horizontally scrollable at the
        // narrow breakpoint, which "Auto" provides and which the capture
        // matrix verifies; it pins the value that provides it.
        Assert.Equal("Auto", (string?)grid.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("OnColumnReordered", (string?)grid.Attribute("ColumnReordered"));

        XElement[] columns = grid
            .Descendants()
            .Where(element => element.Attribute("Tag") is not null)
            .ToArray();

        Assert.Equal(10, columns.Length);
        Assert.Equal(10, columns.Select(column => (string)column.Attribute("Tag")!).Distinct().Count());
        // Wave 37 rebalanced default widths so all ten columns fit a
        // 1400x900 viewport without horizontal scrolling; 95 was verified in
        // captures to render the full "Needs fix" header. The intent this
        // guard protects is the full header, so it pins the width that
        // was pixel-verified to provide it.
        Assert.Contains(
            columns,
            column =>
                (string?)column.Attribute("Header") == "Needs fix" &&
                (string?)column.Attribute("Width") == "95");
    }

    [Fact]
    public void SettingsExposeEveryInstalledGameColumn()
    {
        // The column checkboxes moved from the header flyout to the Settings
        // page's Layout section; they stay bound to the InstalledGames child
        // view model so the table and Settings edit the same live options.
        XDocument view = XDocument.Load(FindView("SettingsView.axaml"));

        XElement options = Assert.Single(
            view.Descendants(),
            element =>
                (string?)element.Attribute("ItemsSource") ==
                "{Binding InstalledGames.ColumnOptions}");

        Assert.Contains(
            options.Ancestors(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                ((string?)element.Attribute("Classes") ?? string.Empty)
                    .Split(' ')
                    .Contains("pageScroll"));
    }

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
}
