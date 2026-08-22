using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Applies user-selected accent palettes and per-component transparency
    /// on top of the active Light/Dark theme variant. Overrides are written
    /// to a single runtime <see cref="ResourceDictionary"/> merged after the
    /// token dictionaries, so variant switching still resolves base values
    /// from <c>Tokens.axaml</c> and calling <see cref="Apply"/> again after a
    /// variant switch recomputes everything against the new variant.
    /// </summary>
    public sealed class ThemeService
    {
        internal const string SystemAccentColorKey = "SystemAccentColor";
        internal const string AccentBrushKey = "AccentBrush";
        internal const string BrandBrushKey = "BrandBrush";
        internal const string AccentHoverBrushKey = "AccentHoverBrush";
        internal const string AccentPressedBrushKey = "AccentPressedBrush";
        internal const string PrimaryButtonBrushKey = "PrimaryButtonBrush";
        internal const string PrimaryButtonHoverBrushKey = "PrimaryButtonHoverBrush";
        internal const string PrimaryButtonPressedBrushKey = "PrimaryButtonPressedBrush";

        internal const string PageBackgroundBrushKey = "PageBackgroundBrush";
        internal const string PageBackgroundOpaqueBrushKey = "PageBackgroundOpaqueBrush";
        internal const string CardBackgroundBrushKey = "CardBackgroundBrush";
        internal const string CardBorderBrushKey = "CardBorderBrush";
        internal const string SubtleBorderBrushKey = "SubtleBorderBrush";
        internal const string SplitterBrushKey = "SplitterBrush";
        internal const string InsetBackgroundBrushKey = "InsetBackgroundBrush";
        internal const string DeepInsetBackgroundBrushKey = "DeepInsetBackgroundBrush";

        internal const string FontSizeMicroKey = "FontSizeMicro";
        internal const string FontSizeCaptionKey = "FontSizeCaption";
        internal const string FontSizeBodyKey = "FontSizeBody";
        internal const string FontSizeSubtitleKey = "FontSizeSubtitle";
        internal const string FontSizeBodyLargeKey = "FontSizeBodyLarge";
        internal const string FontSizeSubheadingKey = "FontSizeSubheading";
        internal const string FontSizeTitleKey = "FontSizeTitle";
        internal const string FontSizeH1Key = "FontSizeH1";
        internal const string FontSizeDisplayKey = "FontSizeDisplay";
        internal const string FontSizeStatKey = "FontSizeStat";

        internal const string ScrollBarOpacityTransitionsKey =
            "ScrollBarOpacityTransitions";

        /// <summary>
        /// The app's only animation: the scrollbar opacity fade in
        /// <c>Tokens.axaml</c>. Reduce Motion swaps the transition
        /// collection for an empty one, so state changes are instant.
        /// </summary>
        internal static readonly TimeSpan DefaultMotionDurationFast =
            TimeSpan.FromMilliseconds(150);

        internal const string DarkVariantKey = "Dark";
        internal const string LightVariantKey = "Light";

        private ResourceDictionary? _overrides;

        // Set by WindowMaterialService: true only while the platform has
        // confirmed it is compositing the requested window material. While
        // true the page background resolves fully transparent so the OS
        // material shows through; while false the opaque page colour is
        // never given up, so a denied or revoked material can never leave a
        // transparent window with nothing behind it.
        private bool _windowMaterialActive;

        /// <summary>
        /// See <see cref="WindowMaterialService"/>; only it may change this,
        /// and every change must be followed by <see cref="Apply"/>.
        /// </summary>
        internal void SetWindowMaterialActive(bool active) => _windowMaterialActive = active;

        /// <summary>
        /// One accent's colors for a single theme variant. Indigo reproduces
        /// the shipped <c>Tokens.axaml</c> values exactly, so the default
        /// look is unchanged.
        /// </summary>
        internal sealed record AccentPalette(
            Color SystemAccentColor,
            Color Accent,
            Color Brand,
            Color AccentHover,
            Color AccentPressed,
            Color PrimaryButton,
            Color PrimaryButtonHover,
            Color PrimaryButtonPressed)
        {
            public IEnumerable<KeyValuePair<string, Color>> AsResources()
            {
                yield return new(SystemAccentColorKey, SystemAccentColor);
                yield return new(AccentBrushKey, Accent);
                yield return new(BrandBrushKey, Brand);
                yield return new(AccentHoverBrushKey, AccentHover);
                yield return new(AccentPressedBrushKey, AccentPressed);
                yield return new(PrimaryButtonBrushKey, PrimaryButton);
                yield return new(PrimaryButtonHoverBrushKey, PrimaryButtonHover);
                yield return new(PrimaryButtonPressedBrushKey, PrimaryButtonPressed);
            }
        }

        internal static readonly IReadOnlyList<string> AccentResourceKeys = new[]
        {
            SystemAccentColorKey,
            AccentBrushKey,
            BrandBrushKey,
            AccentHoverBrushKey,
            AccentPressedBrushKey,
            PrimaryButtonBrushKey,
            PrimaryButtonHoverBrushKey,
            PrimaryButtonPressedBrushKey,
        };

        // Indigo entries must match Tokens.axaml byte for byte; the tests
        // pin them against hardcoded expected values.
        private static readonly IReadOnlyDictionary<string, AccentPalette> DarkAccents =
            new Dictionary<string, AccentPalette>
            {
                [AppUiSettings.AccentIndigo] = new(
                    SystemAccentColor: Color.Parse("#4F6EDB"),
                    Accent: Color.Parse("#7C9CFF"),
                    Brand: Color.Parse("#A9BCFF"),
                    AccentHover: Color.Parse("#93AEFF"),
                    AccentPressed: Color.Parse("#6B8BF0"),
                    PrimaryButton: Color.Parse("#4F6EDB"),
                    PrimaryButtonHover: Color.Parse("#5B7BE8"),
                    PrimaryButtonPressed: Color.Parse("#3F5CC4")),
                [AppUiSettings.AccentTeal] = new(
                    SystemAccentColor: Color.Parse("#0F9D8F"),
                    Accent: Color.Parse("#2DD4BF"),
                    Brand: Color.Parse("#7EE8DB"),
                    AccentHover: Color.Parse("#4CDCCB"),
                    AccentPressed: Color.Parse("#17B5A4"),
                    PrimaryButton: Color.Parse("#0F9D8F"),
                    PrimaryButtonHover: Color.Parse("#17AFA0"),
                    PrimaryButtonPressed: Color.Parse("#0D8A7E")),
                [AppUiSettings.AccentRose] = new(
                    SystemAccentColor: Color.Parse("#DC264F"),
                    Accent: Color.Parse("#FB7185"),
                    Brand: Color.Parse("#FECDD3"),
                    AccentHover: Color.Parse("#FC8B9B"),
                    AccentPressed: Color.Parse("#EF5D74"),
                    PrimaryButton: Color.Parse("#DC264F"),
                    PrimaryButtonHover: Color.Parse("#E63D63"),
                    PrimaryButtonPressed: Color.Parse("#C41F45")),
                [AppUiSettings.AccentAmber] = new(
                    SystemAccentColor: Color.Parse("#D97706"),
                    Accent: Color.Parse("#FBBF24"),
                    Brand: Color.Parse("#FDE68A"),
                    AccentHover: Color.Parse("#FCC63D"),
                    AccentPressed: Color.Parse("#EBAF17"),
                    PrimaryButton: Color.Parse("#D97706"),
                    PrimaryButtonHover: Color.Parse("#E18408"),
                    PrimaryButtonPressed: Color.Parse("#C06705")),
                [AppUiSettings.AccentViolet] = new(
                    SystemAccentColor: Color.Parse("#8B5CF6"),
                    Accent: Color.Parse("#A78BFA"),
                    Brand: Color.Parse("#C4B5FD"),
                    AccentHover: Color.Parse("#B39DFB"),
                    AccentPressed: Color.Parse("#9674F8"),
                    PrimaryButton: Color.Parse("#8B5CF6"),
                    PrimaryButtonHover: Color.Parse("#9669F7"),
                    PrimaryButtonPressed: Color.Parse("#7C4AEF")),
            };

        private static readonly IReadOnlyDictionary<string, AccentPalette> LightAccents =
            new Dictionary<string, AccentPalette>
            {
                [AppUiSettings.AccentIndigo] = new(
                    SystemAccentColor: Color.Parse("#3557C7"),
                    Accent: Color.Parse("#3557C7"),
                    Brand: Color.Parse("#3557C7"),
                    AccentHover: Color.Parse("#2C4BB0"),
                    AccentPressed: Color.Parse("#243F99"),
                    PrimaryButton: Color.Parse("#3557C7"),
                    PrimaryButtonHover: Color.Parse("#2C4BB0"),
                    PrimaryButtonPressed: Color.Parse("#243F99")),
                [AppUiSettings.AccentTeal] = new(
                    SystemAccentColor: Color.Parse("#0F766E"),
                    Accent: Color.Parse("#0F766E"),
                    Brand: Color.Parse("#0F766E"),
                    AccentHover: Color.Parse("#0C635D"),
                    AccentPressed: Color.Parse("#0A524D"),
                    PrimaryButton: Color.Parse("#0F766E"),
                    PrimaryButtonHover: Color.Parse("#0C635D"),
                    PrimaryButtonPressed: Color.Parse("#0A524D")),
                [AppUiSettings.AccentRose] = new(
                    SystemAccentColor: Color.Parse("#BE123C"),
                    Accent: Color.Parse("#BE123C"),
                    Brand: Color.Parse("#BE123C"),
                    AccentHover: Color.Parse("#A51035"),
                    AccentPressed: Color.Parse("#8D0E2D"),
                    PrimaryButton: Color.Parse("#BE123C"),
                    PrimaryButtonHover: Color.Parse("#A51035"),
                    PrimaryButtonPressed: Color.Parse("#8D0E2D")),
                [AppUiSettings.AccentAmber] = new(
                    SystemAccentColor: Color.Parse("#B45309"),
                    Accent: Color.Parse("#B45309"),
                    Brand: Color.Parse("#B45309"),
                    AccentHover: Color.Parse("#9A4708"),
                    AccentPressed: Color.Parse("#823B06"),
                    PrimaryButton: Color.Parse("#B45309"),
                    PrimaryButtonHover: Color.Parse("#9A4708"),
                    PrimaryButtonPressed: Color.Parse("#823B06")),
                [AppUiSettings.AccentViolet] = new(
                    SystemAccentColor: Color.Parse("#7C3AED"),
                    Accent: Color.Parse("#7C3AED"),
                    Brand: Color.Parse("#7C3AED"),
                    AccentHover: Color.Parse("#6A31C8"),
                    AccentPressed: Color.Parse("#5828A6"),
                    PrimaryButton: Color.Parse("#7C3AED"),
                    PrimaryButtonHover: Color.Parse("#6A31C8"),
                    PrimaryButtonPressed: Color.Parse("#5828A6")),
            };

        /// <summary>Returns the palette for an accent in one variant, defaulting to indigo.</summary>
        internal static AccentPalette GetPalette(string accentTheme, bool isDark)
        {
            IReadOnlyDictionary<string, AccentPalette> palettes =
                isDark ? DarkAccents : LightAccents;

            return palettes.TryGetValue(accentTheme, out AccentPalette? palette)
                ? palette
                : palettes[AppUiSettings.AccentIndigo];
        }

        /// <summary>
        /// The semantic keys a high-contrast palette replaces. Accent keys are
        /// deliberately absent: high contrast composes with the chosen accent,
        /// which keeps ownership of interactive accent keys in
        /// <see cref="AccentPalette"/>.
        /// </summary>
        internal static readonly IReadOnlyList<string> HighContrastResourceKeys = new[]
        {
            PageBackgroundBrushKey,
            CardBackgroundBrushKey,
            InsetBackgroundBrushKey,
            DeepInsetBackgroundBrushKey,
            "EmptyStateCircleBrush",
            CardBorderBrushKey,
            SubtleBorderBrushKey,
            SplitterBrushKey,
            "PrimaryTextBrush",
            "SecondaryTextBrush",
            "MutedTextBrush",
            "PlaceholderInkBrush",
            "OnAccentBrush",
            "SuccessBrush",
            "WarningBrush",
            "WarningTintBrush",
            "WarningEdgeBrush",
            "DangerBrush",
            "ToggleButtonBackgroundPointerOver",
            "ToggleButtonBackgroundPressed",
            "ToggleButtonBackgroundCheckedPointerOver",
            "ToggleButtonBackgroundCheckedPressed",
            "ExpanderHeaderBackground",
            "ExpanderHeaderBackgroundPointerOver",
            "ExpanderHeaderBackgroundPressed",
            "ExpanderHeaderBorderBrush",
            "ExpanderContentBackground",
            "ExpanderContentBorderBrush",
            "TextControlPlaceholderForeground",
            "TextControlPlaceholderForegroundFocused",
            "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundDisabled",
            "ComboBoxPlaceHolderForeground",
            "ComboBoxPlaceHolderForegroundFocusedPressed",
            "ComboBoxForegroundDisabled",
        };

        // High-contrast surfaces follow the Windows high-contrast idiom:
        // solid near-black/near-white fills distinguished by strong borders
        // rather than by tinted surfaces, one full-strength ink for all text
        // tiers, and bright status colours. No value carries alpha.
        private static readonly IReadOnlyDictionary<string, Color> HighContrastDarkKeys =
            new Dictionary<string, Color>
            {
                [PageBackgroundBrushKey] = Color.Parse("#000000"),
                [CardBackgroundBrushKey] = Color.Parse("#000000"),
                [InsetBackgroundBrushKey] = Color.Parse("#000000"),
                [DeepInsetBackgroundBrushKey] = Color.Parse("#000000"),
                ["EmptyStateCircleBrush"] = Color.Parse("#2E2E2E"),
                [CardBorderBrushKey] = Color.Parse("#FFFFFF"),
                [SubtleBorderBrushKey] = Color.Parse("#FFFFFF"),
                [SplitterBrushKey] = Color.Parse("#FFFFFF"),
                ["PrimaryTextBrush"] = Color.Parse("#FFFFFF"),
                ["SecondaryTextBrush"] = Color.Parse("#FFFFFF"),
                ["MutedTextBrush"] = Color.Parse("#FFFFFF"),
                ["PlaceholderInkBrush"] = Color.Parse("#FFFFFF"),
                ["OnAccentBrush"] = Color.Parse("#FFFFFF"),
                ["SuccessBrush"] = Color.Parse("#16C60C"),
                ["WarningBrush"] = Color.Parse("#F9F1A5"),
                ["WarningTintBrush"] = Color.Parse("#000000"),
                ["WarningEdgeBrush"] = Color.Parse("#F9F1A5"),
                ["DangerBrush"] = Color.Parse("#E74856"),
                ["ToggleButtonBackgroundPointerOver"] = Color.Parse("#444444"),
                ["ToggleButtonBackgroundPressed"] = Color.Parse("#444444"),
                ["ToggleButtonBackgroundCheckedPointerOver"] = Color.Parse("#444444"),
                ["ToggleButtonBackgroundCheckedPressed"] = Color.Parse("#444444"),
                ["ExpanderHeaderBackground"] = Color.Parse("#000000"),
                ["ExpanderHeaderBackgroundPointerOver"] = Color.Parse("#444444"),
                ["ExpanderHeaderBackgroundPressed"] = Color.Parse("#444444"),
                ["ExpanderHeaderBorderBrush"] = Color.Parse("#FFFFFF"),
                ["ExpanderContentBackground"] = Color.Parse("#000000"),
                ["ExpanderContentBorderBrush"] = Color.Parse("#FFFFFF"),
                ["TextControlPlaceholderForeground"] = Color.Parse("#FFFFFF"),
                ["TextControlPlaceholderForegroundFocused"] = Color.Parse("#FFFFFF"),
                ["TextControlPlaceholderForegroundPointerOver"] = Color.Parse("#FFFFFF"),
                ["TextControlPlaceholderForegroundDisabled"] = Color.Parse("#FFFFFF"),
                ["ComboBoxPlaceHolderForeground"] = Color.Parse("#FFFFFF"),
                ["ComboBoxPlaceHolderForegroundFocusedPressed"] = Color.Parse("#FFFFFF"),
                ["ComboBoxForegroundDisabled"] = Color.Parse("#FFFFFF"),
            };

        private static readonly IReadOnlyDictionary<string, Color> HighContrastLightKeys =
            new Dictionary<string, Color>
            {
                [PageBackgroundBrushKey] = Color.Parse("#FFFFFF"),
                [CardBackgroundBrushKey] = Color.Parse("#FFFFFF"),
                [InsetBackgroundBrushKey] = Color.Parse("#FFFFFF"),
                [DeepInsetBackgroundBrushKey] = Color.Parse("#FFFFFF"),
                ["EmptyStateCircleBrush"] = Color.Parse("#D6D6D6"),
                [CardBorderBrushKey] = Color.Parse("#000000"),
                [SubtleBorderBrushKey] = Color.Parse("#000000"),
                [SplitterBrushKey] = Color.Parse("#000000"),
                ["PrimaryTextBrush"] = Color.Parse("#000000"),
                ["SecondaryTextBrush"] = Color.Parse("#000000"),
                ["MutedTextBrush"] = Color.Parse("#000000"),
                ["PlaceholderInkBrush"] = Color.Parse("#000000"),
                ["OnAccentBrush"] = Color.Parse("#FFFFFF"),
                ["SuccessBrush"] = Color.Parse("#107C10"),
                ["WarningBrush"] = Color.Parse("#7A6000"),
                ["WarningTintBrush"] = Color.Parse("#FFFFFF"),
                ["WarningEdgeBrush"] = Color.Parse("#7A6000"),
                ["DangerBrush"] = Color.Parse("#A4262C"),
                ["ToggleButtonBackgroundPointerOver"] = Color.Parse("#D6D6D6"),
                ["ToggleButtonBackgroundPressed"] = Color.Parse("#D6D6D6"),
                ["ToggleButtonBackgroundCheckedPointerOver"] = Color.Parse("#D6D6D6"),
                ["ToggleButtonBackgroundCheckedPressed"] = Color.Parse("#D6D6D6"),
                ["ExpanderHeaderBackground"] = Color.Parse("#FFFFFF"),
                ["ExpanderHeaderBackgroundPointerOver"] = Color.Parse("#D6D6D6"),
                ["ExpanderHeaderBackgroundPressed"] = Color.Parse("#D6D6D6"),
                ["ExpanderHeaderBorderBrush"] = Color.Parse("#000000"),
                ["ExpanderContentBackground"] = Color.Parse("#FFFFFF"),
                ["ExpanderContentBorderBrush"] = Color.Parse("#000000"),
                ["TextControlPlaceholderForeground"] = Color.Parse("#000000"),
                ["TextControlPlaceholderForegroundFocused"] = Color.Parse("#000000"),
                ["TextControlPlaceholderForegroundPointerOver"] = Color.Parse("#000000"),
                ["TextControlPlaceholderForegroundDisabled"] = Color.Parse("#000000"),
                ["ComboBoxPlaceHolderForeground"] = Color.Parse("#000000"),
                ["ComboBoxPlaceHolderForegroundFocusedPressed"] = Color.Parse("#000000"),
                ["ComboBoxForegroundDisabled"] = Color.Parse("#000000"),
            };

        /// <summary>
        /// The key/value overrides a high-contrast palette writes for one
        /// variant, or an empty table when high contrast is off.
        /// </summary>
        internal static IReadOnlyDictionary<string, Color> GetHighContrastOverrides(
            bool isDark,
            bool highContrast)
        {
            if (!highContrast)
                return new Dictionary<string, Color>();

            return isDark ? HighContrastDarkKeys : HighContrastLightKeys;
        }

        /// <summary>
        /// Applies the Light/Dark/system choice at the application level so
        /// every window follows it. "system" maps to the Default variant,
        /// which tracks the OS setting. Callers that change the variant must
        /// re-run <see cref="Apply"/> afterwards so accent and transparency
        /// overrides are recomputed against the newly active variant.
        /// </summary>
        public void ApplyThemeVariant(string themeChoice)
        {
            if (Application.Current is not { } application)
                return;

            application.RequestedThemeVariant = themeChoice switch
            {
                AppUiSettings.ThemeLight => ThemeVariant.Light,
                AppUiSettings.ThemeDark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        /// <summary>Scales a color's alpha channel by an opacity in [0,1], keeping hue.</summary>
        internal static Color WithOpacity(Color color, double opacity)
        {
            double clamped = UiTransparencySettings.NormalizeOpacity(opacity);

            byte alpha = (byte)Math.Round(
                color.A * clamped,
                MidpointRounding.AwayFromZero);

            return new Color(alpha, color.R, color.G, color.B);
        }

        // Base sizes must match the root FontSize tokens in Tokens.axaml
        // exactly; the tests pin the table against the XAML so a scale of
        // 1.0 always reproduces the shipped look byte for byte.
        internal static readonly IReadOnlyDictionary<string, double> FontSizeTokenBaseSizes =
            new Dictionary<string, double>
            {
                [FontSizeMicroKey] = 10.0,
                [FontSizeCaptionKey] = 11.0,
                [FontSizeBodyKey] = 12.0,
                [FontSizeSubtitleKey] = 13.0,
                [FontSizeBodyLargeKey] = 14.0,
                [FontSizeSubheadingKey] = 15.0,
                [FontSizeTitleKey] = 16.0,
                [FontSizeH1Key] = 18.0,
                [FontSizeDisplayKey] = 22.0,
                [FontSizeStatKey] = 32.0,
            };

        /// <summary>Applies a clamped text scale to one base font size.</summary>
        internal static double ScaledFontSize(double baseSize, double textScale) =>
            Math.Round(
                baseSize * UiAccessibilitySettings.ClampTextScale(textScale),
                3,
                MidpointRounding.AwayFromZero);

        /// <summary>
        /// The root FontSize token overrides for a text scale. A scale of 1.0
        /// yields the base table unchanged.
        /// </summary>
        internal static IReadOnlyDictionary<string, double> BuildTextScaleOverrides(
            double textScale) =>
            FontSizeTokenBaseSizes.ToDictionary(
                pair => pair.Key,
                pair => ScaledFontSize(pair.Value, textScale));

        /// <summary>
        /// The scrollbar fade duration for the reduce-motion choice: instant
        /// when motion is reduced, otherwise the shipped 150ms.
        /// </summary>
        internal static TimeSpan MotionDurationFast(bool reduceMotion) =>
            reduceMotion ? TimeSpan.Zero : DefaultMotionDurationFast;

        /// <summary>
        /// The transition collection published as the
        /// <c>ScrollBarOpacityTransitions</c> resource: the shipped 150ms
        /// opacity fade, or an empty collection (instant state changes) when
        /// motion is reduced. XAML cannot author this collection because a
        /// transition's property resolves only inside a style scope.
        /// </summary>
        internal static Transitions BuildScrollBarTransitions(bool reduceMotion)
        {
            if (reduceMotion)
                return new Transitions();

            return new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = MotionDurationFast(reduceMotion),
                },
            };
        }

        /// <summary>
        /// The opacity a surface background actually renders with: high
        /// contrast forces fully opaque regardless of the transparency
        /// settings, because translucent surfaces would dilute the contrast.
        /// </summary>
        internal static double EffectiveSurfaceOpacity(
            double opacity, bool highContrast) =>
            highContrast
                ? UiTransparencySettings.Opaque
                : UiTransparencySettings.NormalizeOpacity(opacity);

        /// <summary>
        /// The opacity the window-level page background renders with. While a
        /// material is compositing, the OS backdrop replaces the page
        /// background entirely, so it becomes fully transparent regardless of
        /// the (inert) window opacity setting. High contrast still wins: the
        /// material service never confirms a material under high contrast,
        /// and this stays defensive about it.
        /// </summary>
        internal static double EffectiveWindowSurfaceOpacity(
            double opacity, bool highContrast, bool materialActive) =>
            materialActive && !highContrast
                ? 0.0
                : EffectiveSurfaceOpacity(opacity, highContrast);

        public void Apply(AppUiSettings settings)
        {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));

            if (Application.Current is not { } application)
                return;

            bool isDark = application.ActualThemeVariant != ThemeVariant.Light;
            string variantKey = isDark ? DarkVariantKey : LightVariantKey;

            var overrides = new ResourceDictionary();

            AccentPalette palette = GetPalette(settings.AccentTheme, isDark);

            foreach ((string key, Color color) in palette.AsResources())
            {
                if (key == SystemAccentColorKey)
                    overrides[key] = color;
                else
                    overrides[key] = new ImmutableSolidColorBrush(color);
            }

            UiAccessibilitySettings accessibility = settings.Accessibility;

            // Text scale: override every root font-size token. Icon glyph
            // sizes stay literal in the views, so only reading text scales.
            foreach ((string key, double size) in BuildTextScaleOverrides(
                         accessibility.TextScale))
            {
                overrides[key] = size;
            }

            if (accessibility.HighContrast)
            {
                // High contrast composes with the accent choice above: it
                // replaces semantic surfaces, borders and text with solid
                // maximum-contrast values and ignores transparency entirely.
                foreach ((string key, Color color) in GetHighContrastOverrides(
                             isDark, highContrast: true))
                {
                    overrides[key] = new ImmutableSolidColorBrush(color);
                }

                // The material fallback colour stays solid and high-contrast
                // even if a stale window still carries a hint.
                overrides[PageBackgroundOpaqueBrushKey] = new ImmutableSolidColorBrush(
                    GetHighContrastOverrides(isDark, highContrast: true)[
                        PageBackgroundBrushKey]);
            }
            else
            {
                AddTransparencyOverride(
                    overrides, application, variantKey,
                    PageBackgroundBrushKey,
                    EffectiveWindowSurfaceOpacity(
                        settings.Transparency.Window,
                        accessibility.HighContrast,
                        _windowMaterialActive));
                AddTransparencyOverride(
                    overrides, application, variantKey,
                    CardBackgroundBrushKey,
                    EffectiveSurfaceOpacity(
                        settings.Transparency.Card, accessibility.HighContrast));
                AddTransparencyOverride(
                    overrides, application, variantKey,
                    InsetBackgroundBrushKey,
                    EffectiveSurfaceOpacity(
                        settings.Transparency.Inset, accessibility.HighContrast));
                AddTransparencyOverride(
                    overrides, application, variantKey,
                    DeepInsetBackgroundBrushKey,
                    EffectiveSurfaceOpacity(
                        settings.Transparency.Inset, accessibility.HighContrast));

                // Always-opaque page colour for the window template's
                // transparency fallback: it must never inherit the
                // transparency of PageBackgroundBrush itself, or a revoked
                // material would fall back to transparent (a black window).
                if (FindVariantColor(
                        application, variantKey, PageBackgroundBrushKey)
                    is { } opaquePageColor)
                {
                    overrides[PageBackgroundOpaqueBrushKey] =
                        new ImmutableSolidColorBrush(opaquePageColor);
                }
            }

            // Reduce motion: publish the scrollbar fade or its instant
            // replacement. Always written (the resource has no token
            // fallback) from the same table the tests pin.
            overrides[ScrollBarOpacityTransitionsKey] =
                BuildScrollBarTransitions(accessibility.ReduceMotion);

            if (_overrides is { } previous)
                application.Resources.MergedDictionaries.Remove(previous);

            _overrides = overrides;
            application.Resources.MergedDictionaries.Add(overrides);
        }

        // The base value comes from the variant's ThemeDictionary in the
        // token resources, never from this service's own overrides, so
        // reapplying never compound alpha changes.
        private static void AddTransparencyOverride(
            ResourceDictionary overrides,
            Application application,
            string variantKey,
            string brushKey,
            double opacity)
        {
            if (FindVariantColor(application, variantKey, brushKey) is not { } baseColor)
                return;

            overrides[brushKey] = new ImmutableSolidColorBrush(
                WithOpacity(baseColor, opacity));
        }

        private static Color? FindVariantColor(
            Application application,
            string variantKey,
            string resourceKey)
        {
            // Tokens.axaml keys its theme dictionaries by ThemeVariant
            // ("Dark"/"Light" convert through the XAML type converter).
            ThemeVariant variant = variantKey == DarkVariantKey
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            foreach (IResourceProvider merged in application.Resources.MergedDictionaries)
            {
                // Tokens.axaml is included through a ResourceInclude, whose
                // theme dictionaries live on its lazily loaded dictionary
                // rather than on the include itself. Direct dictionaries
                // (never the runtime overrides, which carry no theme
                // dictionaries) are read as-is.
                ResourceDictionary? dictionary = merged switch
                {
                    ResourceDictionary direct => direct,
                    ResourceInclude { Loaded: ResourceDictionary loaded } => loaded,
                    _ => null,
                };

                if (dictionary is null ||
                    !dictionary.ThemeDictionaries.TryGetValue(
                        variant, out IThemeVariantProvider? themed) ||
                    themed is not ResourceDictionary variantDictionary ||
                    !variantDictionary.TryGetValue(resourceKey, out object? value))
                {
                    continue;
                }

                if (value is Color color)
                    return color;

                if (value is ISolidColorBrush brush)
                    return brush.Color;
            }

            return null;
        }
    }
}
