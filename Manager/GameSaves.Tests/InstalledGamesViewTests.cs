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
        Assert.Equal("Visible", (string?)grid.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("OnColumnReordered", (string?)grid.Attribute("ColumnReordered"));

        XElement[] columns = grid
            .Descendants()
            .Where(element => element.Attribute("Tag") is not null)
            .ToArray();

        Assert.Equal(10, columns.Length);
        Assert.Equal(10, columns.Select(column => (string)column.Attribute("Tag")!).Distinct().Count());
        Assert.Contains(
            columns,
            column =>
                (string?)column.Attribute("Header") == "Needs fix" &&
                (string?)column.Attribute("Width") == "105");
    }

    [Fact]
    public void SettingsExposeEveryInstalledGameColumn()
    {
        XDocument window = XDocument.Load(FindView("MainWindow.axaml"));

        XElement options = Assert.Single(
            window.Descendants(),
            element =>
                (string?)element.Attribute("ItemsSource") ==
                "{Binding InstalledGames.ColumnOptions}");

        Assert.Contains(
            options.Ancestors(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto");
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
