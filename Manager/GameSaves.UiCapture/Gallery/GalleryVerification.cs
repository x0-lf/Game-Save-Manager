using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameSaves.App.Services;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// The checks that decide whether a gallery is usable, expressed as data
    /// so the same rules run in <c>dotnet test</c> (against the plan) and in
    /// the capture harness (against the images it just wrote).
    ///
    /// Every method returns the problems it found rather than throwing, so one
    /// run reports every gap instead of the first one.
    /// </summary>
    public static class GalleryVerification
    {
        /// <summary>
        /// Checks the plan alone: does the curated set actually cover what the
        /// website promises, and is every scenario reachable and truthful?
        /// </summary>
        public static IReadOnlyList<string> VerifyPlan()
        {
            var problems = new List<string>();
            IReadOnlyList<GalleryScenario> curated = GalleryPlan.Curated();
            IReadOnlyList<GalleryScenario> full = GalleryPlan.Full();
            IReadOnlyList<GalleryScenario> accessibility = GalleryPlan.Accessibility();

            foreach (GalleryScenario scenario in curated.Concat(full).Concat(accessibility))
            {
                if (!UiRailLayoutSettings.IsTabKey(scenario.Page))
                    problems.Add($"{scenario.FileName}: unknown page '{scenario.Page}'.");

                if (!AppUiSettings.IsAccentTheme(scenario.Accent))
                    problems.Add($"{scenario.FileName}: unknown accent '{scenario.Accent}'.");

                if (!AppUiSettings.IsWindowMaterial(scenario.RequestedMaterial))
                {
                    problems.Add(
                        $"{scenario.FileName}: unknown material '{scenario.RequestedMaterial}'.");
                }

                if (!UiRailLayoutSettings.IsRailPosition(scenario.RailPosition))
                {
                    problems.Add(
                        $"{scenario.FileName}: unknown rail position '{scenario.RailPosition}'.");
                }

                if (scenario.TextScale < UiAccessibilitySettings.MinTextScale ||
                    scenario.TextScale > UiAccessibilitySettings.MaxTextScale)
                {
                    problems.Add($"{scenario.FileName}: text scale out of range.");
                }

                // A material the headless engine cannot composite must never be
                // routed to it: that is the whole point of splitting the plan
                // across two harnesses.
                if (scenario.RequestedMaterial != GalleryMaterials.None &&
                    scenario.Engine == GalleryEngines.Headless)
                {
                    problems.Add(
                        $"{scenario.FileName}: {scenario.RequestedMaterial} is assigned to " +
                        "the headless engine, which cannot composite a window material.");
                }

                // High Contrast disables transparency by design, so a High
                // Contrast scenario can never claim a material.
                if (scenario.HighContrast &&
                    scenario.ExpectedEffectiveMaterial != GalleryMaterials.None)
                {
                    problems.Add(
                        $"{scenario.FileName}: High Contrast must resolve to no material.");
                }
            }

            foreach (GalleryScenario scenario in curated)
            {
                if (scenario.Caption.Length == 0)
                    problems.Add($"{scenario.FileName}: no caption.");

                if (scenario.Alt.Length == 0)
                    problems.Add($"{scenario.FileName}: no alt text.");
            }

            foreach (IGrouping<string, GalleryScenario> duplicate in curated
                .GroupBy(scenario => scenario.FileName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                problems.Add($"{duplicate.Key}: the curated set names this file twice.");
            }

            if (curated.Count is < 40 or > 60)
            {
                problems.Add(
                    $"The curated set has {curated.Count} images; the website set is " +
                    "meant to be roughly 40 to 60.");
            }

            problems.AddRange(VerifyCoverage(
                curated.Select(Coverage).ToArray(), "curated plan"));

            // The two website resolutions, exactly.
            foreach (GalleryScenario scenario in curated)
            {
                if (!GalleryScenario.GallerySizes.Contains((scenario.Width, scenario.Height)))
                {
                    problems.Add(
                        $"{scenario.FileName}: {scenario.Width}x{scenario.Height} is not " +
                        "one of the website resolutions.");
                }
            }

            // The archive's cross-product, so a page or an accent cannot fall
            // out of it unnoticed.
            int expectedNormal =
                GalleryScenario.Pages.Count * 2 *
                GalleryScenario.GalleryAccents.Count *
                GalleryScenario.Materials.Count *
                GalleryScenario.GallerySizes.Count;
            int expectedHighContrast =
                GalleryScenario.Pages.Count *
                GalleryScenario.GalleryAccents.Count *
                GalleryScenario.GallerySizes.Count;

            if (full.Count != expectedNormal + expectedHighContrast)
            {
                problems.Add(
                    $"The archive matrix has {full.Count} cells; expected " +
                    $"{expectedNormal + expectedHighContrast}.");
            }

            // 85, 100 and 125 per cent are gallery candidates; 150 is
            // regression evidence and must still be captured.
            foreach (double scale in new[] { 0.85, 1.0, 1.25, 1.5 })
            {
                foreach (string page in GalleryScenario.Pages)
                {
                    if (!accessibility.Any(scenario =>
                            scenario.Page == page &&
                            Math.Abs(scenario.TextScale - scale) < 0.001))
                    {
                        problems.Add(
                            $"No text-scale capture for {page} at " +
                            scale.ToString("0.##", CultureInfo.InvariantCulture) + ".");
                    }
                }
            }

            return problems;
        }

        /// <summary>
        /// Checks a gallery that has actually been written: every manifest row
        /// has a file, every file is named once, and the selection still covers
        /// what the plan promised after any capture was disqualified.
        /// </summary>
        public static IReadOnlyList<string> VerifyOutput(string galleryRoot)
        {
            var problems = new List<string>();
            string manifestPath =
                Path.Combine(galleryRoot, GalleryManifest.CombinedFileName);

            if (!File.Exists(manifestPath))
                return new[] { $"No manifest at {manifestPath}." };

            GalleryManifestDocument? document =
                JsonSerializer.Deserialize<GalleryManifestDocument>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { WriteIndented = true });

            if (document is null)
                return new[] { $"{manifestPath} could not be read." };

            foreach (GalleryManifestEntry entry in document.Images)
            {
                string path = Path.Combine(
                    galleryRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path))
                {
                    problems.Add($"{entry.RelativePath}: the manifest names a missing file.");
                    continue;
                }

                if (!string.Equals(
                        GalleryManifest.Sha256Of(path), entry.Sha256, StringComparison.Ordinal))
                {
                    problems.Add($"{entry.RelativePath}: the file no longer matches its hash.");
                }

                // A capture may not claim a material the platform did not
                // grant, whatever the plan asked for.
                if (entry.EffectiveMaterial != GalleryMaterials.None &&
                    entry.CaptureEngine != GalleryEngines.WindowsScreenReadback)
                {
                    problems.Add(
                        $"{entry.RelativePath}: claims {entry.EffectiveMaterial} from " +
                        $"the {entry.CaptureEngine} engine, which composites nothing.");
                }

                if (entry.HighContrast && entry.EffectiveMaterial != GalleryMaterials.None)
                {
                    problems.Add(
                        $"{entry.RelativePath}: High Contrast with a window material.");
                }
            }

            foreach (IGrouping<string, GalleryManifestEntry> duplicate in document.Images
                .Where(entry => entry.GalleryCandidate)
                .GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                problems.Add($"{duplicate.Key}: two selected images share this name.");
            }

            // Every image on disk should be described. An undescribed PNG is
            // exactly the thing the manifest exists to prevent.
            foreach (string file in Directory.EnumerateFiles(
                galleryRoot, "*.png", SearchOption.AllDirectories))
            {
                string relative = Path
                    .GetRelativePath(galleryRoot, file)
                    .Replace(Path.DirectorySeparatorChar, '/');

                if (!document.Images.Any(entry =>
                        string.Equals(entry.RelativePath, relative, StringComparison.Ordinal)))
                {
                    problems.Add($"{relative}: on disk but not in the manifest.");
                }
            }

            GalleryManifestEntry[] selected = document.Images
                .Where(entry => entry.GalleryCandidate)
                .ToArray();

            problems.AddRange(VerifyCoverage(
                selected.Select(Coverage).ToArray(), "written gallery"));

            return problems;
        }

        // One row of the things the website set has to contain, shared by the
        // plan check and the output check so they can never disagree.
        private sealed record CoverageRow(
            string Page,
            string Theme,
            string Accent,
            bool HighContrast,
            string Material,
            string RailPosition,
            bool RailCollapsed,
            string Workspace,
            string Provider,
            string? Subpage);

        private static CoverageRow Coverage(GalleryScenario scenario) => new(
            scenario.Page,
            scenario.ThemeSlug,
            scenario.Accent,
            scenario.HighContrast,
            scenario.ExpectedEffectiveMaterial,
            scenario.RailPosition,
            scenario.RailCollapsed,
            scenario.Workspace,
            scenario.ProviderScenario,
            scenario.SettingsCategory is { } index &&
                index < GalleryScenario.SettingsCategories.Count
                    ? GalleryScenario.SettingsCategories[index]
                    : null);

        private static CoverageRow Coverage(GalleryManifestEntry entry) => new(
            entry.Page,
            entry.Theme,
            entry.Accent,
            entry.HighContrast,
            entry.EffectiveMaterial,
            entry.RailPosition,
            entry.RailCollapsed,
            entry.WorkspaceScenario,
            entry.ProviderScenario,
            entry.Subpage);

        private static IReadOnlyList<string> VerifyCoverage(
            IReadOnlyList<CoverageRow> rows, string what)
        {
            var problems = new List<string>();

            void Require(bool present, string description)
            {
                if (!present)
                    problems.Add($"The {what} does not cover {description}.");
            }

            foreach (string page in GalleryScenario.Pages)
                Require(rows.Any(row => row.Page == page), $"the {page} page");

            foreach (string accent in GalleryScenario.GalleryAccents)
                Require(rows.Any(row => row.Accent == accent), $"the {accent} accent");

            Require(rows.Any(row => row.Theme == AppUiSettings.ThemeLight), "light mode");
            Require(rows.Any(row => row.Theme == AppUiSettings.ThemeDark), "dark mode");
            Require(rows.Any(row => row.HighContrast), "High Contrast");

            foreach (string material in GalleryScenario.Materials)
            {
                Require(
                    rows.Any(row => row.Material == material),
                    $"an effective {material} window material");
            }

            foreach (string position in new[]
            {
                UiRailLayoutSettings.PositionLeft,
                UiRailLayoutSettings.PositionRight,
                UiRailLayoutSettings.PositionTop,
            })
            {
                Require(
                    rows.Any(row => row.RailPosition == position),
                    $"the {position} navigation position");
            }

            Require(rows.Any(row => row.RailCollapsed), "a collapsed navigation rail");

            foreach (string workspace in new[]
            {
                GalleryWorkspaces.LeftRightSplit,
                GalleryWorkspaces.TopBottom,
                GalleryWorkspaces.FourRegions,
                GalleryWorkspaces.Resized,
                GalleryWorkspaces.SavedCustom,
                GalleryWorkspaces.Restored,
                GalleryWorkspaces.FloatingPanel,
                GalleryWorkspaces.DetachedTab,
                GalleryWorkspaces.MultipleWindows,
            })
            {
                Require(
                    rows.Any(row => row.Workspace == workspace),
                    $"the {workspace} workspace arrangement");
            }

            foreach (string provider in new[]
            {
                GalleryProviders.LocalFolder,
                GalleryProviders.Sftp,
                GalleryProviders.GoogleDrive,
                GalleryProviders.Preview,
                GalleryProviders.Results,
            })
            {
                Require(
                    rows.Any(row => row.Provider == provider),
                    $"the {provider} sync state");
            }

            foreach (string category in GalleryScenario.SettingsCategories)
            {
                Require(
                    rows.Any(row => row.Subpage == category),
                    $"the {category} settings category");
            }

            return problems;
        }
    }
}
