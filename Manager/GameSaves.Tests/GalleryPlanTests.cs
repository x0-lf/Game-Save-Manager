using System;
using System.Collections.Generic;
using System.Linq;
using GameSaves.App.Services;
using GameSaves.UiCapture.Gallery;

namespace GameSaves.Tests
{
    /// <summary>
    /// Guards the gallery plan. The images themselves need a display and take
    /// minutes to produce, but everything that decides whether they will be
    /// correct — which pages exist, which accents and materials resolve, what
    /// the website set has to cover, and which engine is allowed to claim a
    /// window material — is data, and data can be checked here in milliseconds.
    /// </summary>
    public class GalleryPlanTests
    {
        [Fact]
        public void PlanCoversEverythingTheWebsitePromises()
        {
            IReadOnlyList<string> problems = GalleryVerification.VerifyPlan();

            Assert.True(
                problems.Count == 0,
                "Gallery plan problems:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void EveryCanonicalPageResolvesToATab()
        {
            for (int index = 0; index < UiRailLayoutSettings.CanonicalTabOrder.Count; index++)
            {
                Assert.Equal(
                    index,
                    GalleryScene.TabIndexOf(UiRailLayoutSettings.CanonicalTabOrder[index]));
            }
        }

        [Fact]
        public void GallerySizesAreTheTwoWebsiteResolutions()
        {
            Assert.Equal(
                new[] { (1280, 720), (1336, 768) },
                GalleryScenario.GallerySizes.ToArray());
        }

        [Fact]
        public void FileNamesDescribeEveryDimensionThatChangedThePixels()
        {
            GalleryScenario scenario = GalleryPlan.Curated()[0];

            Assert.Contains(scenario.Width.ToString(), scenario.FileName, StringComparison.Ordinal);
            Assert.Contains(scenario.Theme, scenario.FileName, StringComparison.Ordinal);
            Assert.Contains(scenario.Accent, scenario.FileName, StringComparison.Ordinal);
            Assert.Contains(scenario.RequestedMaterial, scenario.FileName, StringComparison.Ordinal);
            Assert.EndsWith(".png", scenario.FileName, StringComparison.Ordinal);
        }

        // The rule the whole two-harness split exists to enforce: a headless
        // render contains no compositor backdrop, so it may never be described
        // as showing one.
        [Fact]
        public void HeadlessCapturesNeverClaimAWindowMaterial()
        {
            foreach (GalleryScenario scenario in GalleryPlan.Curated()
                .Concat(GalleryPlan.Full())
                .Concat(GalleryPlan.Accessibility())
                .Where(candidate => candidate.Engine == GalleryEngines.Headless))
            {
                Assert.Equal(GalleryMaterials.None, scenario.ExpectedEffectiveMaterial);
            }
        }

        [Fact]
        public void HighContrastAlwaysResolvesToAnOpaqueWindow()
        {
            foreach (GalleryScenario scenario in GalleryPlan.Curated()
                .Concat(GalleryPlan.Full())
                .Where(candidate => candidate.HighContrast))
            {
                Assert.Equal(GalleryMaterials.None, scenario.ExpectedEffectiveMaterial);
            }
        }

        [Fact]
        public void DashboardShowcaseUsesTheAgreedMappingTotals()
        {
            Assert.Equal(2698, GalleryShowcase.ApprovedMappings);
            Assert.Equal(5978, GalleryShowcase.PendingMappings);
            Assert.Equal(16, GalleryShowcase.NeedsAttentionMappings);
        }

        // Privacy is the property that must not regress: a showcase value that
        // came from the machine would put a real name into a published image.
        [Fact]
        public void ShowcaseFixtureContainsNothingFromThisMachine()
        {
            string user = Environment.UserName;
            string machine = Environment.MachineName;

            var text = new List<string>();

            foreach (var game in GalleryShowcase.Games())
            {
                text.Add(game.GameName);
                text.Add(game.InstallPath);
                text.Add(game.LibraryPath);
            }

            foreach (var profile in GalleryShowcase.Profiles())
            {
                text.Add(profile.DisplayName);
                text.Add(profile.UserDataPath);
                text.Add(profile.SteamId64);
                text.Add(profile.AccountId);
            }

            foreach (string value in text)
            {
                Assert.DoesNotContain(user, value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(machine, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Synthetic identifiers have to look synthetic. A plausible-looking
        // SteamID64 in a published screenshot is indistinguishable from a real
        // one to everybody who sees it.
        [Fact]
        public void ShowcaseSteamIdentifiersAreVisiblyMasked()
        {
            foreach (var profile in GalleryShowcase.Profiles())
                Assert.Contains('•', profile.SteamId64);
        }

        [Fact]
        public void ShowcaseSyncHostCannotResolve()
        {
            // RFC 2606 reserves .test for exactly this.
            Assert.Contains(
                GalleryPlan.Curated(),
                scenario => scenario.ProviderScenario == GalleryProviders.Sftp);
        }

        [Fact]
        public void CuratedSetIsOrderedForPresentation()
        {
            int[] orders = GalleryPlan.Curated()
                .Select(scenario => scenario.GalleryOrder)
                .ToArray();

            Assert.Equal(orders.OrderBy(order => order).ToArray(), orders);
            Assert.Equal(orders.Distinct().Count(), orders.Length);
        }

        [Fact]
        public void EveryCuratedScenarioIsMarkedForTheWebsite()
        {
            Assert.All(GalleryPlan.Curated(), scenario => Assert.True(scenario.GalleryCandidate));
        }

        [Fact]
        public void PerceptualHashSeesThroughAnUnchangedFrameAndNotThroughADifferentOne()
        {
            byte[] left = Frame(0x20);
            byte[] right = Frame(0x20);
            byte[] other = Frame(0x20, invert: true);

            string a = GalleryManifest.AverageHash(left, 8, 8, 32);
            string b = GalleryManifest.AverageHash(right, 8, 8, 32);
            string c = GalleryManifest.AverageHash(other, 8, 8, 32);

            Assert.Equal(0, GalleryManifest.HammingDistance(a, b));
            Assert.True(GalleryManifest.HammingDistance(a, c) > 8);
        }

        private static byte[] Frame(byte value, bool invert = false)
        {
            byte[] pixels = new byte[8 * 8 * 4];

            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                bool bright = invert ? x < 4 : y < 4;
                byte level = bright ? (byte)(value + 200) : value;
                int index = ((y * 8) + x) * 4;

                pixels[index] = level;
                pixels[index + 1] = level;
                pixels[index + 2] = level;
                pixels[index + 3] = 255;
            }

            return pixels;
        }
    }
}
