using System.Xml.Linq;

namespace GameSaves.Tests;

public sealed class ManualBackupViewTests
{
    [Theory]
    [InlineData("BackupHistoryView.axaml")]
    [InlineData("InstalledGamesView.axaml")]
    [InlineData("MainWindow.axaml")]
    [InlineData("ManualBackupView.axaml")]
    [InlineData("ProfilesView.axaml")]
    [InlineData("TransferHistoryView.axaml")]
    [InlineData("TransferPreviewView.axaml")]
    public void OperationalStatus_RemainsVisible(string fileName)
    {
        XDocument view = XDocument.Load(FindView(fileName));
        XElement status = Assert.Single(
            view.Descendants(),
            element =>
                (string?)element.Attribute("Text") == "{Binding StatusMessage}");

        Assert.DoesNotContain(
            status.AncestorsAndSelf(),
            element => element.Attribute("IsVisible") is not null);
    }

    private static string FindView(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                return Path.Combine(
                    directory.FullName,
                    "GameSaves.App",
                    "Views",
                    fileName);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
