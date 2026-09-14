using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia.Media;
using GameSaves.App.Services;
using Xunit;

namespace GameSaves.Tests
{
    public sealed class WindowMaterialContrastShieldTests
    {
        [Fact]
        public void EffectiveWindowSurfaceOpacity_AcrylicDark_ReturnsContrastShieldOpacity()
        {
            double opacity = ThemeService.EffectiveWindowSurfaceOpacity(
                opacity: 0.2,
                highContrast: false,
                materialActive: true,
                material: AppUiSettings.MaterialAcrylic,
                isDark: true);

            Assert.Equal(ThemeService.AcrylicDarkContrastShieldOpacity, opacity);
            Assert.Equal(0.75, opacity);
        }

        [Theory]
        [InlineData(AppUiSettings.MaterialMica, true, 0.0)]
        [InlineData(AppUiSettings.MaterialMica, false, 0.0)]
        [InlineData(AppUiSettings.MaterialAcrylic, false, 0.0)]
        public void EffectiveWindowSurfaceOpacity_MicaOrLight_ReturnsTransparent(
            string material, bool isDark, double expected)
        {
            double opacity = ThemeService.EffectiveWindowSurfaceOpacity(
                opacity: 0.3,
                highContrast: false,
                materialActive: true,
                material: material,
                isDark: isDark);

            Assert.Equal(expected, opacity);
        }

        [Theory]
        [InlineData(AppUiSettings.MaterialNone, true, 0.4, 0.4)]
        [InlineData(AppUiSettings.MaterialNone, false, 0.8, 0.8)]
        public void EffectiveWindowSurfaceOpacity_MaterialNone_PreservesConfiguredWindowOpacity(
            string material, bool isDark, double stored, double expected)
        {
            double opacity = ThemeService.EffectiveWindowSurfaceOpacity(
                opacity: stored,
                highContrast: false,
                materialActive: false,
                material: material,
                isDark: isDark);

            Assert.Equal(expected, opacity);
        }

        [Theory]
        [InlineData(AppUiSettings.MaterialAcrylic, true)]
        [InlineData(AppUiSettings.MaterialAcrylic, false)]
        [InlineData(AppUiSettings.MaterialMica, true)]
        [InlineData(AppUiSettings.MaterialMica, false)]
        [InlineData(AppUiSettings.MaterialNone, true)]
        public void EffectiveWindowSurfaceOpacity_HighContrast_AlwaysForcesOpaque(
            string material, bool isDark)
        {
            double opacity = ThemeService.EffectiveWindowSurfaceOpacity(
                opacity: 0.2,
                highContrast: true,
                materialActive: true,
                material: material,
                isDark: isDark);

            Assert.Equal(1.0, opacity);
        }

        [Fact]
        public void Tokens_DeclaresBackdropContrastShieldBrush_InDarkAndLightVariants()
        {
            IReadOnlyDictionary<string, Color> dark = VariantColors("Dark");
            IReadOnlyDictionary<string, Color> light = VariantColors("Light");

            Assert.True(dark.ContainsKey(ThemeService.BackdropContrastShieldBrushKey));
            Assert.True(light.ContainsKey(ThemeService.BackdropContrastShieldBrushKey));

            // Dark variant: #111217 at 75% opacity (0xBF = 191)
            Color darkShield = dark[ThemeService.BackdropContrastShieldBrushKey];
            Assert.Equal(191, darkShield.A);
            Assert.Equal(17, darkShield.R);
            Assert.Equal(18, darkShield.G);
            Assert.Equal(23, darkShield.B);

            // Light variant: Transparent
            Color lightShield = light[ThemeService.BackdropContrastShieldBrushKey];
            Assert.Equal(0, lightShield.A);
        }

        [Fact]
        public void AcrylicDarkContrastShield_OverWhiteBackdrop_Exceeds7To1ContrastRatio()
        {
            IReadOnlyDictionary<string, Color> tokens = VariantColors("Dark");
            Color pageBase = tokens[ThemeService.PageBackgroundBrushKey];
            Color primaryText = tokens["PrimaryTextBrush"];
            Color whiteDesktop = Color.Parse("#FFFFFF");

            // Without contrast shield (0.0 opacity over pure white desktop blur):
            Color unshieldedBackdrop = whiteDesktop;
            double unshieldedContrast = ContrastRatio(unshieldedBackdrop, primaryText);
            Assert.True(unshieldedContrast < 2.0, $"Unshielded contrast {unshieldedContrast:F2}:1 is illegible.");

            // With contrast shield (#111217 at 75% opacity over pure white desktop blur):
            Color shieldedBackdrop = Composite(pageBase, whiteDesktop, ThemeService.AcrylicDarkContrastShieldOpacity);
            double shieldedContrast = ContrastRatio(shieldedBackdrop, primaryText);

            // WCAG AAA threshold for normal reading text is 7:1
            Assert.True(
                shieldedContrast >= 7.0,
                $"Backdrop contrast shield must exceed 7:1 over white backdrop. Measured: {shieldedContrast:F2}:1");
        }

        [Fact]
        public void ThemeService_WithOpacity_ComputesAccurate75PercentAlpha()
        {
            Color baseColor = Color.Parse("#111217");
            Color shielded = ThemeService.WithOpacity(baseColor, ThemeService.AcrylicDarkContrastShieldOpacity);

            Assert.Equal(191, shielded.A);
            Assert.Equal(baseColor.R, shielded.R);
            Assert.Equal(baseColor.G, shielded.G);
            Assert.Equal(baseColor.B, shielded.B);
        }

        private static IReadOnlyDictionary<string, Color> VariantColors(string variant)
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
                    element => Color.Parse(element.Attribute("Color")!.Value));
        }

        private static Color Composite(Color surface, Color backdrop, double alpha) =>
            Color.FromRgb(
                (byte)Math.Round((alpha * surface.R) + ((1 - alpha) * backdrop.R)),
                (byte)Math.Round((alpha * surface.G) + ((1 - alpha) * backdrop.G)),
                (byte)Math.Round((alpha * surface.B) + ((1 - alpha) * backdrop.B)));

        private static double ContrastRatio(Color first, Color second)
        {
            double a = RelativeLuminance(first);
            double b = RelativeLuminance(second);

            return a > b
                ? (a + 0.05) / (b + 0.05)
                : (b + 0.05) / (a + 0.05);
        }

        private static double RelativeLuminance(Color color) =>
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
