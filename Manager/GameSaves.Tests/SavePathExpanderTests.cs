using GameSaves.Core.Steam;
using GameSaves.Infrastructure.Save;
using Xunit;

namespace GameSaves.Tests;

public sealed class SavePathExpanderTests
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static SteamGame Game(string gamePath) =>
        new("620", "Portal 2", "Portal 2", @"C:\Steam", @"C:\Steam\steamapps\appmanifest_620.acf", gamePath, gamePath.Length > 0, SteamDiscoveryConfidence.High);

    [Fact]
    public void GameInstallPath_ForAGameThatIsNotInstalled_YieldsNoCandidate()
    {
        // Used to become "C:\saves" (the current drive's root) and be offered
        // as a Ready restore target.
        Assert.Empty(new SavePathExpander().ExpandCandidatePaths(@"{GameInstallPath}\saves", Game(""), steamRoot: null));
    }

    [Fact]
    public void GameInstallPath_ForAnInstalledGame_Expands()
    {
        Assert.Equal(
            [@"C:\Games\Portal 2\saves"],
            new SavePathExpander().ExpandCandidatePaths(@"{GameInstallPath}\saves", Game(@"C:\Games\Portal 2"), steamRoot: null));
    }

    [Fact]
    public void SavedGames_ExpandsUnderTheUserProfile()
    {
        Assert.Equal(
            [Path.Combine(UserProfile, "Saved Games", "X")],
            new SavePathExpander().ExpandCandidatePaths(@"{SavedGames}\X", Game(""), steamRoot: null));
    }

    [Theory]
    [InlineData(@"{SteamRoot}\userdata\x")]
    [InlineData(@"{NoSuchToken}\x")]
    [InlineData(@"%GSM_NO_SUCH_VARIABLE%\x")]
    [InlineData(@"relative\x")]
    [InlineData("$HOME/.local/share/x")]
    public void UnresolvedOrRelativeTemplates_YieldNoCandidate(string template)
    {
        Assert.Empty(new SavePathExpander().ExpandCandidatePaths(template, Game(@"C:\Games\Portal 2"), steamRoot: null));
    }
}
