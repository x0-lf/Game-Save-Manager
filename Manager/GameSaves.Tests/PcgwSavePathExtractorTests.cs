using GameSaves.External;
using Xunit;

namespace GameSaves.Tests
{
    // First behavioural coverage for the PCGamingWiki extractor, built from
    // the shapes the 2026-08-20 measurement (docs/pcgw-measurement.md) found
    // in the real cached pages. The compound-template cases are the single
    // largest genuine-failure category (82% of parser failures, 1,888 pages):
    // the wiki writes the rest of the path inside the same {{p|...}} template
    // as the token.
    public sealed class PcgwSavePathExtractorTests
    {
        private static readonly PcgwTitle Title = new(
            PageId: 1,
            PageName: "Example_Game",
            DisplayTitle: "Example Game",
            SteamAppIds: new List<string> { "400" },
            SourceUrl: "https://example.invalid/Example_Game");

        private static string Page(params string[] saveRows)
        {
            return "==Availability==\nSome text.\n"
                + "===Save game data location===\n"
                + string.Join("\n", saveRows)
                + "\n==Video==\nMore text.\n";
        }

        private static List<string> Extract(params string[] saveRows)
        {
            var extractor = new PcgwSavePathExtractor();
            return extractor
                .ExtractCandidates(Title, Page(saveRows))
                .Select(item => $"{item.Platform}|{item.PathTemplate}")
                .ToList();
        }

        [Fact]
        public void ACompoundUserprofileTemplate_YieldsTheFullExpandedPath()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|userprofile\Documents\My Games\Example}}}}");

            string result = Assert.Single(results);
            Assert.Equal(@"windows|%USERPROFILE%\Documents\My Games\Example", result);
        }

        [Fact]
        public void ACompoundAppDataLocalLowTemplate_YieldsTheFullExpandedPath()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|userprofile\appdata\locallow\Studio\Example}}}}");

            string result = Assert.Single(results);
            Assert.Equal(
                @"windows|%USERPROFILE%\appdata\locallow\Studio\Example", result);
        }

        [Fact]
        public void AForwardSlashCompoundTemplate_IsExpandedAndNormalized()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|userprofile/Documents/Example}}}}");

            string result = Assert.Single(results);
            Assert.Equal(@"windows|%USERPROFILE%\Documents\Example", result);
        }

        [Fact]
        public void ABareTokenFollowedByPlainText_StillYieldsThePath()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|userprofile}}\Saved Games\Example}}");

            string result = Assert.Single(results);
            Assert.Equal(@"windows|%USERPROFILE%\Saved Games\Example", result);
        }

        [Fact]
        public void TheSteamappsToken_IsNeverSwallowedByTheShorterSteamToken()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|steamapps}}\common\Example\Saves}}");

            string result = Assert.Single(results);
            Assert.Equal(@"windows|{LibraryRoot}\steamapps\common\Example\Saves", result);
        }

        [Fact]
        public void TheXdgDataHomeToken_YieldsALinuxPath()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Linux|{{p|xdgdatahome}}/Example}}");

            string result = Assert.Single(results);
            Assert.Equal(@"linux|$XDG_DATA_HOME\Example", result);
        }

        [Fact]
        public void TheOsxHomeToken_YieldsAMacPathClassifiedByItsRow()
        {
            List<string> results = Extract(
                @"{{Game data/saves|OS X|{{p|osxhome}}/Library/Application Support/Example}}");

            string result = Assert.Single(results);
            Assert.Equal(@"macos|$HOME\Library\Application Support\Example", result);
        }

        [Fact]
        public void MultipleRows_AreAllPreservedAsSeparateCandidates()
        {
            List<string> results = Extract(
                @"{{Game data/saves|Windows|{{p|userprofile\Documents\My Games\Example}}}}",
                @"{{Game data/saves|Windows|{{p|localappdata}}\Example}}",
                @"{{Game data/saves|Linux|{{p|xdgconfighome}}/Example}}");

            Assert.Equal(3, results.Count);
            Assert.Contains(
                @"windows|%USERPROFILE%\Documents\My Games\Example", results);
            Assert.Contains(@"windows|%LOCALAPPDATA%\Example", results);
            Assert.Contains(@"linux|$XDG_CONFIG_HOME\Example", results);
        }

        [Fact]
        public void APageWithoutASaveSection_YieldsNothing()
        {
            var extractor = new PcgwSavePathExtractor();

            List<GameSaves.Core.Save.SavePathImportItem> results =
                extractor.ExtractCandidates(
                    Title, "==Availability==\nNo save section here.\n");

            Assert.Empty(results);
        }
    }
}
