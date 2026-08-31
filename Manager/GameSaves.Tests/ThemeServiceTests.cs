using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia.Media;
using GameSaves.App.Services;
using Xunit;

namespace GameSaves.Tests
{
    // ThemeService's runtime application needs a live Avalonia Application,
    // so these tests pin the pure parts: palette completeness and fidelity
    // (indigo must reproduce Tokens.axaml exactly), the opacity math, the
    // text-scale token table, and the reduce-motion and high-contrast tables.
    public sealed class ThemeServiceTests
    {
        public static TheoryData<string> Accents => new()
        {
            AppUiSettings.AccentIndigo,
            AppUiSettings.AccentTeal,
            AppUiSettings.AccentRose,
            AppUiSettings.AccentAmber,
            AppUiSettings.AccentViolet,
        };

        [Theory]
        [MemberData(nameof(Accents))]
        public void EveryAccent_DefinesAllKeysInBothVariants(string accent)
        {
            foreach (bool isDark in new[] { true, false })
            {
                ThemeService.AccentPalette palette =
                    ThemeService.GetPalette(accent, isDark);

                var colors = palette.AsResources().ToDictionary(
                    pair => pair.Key, pair => pair.Value);

                Assert.Superset(
                    ThemeService.AccentResourceKeys.ToHashSet(),
                    colors.Keys.ToHashSet());
                Assert.DoesNotContain(default, colors.Values);
            }
        }

        [Fact]
        public void AnUnknownAccent_FallsBackToIndigo()
        {
            Assert.Equal(
                ThemeService.GetPalette(AppUiSettings.AccentIndigo, isDark: true),
                ThemeService.GetPalette("chartreuse", isDark: true));
            Assert.Equal(
                ThemeService.GetPalette(AppUiSettings.AccentIndigo, isDark: false),
                ThemeService.GetPalette(string.Empty, isDark: false));
        }

        [Fact]
        public void IndigoDarkPalette_MatchesTokensAxamlExactly()
        {
            ThemeService.AccentPalette indigo =
                ThemeService.GetPalette(AppUiSettings.AccentIndigo, isDark: true);

            Assert.Equal(Color.Parse("#4F6EDB"), indigo.SystemAccentColor);
            Assert.Equal(Color.Parse("#7C9CFF"), indigo.Accent);
            Assert.Equal(Color.Parse("#A9BCFF"), indigo.Brand);
            Assert.Equal(Color.Parse("#93AEFF"), indigo.AccentHover);
            Assert.Equal(Color.Parse("#6B8BF0"), indigo.AccentPressed);
            Assert.Equal(Color.Parse("#4F6EDB"), indigo.PrimaryButton);
            Assert.Equal(Color.Parse("#5B7BE8"), indigo.PrimaryButtonHover);
            Assert.Equal(Color.Parse("#3F5CC4"), indigo.PrimaryButtonPressed);
        }

        [Fact]
        public void IndigoLightPalette_MatchesTokensAxamlExactly()
        {
            ThemeService.AccentPalette indigo =
                ThemeService.GetPalette(AppUiSettings.AccentIndigo, isDark: false);

            Assert.Equal(Color.Parse("#3557C7"), indigo.SystemAccentColor);
            Assert.Equal(Color.Parse("#3557C7"), indigo.Accent);
            Assert.Equal(Color.Parse("#3557C7"), indigo.Brand);
            Assert.Equal(Color.Parse("#2C4BB0"), indigo.AccentHover);
            Assert.Equal(Color.Parse("#243F99"), indigo.AccentPressed);
            Assert.Equal(Color.Parse("#3557C7"), indigo.PrimaryButton);
            Assert.Equal(Color.Parse("#2C4BB0"), indigo.PrimaryButtonHover);
            Assert.Equal(Color.Parse("#243F99"), indigo.PrimaryButtonPressed);
        }

        [Fact]
        public void WithOpacity_ScalesAlphaAndKeepsHue()
        {
            Color baseColor = Color.Parse("#FF102030");

            Color half = ThemeService.WithOpacity(baseColor, 0.5);
            Assert.Equal(128, half.A);
            Assert.Equal(baseColor.R, half.R);
            Assert.Equal(baseColor.G, half.G);
            Assert.Equal(baseColor.B, half.B);

            Color alreadyTranslucent = Color.Parse("#80102030");
            Color quarter = ThemeService.WithOpacity(alreadyTranslucent, 0.5);
            Assert.Equal(64, quarter.A);

            Color semi = Color.Parse("#C8102030");
            Color full = ThemeService.WithOpacity(semi, 2.5);
            Assert.Equal(0xC8, full.A);
        }

        [Theory]
        [InlineData(1.5, 1.0)]
        [InlineData(-0.2, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(1.0, 1.0)]
        [InlineData(double.NaN, 1.0)]
        [InlineData(double.PositiveInfinity, 1.0)]
        public void OpacityNormalizesToTheUnitRange(double input, double expected)
        {
            Assert.Equal(
                expected,
                UiTransparencySettings.NormalizeOpacity(input));
        }

        [Theory]
        [InlineData(0.5, 0.85)]
        [InlineData(0.85, 0.85)]
        [InlineData(1.0, 1.0)]
        [InlineData(1.2, 1.2)]
        [InlineData(1.5, 1.5)]
        [InlineData(2.0, 1.5)]
        [InlineData(double.NaN, 1.0)]
        [InlineData(double.NegativeInfinity, 1.0)]
        public void TextScale_ClampsToTheSupportedRange(double input, double expected)
        {
            Assert.Equal(expected, UiAccessibilitySettings.ClampTextScale(input));
        }

        [Fact]
        public void ScaledFontSize_MultipliesTheBaseByTheClampedScale()
        {
            Assert.Equal(12, ThemeService.ScaledFontSize(12, 1.0));
            Assert.Equal(15, ThemeService.ScaledFontSize(12, 1.25));
            Assert.Equal(10.2, ThemeService.ScaledFontSize(12, 0.85));
            Assert.Equal(48, ThemeService.ScaledFontSize(32, 1.5));
            Assert.Equal(18, ThemeService.ScaledFontSize(12, 9.0));
            Assert.Equal(12, ThemeService.ScaledFontSize(12, double.NaN));
        }

        [Fact]
        public void TextScaleOverrides_AtDefaultReproduceTheBaseTable()
        {
            Assert.Equal(
                ThemeService.FontSizeTokenBaseSizes,
                ThemeService.BuildTextScaleOverrides(1.0));
        }

        [Fact]
        public void TextScaleOverrides_ScaleEveryTokenAtAnIncreasedScale()
        {
            System.Collections.Generic.IReadOnlyDictionary<string, double> overrides =
                ThemeService.BuildTextScaleOverrides(1.3);

            Assert.Equal(ThemeService.FontSizeTokenBaseSizes.Keys, overrides.Keys);

            foreach ((string key, double baseSize) in ThemeService.FontSizeTokenBaseSizes)
                Assert.Equal(ThemeService.ScaledFontSize(baseSize, 1.3), overrides[key]);
        }

        [Fact]
        public void FontSizeTokenBaseSizes_MatchTokensAxamlExactly()
        {
            XDocument tokens = XDocument.Load(FindAppFile(Path.Combine("Themes", "Tokens.axaml")));

            // The x:Key attribute lives in the XAML namespace; resolve it by
            // local name because the prefix is an implementation detail.
            var expected = tokens
                .Descendants()
                .Where(element => element.Name.LocalName == "Double")
                .ToDictionary(
                    element => element.Attributes().Single(
                        attribute => attribute.Name.LocalName == "Key").Value,
                    element => double.Parse(element.Value));

            Assert.Equal(
                ThemeService.FontSizeTokenBaseSizes.OrderBy(pair => pair.Key),
                expected.OrderBy(pair => pair.Key));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HighContrast_WhenOff_WritesNothing(bool isDark)
        {
            Assert.Empty(ThemeService.GetHighContrastOverrides(isDark, highContrast: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HighContrast_CoversEverySemanticKeyWithSolidColors(bool isDark)
        {
            System.Collections.Generic.IReadOnlyDictionary<string, Color> overrides =
                ThemeService.GetHighContrastOverrides(isDark, highContrast: true);

            Assert.Equal(
                ThemeService.HighContrastResourceKeys.ToHashSet(),
                overrides.Keys.ToHashSet());

            foreach (Color color in overrides.Values)
            {
                Assert.NotEqual(default, color);
                Assert.Equal(255, color.A);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HighContrast_LeavesAccentKeysToTheAccentPalette(bool isDark)
        {
            System.Collections.Generic.IReadOnlyDictionary<string, Color> overrides =
                ThemeService.GetHighContrastOverrides(isDark, highContrast: true);

            Assert.Empty(overrides.Keys.Intersect(ThemeService.AccentResourceKeys));
        }

        [Fact]
        public void HighContrast_UsesInvertedInksBetweenVariants()
        {
            System.Collections.Generic.IReadOnlyDictionary<string, Color> dark =
                ThemeService.GetHighContrastOverrides(isDark: true, highContrast: true);
            System.Collections.Generic.IReadOnlyDictionary<string, Color> light =
                ThemeService.GetHighContrastOverrides(isDark: false, highContrast: true);

            Assert.Equal(
                Color.Parse("#000000"),
                dark[ThemeService.PageBackgroundBrushKey]);
            Assert.Equal(
                Color.Parse("#FFFFFF"),
                dark["PrimaryTextBrush"]);
            Assert.Equal(
                Color.Parse("#FFFFFF"),
                light[ThemeService.PageBackgroundBrushKey]);
            Assert.Equal(
                Color.Parse("#000000"),
                light["PrimaryTextBrush"]);
        }

        [Theory]
        [InlineData(0.4, true, 1.0)]
        [InlineData(0.4, false, 0.4)]
        [InlineData(1.7, false, 1.0)]
        [InlineData(1.7, true, 1.0)]
        public void HighContrast_ForcesOpaqueSurfaces(
            double stored, bool highContrast, double expected)
        {
            Assert.Equal(
                expected,
                ThemeService.EffectiveSurfaceOpacity(stored, highContrast));
        }

        [Theory]
        [InlineData(0.4, false, false, 0.4)]
        [InlineData(0.4, true, false, 1.0)]
        // A compositing material makes the window opacity setting inert and
        // pins the surface to the readability floor. It used to drop to 0.0,
        // which handed the app's ink to whatever was behind the window.
        [InlineData(1.0, false, true, ThemeService.WindowMaterialSurfaceFloor)]
        [InlineData(0.2, false, true, ThemeService.WindowMaterialSurfaceFloor)]
        // High contrast always wins: even a stale material confirmation
        // must not thin a high-contrast surface.
        [InlineData(0.2, true, true, 1.0)]
        public void WindowSurfaceOpacity_HoldsTheMaterialFloorWhileOneComposites(
            double stored, bool highContrast, bool materialActive, double expected)
        {
            Assert.Equal(
                expected,
                ThemeService.EffectiveWindowSurfaceOpacity(
                    stored, highContrast, materialActive));
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void TheMaterialFloor_KeepsNormalTextReadableOverAnyDesktop(
            string variant)
        {
            // The reported failure: Acrylic let the desktop decide the
            // luminance the app's ink was read against, and dark secondary text
            // measures about 1.1:1 on white. The colours are read from the
            // token dictionary rather than repeated here, so a repalette is
            // caught too; both extremes are tested so the floor cannot be tuned
            // to one desktop. Every ink the app treats as normal text has to
            // clear AA — including the muted one, which is dimmer than the
            // secondary one in both variants and is the binding case.
            IReadOnlyDictionary<string, Avalonia.Media.Color> tokens =
                VariantColors(variant);

            Avalonia.Media.Color page = tokens["PageBackgroundBrush"];

            foreach (string inkKey in new[]
            {
                "PrimaryTextBrush", "SecondaryTextBrush", "MutedTextBrush",
            })
            {
                foreach (string desktop in new[] { "#FFFFFF", "#000000" })
                {
                    // What the compositor actually produces: the page colour
                    // drawn at the floor's alpha over what the material shows.
                    Avalonia.Media.Color composited = Composite(
                        page,
                        Avalonia.Media.Color.Parse(desktop),
                        ThemeService.WindowMaterialSurfaceFloor);

                    double ratio = ContrastRatio(composited, tokens[inkKey]);

                    Assert.True(
                        ratio >= 4.5,
                        $"{variant}/{inkKey} over {page} at " +
                        $"{ThemeService.WindowMaterialSurfaceFloor:F2} alpha on a " +
                        $"{desktop} desktop is {ratio:F2}:1, below WCAG AA.");
                }
            }
        }

        // The SolidColorBrush colours declared for one theme variant in
        // Tokens.axaml, so a contrast assertion measures what actually ships.
        private static IReadOnlyDictionary<string, Avalonia.Media.Color> VariantColors(
            string variant)
        {
            XDocument tokens = XDocument.Load(
                FindAppFile(Path.Combine("Themes", "Tokens.axaml")));

            XElement dictionary = tokens
                .Descendants()
                .Single(element => element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key" && attribute.Value == variant));

            return dictionary
                .Descendants()
                .Where(element => element.Name.LocalName == "SolidColorBrush")
                .ToDictionary(
                    element => element.Attributes().Single(
                        attribute => attribute.Name.LocalName == "Key").Value,
                    element => Avalonia.Media.Color.Parse(
                        element.Attribute("Color")!.Value));
        }

        // Source-over alpha compositing, which is what the window surface and
        // the material backdrop actually do.
        private static Avalonia.Media.Color Composite(
            Avalonia.Media.Color surface,
            Avalonia.Media.Color backdrop,
            double alpha) =>
            Avalonia.Media.Color.FromRgb(
                (byte)Math.Round((alpha * surface.R) + ((1 - alpha) * backdrop.R)),
                (byte)Math.Round((alpha * surface.G) + ((1 - alpha) * backdrop.G)),
                (byte)Math.Round((alpha * surface.B) + ((1 - alpha) * backdrop.B)));

        [Fact]
        public void MotionDurationFast_IsInstantWhenReducedAndShippedOtherwise()
        {
            Assert.Equal(TimeSpan.Zero, ThemeService.MotionDurationFast(true));
            Assert.Equal(
                TimeSpan.FromMilliseconds(150),
                ThemeService.MotionDurationFast(false));
        }

        // BuildScrollBarTransitions itself is not unit-constructed here: the
        // Transitions collection validates items through the Avalonia
        // dispatcher, which parallel xUnit threads do not own. The real app
        // only builds it on the UI thread inside ThemeService.Apply; the
        // duration table above and the wiring test below pin the behavior.

        [Fact]
        public void TheOnlyAppAnimation_IsTheResourceDrivenScrollbarFade()
        {
            XDocument controls = XDocument.Load(FindAppFile(Path.Combine("Themes", "Controls.axaml")));

            // Controls.axaml must resolve its scrollbar transitions through
            // the resource so ThemeService can swap them at runtime, and it
            // must not author any other animation of its own.
            XElement scrollBarStyle = controls
                .Descendants()
                .Single(element => element.Name.LocalName == "Style" &&
                    (string?)element.Attribute("Selector") == "ScrollBar");

            Assert.Contains(
                scrollBarStyle.Descendants(),
                element => (string?)element.Attribute("Value") ==
                    "{DynamicResource ScrollBarOpacityTransitions}");

            Assert.DoesNotContain(controls.Descendants(),
                element => element.Name.LocalName == "Transitions");
        }

        [Theory]
        [MemberData(nameof(Accents))]
        public void EveryAccent_PublishesTheFluentShadeColours(string accent)
        {
            // Fluent derives every accent hover and pressed state from these six
            // shades, and Avalonia seeds them from the OPERATING SYSTEM accent,
            // not from SystemAccentColor. If they are not republished here, a
            // hovered checkbox, radio, slider, toggle or tab pipe flashes the
            // Windows accent instead of the one the user chose.
            string[] shadeKeys =
            {
                ThemeService.SystemAccentColorLight1Key,
                ThemeService.SystemAccentColorLight2Key,
                ThemeService.SystemAccentColorLight3Key,
                ThemeService.SystemAccentColorDark1Key,
                ThemeService.SystemAccentColorDark2Key,
                ThemeService.SystemAccentColorDark3Key,
            };

            foreach (bool isDark in new[] { true, false })
            {
                var colors = ThemeService.GetPalette(accent, isDark)
                    .AsResources()
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                foreach (string key in shadeKeys)
                    Assert.True(colors.ContainsKey(key), $"{accent}/{isDark}: {key}");

                // Step one in each direction is the shade the app already tuned
                // for that interaction, so a Fluent control's hover and pressed
                // states land on the app's own colours.
                Assert.Equal(
                    colors[ThemeService.PrimaryButtonHoverBrushKey],
                    colors[ThemeService.SystemAccentColorLight1Key]);
                Assert.Equal(
                    colors[ThemeService.PrimaryButtonPressedBrushKey],
                    colors[ThemeService.SystemAccentColorDark1Key]);
            }
        }

        [Fact]
        public void TheShadeColoursArePublishedAsColorsNotBrushes()
        {
            // Fluent reads these as Colors. Publishing one as a brush silently
            // does nothing, which is exactly how the hover states went unnoticed.
            // Every colour-valued key must also be in the published set, or it
            // would be computed and then never written.
            Assert.Superset(
                ThemeService.AccentColorValuedKeys.ToHashSet(),
                ThemeService.AccentResourceKeys.ToHashSet());

            Assert.Contains(
                ThemeService.SystemAccentColorLight1Key,
                ThemeService.AccentColorValuedKeys);
            Assert.DoesNotContain(
                ThemeService.AccentBrushKey,
                ThemeService.AccentColorValuedKeys);
        }

        [Theory]
        [MemberData(nameof(Accents))]
        public void TheSelectionAndPreviewTints_FollowTheAccent(string accent)
        {
            foreach (bool isDark in new[] { true, false })
            {
                var colors = ThemeService.GetPalette(accent, isDark)
                    .AsResources()
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                Avalonia.Media.Color selection =
                    colors[ThemeService.AccentSelectionTintBrushKey];
                Avalonia.Media.Color preview =
                    colors[ThemeService.AccentPreviewTintBrushKey];
                Avalonia.Media.Color solid = colors[ThemeService.AccentBrushKey];

                // Same hue as the accent, differing only in how much of it shows.
                Assert.Equal(solid.R, selection.R);
                Assert.Equal(solid.G, selection.G);
                Assert.Equal(solid.B, selection.B);
                Assert.InRange(selection.A, 1, 254);
                Assert.True(preview.A > selection.A);
            }
        }

        [Fact]
        public void TokensAxaml_DeclaresNoAccentDrivenControlLiterals()
        {
            // These keys resolve through SystemAccentColor in stock Fluent. A
            // literal here shadows that path and pins the control to indigo for
            // everyone who picks another accent — the defect this file's
            // override list exists to prevent recurring.
            string[] forbidden =
            {
                "CheckBoxCheckBackgroundFillChecked",
                "CheckBoxCheckBackgroundFillCheckedPointerOver",
                "CheckBoxCheckBackgroundFillCheckedPressed",
                "RadioButtonOuterEllipseCheckedFill",
                "RadioButtonOuterEllipseCheckedStroke",
                "RadioButtonOuterEllipseCheckedFillPointerOver",
                "RadioButtonOuterEllipseCheckedStrokePointerOver",
                "SliderTrackValueFill",
                "SliderTrackValueFillPointerOver",
                "SliderTrackValueFillPressed",
                "SliderThumbBackground",
                "TabItemHeaderSelectedPipeFill",
                "ToggleSwitchFillOn",
            };

            string[] declared = XDocument
                .Load(FindAppFile(Path.Combine("Themes", "Tokens.axaml")))
                .Descendants()
                .Select(element => element.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value)
                .Where(key => key is not null)
                .Select(key => key!)
                .ToArray();

            string[] offenders = declared.Intersect(forbidden).OrderBy(key => key).ToArray();

            Assert.True(
                offenders.Length == 0,
                "Tokens.axaml pins accent-driven control states to a literal: " +
                string.Join(", ", offenders));

            // The disabled twins are deliberately fixed and must stay.
            Assert.Contains("CheckBoxCheckBackgroundFillCheckedDisabled", declared);
            Assert.Contains("SliderTrackValueFillDisabled", declared);
            Assert.Contains("RadioButtonOuterEllipseCheckedFillDisabled", declared);
        }

        [Theory]
        [MemberData(nameof(Accents))]
        public void EveryAccentsPrimaryButton_ClearsWcagAaAgainstItsInk(string accent)
        {
            // Button.primary paints OnAccentBrush on PrimaryButtonBrush at 14px
            // SemiBold, so the pair has to clear 4.5:1 in both variants. The
            // shipped indigo does; an accent that does not would be a
            // regression introduced by the accent system itself.
            foreach (bool isDark in new[] { true, false })
            {
                ThemeService.AccentPalette palette =
                    ThemeService.GetPalette(accent, isDark);

                // OnAccentBrush is white in both variants (Tokens.axaml).
                double ratio = ContrastRatio(
                    palette.PrimaryButton, Avalonia.Media.Color.Parse("#FFFFFF"));

                Assert.True(
                    ratio >= 4.5,
                    $"{accent}/{(isDark ? "dark" : "light")}: primary button " +
                    $"{palette.PrimaryButton} against white is {ratio:F2}:1.");
            }
        }

        // WCAG 2.x relative luminance and contrast ratio.
        private static double ContrastRatio(
            Avalonia.Media.Color first,
            Avalonia.Media.Color second)
        {
            double a = RelativeLuminance(first);
            double b = RelativeLuminance(second);

            return a > b
                ? (a + 0.05) / (b + 0.05)
                : (b + 0.05) / (a + 0.05);
        }

        private static double RelativeLuminance(Avalonia.Media.Color color) =>
            (0.2126 * Channel(color.R)) +
            (0.7152 * Channel(color.G)) +
            (0.0722 * Channel(color.B));

        private static double Channel(byte value)
        {
            double channel = value / 255.0;

            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        private static string FindAppFile(string relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
                    return Path.Combine(directory.FullName, "GameSaves.App", relativePath);

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Manager.sln was not found.");
        }
    }
}
