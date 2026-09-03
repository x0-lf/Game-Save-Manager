using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;

namespace GameSaves.UiCapture
{
    // Headless proof for app-owned material semantics. OS blur, Mica, and the
    // effective transparency level still require the interactive Windows run
    // documented in docs/development.md.
    internal static class MaterialSweep
    {
        private static readonly string[] Materials =
        {
            AppUiSettings.MaterialNone,
            AppUiSettings.MaterialAcrylic,
            AppUiSettings.MaterialMica,
        };

        private static readonly string[] Themes =
        {
            AppUiSettings.ThemeDark,
            AppUiSettings.ThemeLight,
            AppUiSettings.ThemeSystem,
        };

        private static readonly string[] Accents =
        {
            AppUiSettings.AccentIndigo,
            AppUiSettings.AccentTeal,
            AppUiSettings.AccentRose,
            AppUiSettings.AccentAmber,
            AppUiSettings.AccentViolet,
        };

        private static readonly string[] Positions =
        {
            UiRailLayoutSettings.PositionLeft,
            UiRailLayoutSettings.PositionRight,
            UiRailLayoutSettings.PositionTop,
        };

        private static readonly (string Slug, Color Color)[] Backdrops =
        {
            ("bright", Colors.White),
            ("dark", Colors.Black),
        };

        private static readonly List<string> Report = new()
        {
            "capture\tmaterial\ttheme\taccent\tbackdrop\trail\tcollapsed\t" +
            "highContrast\tcomposition\trequested\tactual\tpageAlpha\tnavigationAlpha",
        };

        public static int Run(
            Window window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            ThemeService themeService,
            IUiSettingsStore settingsStore,
            string outputDirectory,
            Func<string, int> shot)
        {
            int written = 0;
            window.Width = 1366;
            window.Height = 768;
            tabs.SelectedIndex = 8;

            foreach (string material in Materials)
            foreach (string theme in Themes)
            foreach ((string backdrop, Color color) in Backdrops)
            foreach (string position in Positions)
            {
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    material, theme, AppUiSettings.AccentIndigo, backdrop, color,
                    position, collapsed: false, highContrast: false,
                    compositionActive: material != AppUiSettings.MaterialNone,
                    $"material-{material}_{theme}_{backdrop}_{position}");
            }

            foreach (string accent in Accents)
            foreach (string theme in new[]
                { AppUiSettings.ThemeDark, AppUiSettings.ThemeLight })
            {
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    AppUiSettings.MaterialAcrylic, theme, accent,
                    "dark", Colors.Black, UiRailLayoutSettings.PositionLeft,
                    collapsed: false, highContrast: false,
                    compositionActive: true,
                    $"material-acrylic_{theme}_{accent}");
            }

            foreach (string material in Materials)
            foreach (bool highContrast in new[] { false, true })
            {
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    material, AppUiSettings.ThemeDark, AppUiSettings.AccentIndigo,
                    "bright", Colors.White, UiRailLayoutSettings.PositionLeft,
                    collapsed: false, highContrast,
                    compositionActive:
                        material != AppUiSettings.MaterialNone && !highContrast,
                    $"material-{material}_contrast-{highContrast.ToString().ToLowerInvariant()}");
            }

            foreach (string material in new[]
                { AppUiSettings.MaterialAcrylic, AppUiSettings.MaterialMica })
            foreach ((string backdrop, Color color) in Backdrops)
            {
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    material, AppUiSettings.ThemeDark, AppUiSettings.AccentIndigo,
                    backdrop, color, UiRailLayoutSettings.PositionLeft,
                    collapsed: false, highContrast: false,
                    compositionActive: false,
                    $"material-{material}_unsupported_{backdrop}");
            }

            foreach (string material in Materials)
            foreach (string position in Positions)
            {
                written += Capture(
                    window, viewModel, themeService, settingsStore, shot,
                    material, AppUiSettings.ThemeDark, AppUiSettings.AccentIndigo,
                    "bright", Colors.White, position,
                    collapsed: true, highContrast: false,
                    compositionActive: material != AppUiSettings.MaterialNone,
                    $"material-{material}_{position}_collapsed");
            }

            File.WriteAllLines(
                Path.Combine(outputDirectory, "material-report.tsv"), Report);

            return written;
        }

        private static int Capture(
            Window window,
            MainWindowViewModel viewModel,
            ThemeService themeService,
            IUiSettingsStore settingsStore,
            Func<string, int> shot,
            string material,
            string theme,
            string accent,
            string backdrop,
            Color backdropColor,
            string position,
            bool collapsed,
            bool highContrast,
            bool compositionActive,
            string name)
        {
            viewModel.Settings.ThemeChoice = theme;
            viewModel.Settings.AccentTheme = accent;
            viewModel.Settings.WindowMaterial = material;
            viewModel.Settings.HighContrast = highContrast;
            viewModel.Settings.RailPosition = position;
            viewModel.Settings.RailCollapsed = collapsed;

            AppUiSettings settings = settingsStore.Load();
            themeService.SetWindowMaterialActive(compositionActive);
            themeService.Apply(settings);
            window.TransparencyBackgroundFallback = new SolidColorBrush(backdropColor);
            Dispatcher.UIThread.RunJobs();

            WindowTransparencyLevel requested =
                WindowMaterialService.TransparencyLevelForMaterial(
                    WindowMaterialService.EffectiveMaterial(settings));

            Report.Add(string.Join('\t',
                name,
                material,
                theme,
                accent,
                backdrop,
                position,
                collapsed,
                highContrast,
                compositionActive ? "simulated-supported" : "fallback",
                requested,
                "headless-unavailable",
                BrushAlpha(window, ThemeService.PageBackgroundBrushKey),
                BrushAlpha(window, ThemeService.NavigationSurfaceBrushKey)));

            return shot(name);
        }

        private static byte BrushAlpha(Window window, string key)
        {
            if (window.TryFindResource(
                    key, window.ActualThemeVariant, out object? value) &&
                value is ISolidColorBrush brush)
            {
                return brush.Color.A;
            }

            throw new InvalidOperationException($"Missing brush resource: {key}");
        }
    }
}
