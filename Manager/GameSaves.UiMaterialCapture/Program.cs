using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.App.Views;
using GameSaves.Core.Platform;
using GameSaves.Core.Steam;
using Microsoft.Extensions.DependencyInjection;
using GameSaves.UiCapture.Gallery;
using AppShell = GameSaves.App.App;

namespace GameSaves.UiMaterialCapture
{
    // Interactive Windows material harness. Runs the real application on the
    // real Win32 platform against a throwaway database, settings file, and a
    // Steam locator that finds nothing, drives the full material matrix
    // without restarting, and records what Windows actually composited:
    //
    //   * the requested transparency level and the level the platform granted
    //   * whether the navigation rail and the Settings category strip stay
    //     pixel-identical over a white and a black window underneath
    //   * whether the page surface visibly changes with what is behind it
    //     (or against the same layout with material "none")
    //
    // Every number comes from a screen read-back, because the OS composites
    // the backdrop; the app's own render never contains it.
    internal static class Program
    {
        private const int SettingsTabIndex = 8;

        // A navigation surface that is opaque cannot change at all when the
        // window behind it changes; a couple of stray pixels are allowed for
        // caret and focus animation only.
        private const double NavigationMeanTolerance = 0.5;
        private const double NavigationChangedTolerance = 0.002;

        // The page surface must move by clearly more than sampling noise
        // before a material counts as visible.
        private const double MaterialMeanThreshold = 1.0;
        private const double MaterialChangedThreshold = 0.02;

        private static readonly string[] Materials =
        {
            AppUiSettings.MaterialNone,
            AppUiSettings.MaterialAcrylic,
            AppUiSettings.MaterialMica,
        };

        private static readonly List<string> Report = new()
        {
            string.Join('\t',
                "capture", "material", "theme", "accent", "rail", "collapsed",
                "highContrast", "window", "backdrop", "requested", "actual",
                "pageAlpha", "navAlpha", "railSource", "railBleedMean", "railBleedChanged",
                "stripBleedMean", "stripBleedChanged", "contentBleedMean",
                "contentBleedChanged", "contentVsNoneMean", "verdict"),
        };

        private static readonly List<string> Failures = new();

        // Captures whose window never stopped changing; their numbers are
        // reported but cannot be trusted as evidence.
        private static readonly List<string> Unstable = new();
        private static readonly List<string> Fallbacks = new();

        // Material "none" captures, keyed by layout and backdrop, so a
        // material can be compared against the same layout with no material
        // at all. That is the only reliable Mica check: Mica samples the
        // desktop wallpaper, not the window underneath, so the white/black
        // bleed test alone cannot see it.
        private static readonly Dictionary<string, ScreenFrame> Baselines = new();

        private static int _exitCode;

        // "gallery" and "gallery-full" produce website/QA gallery output from
        // real Windows composition; anything else keeps the original material
        // regression sweep.
        private static string _mode = "material";

        // Read before the working directory moves into the throwaway folder.
        private static string _commit = "unknown";

        [STAThread]
        public static int Main(string[] args)
        {
            string outputDirectory = Path.GetFullPath(args.Length > 0
                ? args[0]
                : Path.Combine("artifacts", "ui-material-windows"));
            Directory.CreateDirectory(outputDirectory);

            _mode = args.Length > 1 ? args[1].ToLowerInvariant() : "material";
            _commit = GalleryManifest.CurrentCommit();

            string tempRoot = GalleryFixtureServices
                .CreateTemporaryRoot("material-capture");

            string originalDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tempRoot;

            try
            {
                // Database, UI settings, sync settings, Steam discovery and its
                // fallback scanner are replaced with throwaway equivalents, and
                // Google Drive authentication is never consulted. See
                // GalleryFixtureServices for what each override prevents.
                AppShell.ServiceOverrides = services =>
                    GalleryFixtureServices.Register(services);

                var lifetime = new ClassicDesktopStyleApplicationLifetime
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Args = args,
                };

                AppBuilder.Configure<AppShell>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .SetupWithLifetime(lifetime);

                bool gallery = _mode.StartsWith("gallery", StringComparison.Ordinal);

                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (gallery)
                            await GallerySweepAsync(lifetime, outputDirectory);
                        else
                            await SweepAsync(lifetime, outputDirectory);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        _exitCode = 3;
                    }
                    finally
                    {
                        lifetime.Shutdown();
                    }
                });

                lifetime.Start(args);

                if (gallery)
                    return _exitCode;

                File.WriteAllLines(
                    Path.Combine(outputDirectory, "windows-material-report.tsv"),
                    Report);

                Console.WriteLine();
                Console.WriteLine($"Report: {outputDirectory}");

                foreach (string fallback in Fallbacks)
                    Console.WriteLine($"PLATFORM FALLBACK: {fallback}");

                foreach (string failure in Failures)
                    Console.WriteLine("FAILED: " + failure);

                foreach (string capture in Unstable)
                    Console.WriteLine("UNSTABLE CAPTURE: " + capture);

                if (_exitCode == 0 && Failures.Count > 0)
                    _exitCode = 1;
                else if (_exitCode == 0 && Fallbacks.Count > 0)
                    _exitCode = 2;

                Console.WriteLine(_exitCode switch
                {
                    0 => "All rows passed: every requested material was granted, "
                        + "visible, and navigation stayed opaque.",
                    1 => "Application regression: see FAILED rows.",
                    2 => "Windows substituted a fallback: see PLATFORM FALLBACK rows.",
                    _ => "The capture run did not complete.",
                });

                return _exitCode;
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;

                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // Gallery mode. The archive half is subsampled by default so the run
        // occupies the screen for minutes rather than most of an hour; pass
        // "gallery-full" for every material cell the plan defines.
        private static async Task GallerySweepAsync(
            IClassicDesktopStyleApplicationLifetime lifetime,
            string outputDirectory)
        {
            var window = (MainWindow)lifetime.MainWindow!;
            var viewModel = (MainWindowViewModel)window.DataContext!;

            GalleryShowcase.Apply(viewModel);
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<GalleryScenario> curated =
                GalleryPlan.CuratedFor(GalleryEngines.WindowsScreenReadback);

            IReadOnlyList<GalleryScenario> archive = _mode == "gallery-full"
                ? GalleryPlan.FullFor(GalleryEngines.WindowsScreenReadback)
                : _mode == "gallery-curated"
                    ? Array.Empty<GalleryScenario>()
                    : GalleryPlan.MaterialArchiveSample();

            if (archive.Count > 0)
            {
                await GalleryMaterialSweep.RunAsync(
                    lifetime, outputDirectory, _commit, archive, "full");
            }

            await GalleryMaterialSweep.RunAsync(
                lifetime, outputDirectory, _commit, curated, "selected");
        }

        private static async Task SweepAsync(
            IClassicDesktopStyleApplicationLifetime lifetime,
            string outputDirectory)
        {
            var window = (MainWindow)lifetime.MainWindow!;
            var viewModel = (MainWindowViewModel)window.DataContext!;
            SettingsViewModel settings = viewModel.Settings;

            // Startup data loading and the first composed frame.
            await Settle(2500);

            Screen screen = window.Screens.Primary ?? window.Screens.All[0];
            double scale = screen.Scaling;
            PixelRect screenBounds = screen.Bounds;

            var backdrop = new Window
            {
                Title = "material backdrop",
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = Brushes.White,
                Width = screenBounds.Width / scale,
                Height = screenBounds.Height / scale,
            };

            backdrop.Show();
            backdrop.Position = screenBounds.Position;

            TabControl navigation = window
                .GetVisualDescendants()
                .OfType<TabControl>()
                .First();

            foreach (Case scenario in BuildCases())
            {
                Apply(settings, navigation, scenario);
                await Settle(400);

                Window target = window;

                if (scenario.Detached)
                {
                    // Side by side, so the detached window composites the
                    // backdrop rather than the main window.
                    double half = (screenBounds.Width / scale) / 2;
                    Place(window, screenBounds.Position, scale, 40, 40, half - 80, 620);
                    window.WorkspaceHost.ApplyDetachedTabs(new[]
                    {
                        UiDetachedWindowSettings.TryCreate(
                            UiRailLayoutSettings.TabBackups,
                            (screenBounds.X / scale) + half,
                            (screenBounds.Y / scale) + 40,
                            half - 80,
                            560)!,
                    });

                    await Settle(600);

                    // The floating window that actually carries the detached
                    // page. Applying a layout reattaches through the
                    // coordinator, which hands the content back but leaves the
                    // emptied window open, so an earlier case's window can
                    // still be around.
                    target = lifetime.Windows.LastOrDefault(
                            candidate => candidate != window &&
                                candidate != backdrop &&
                                candidate.Content is not null)
                        ?? throw new InvalidOperationException(
                            "The detached window was not created.");
                }
                else
                {
                    window.WorkspaceHost.ReattachAllDetachedTabs();
                    Place(window, screenBounds.Position, scale, 60, 50, 1280, 760);
                    await Settle(400);
                }

                await CaptureCaseAsync(
                    scenario, window, target, backdrop, navigation, outputDirectory);

                if (scenario.Detached)
                {
                    // Close it the way a user does, so the page returns to the
                    // rail and no emptied window is left behind the next case.
                    target.Close();
                    await Settle(500);
                }
            }

            // The representative desktop: no synthetic window underneath, so
            // Mica's wallpaper-derived backdrop is judged as a user sees it.
            foreach (Window leftover in lifetime.Windows
                .Where(candidate => candidate != window && candidate != backdrop)
                .ToArray())
            {
                leftover.Close();
            }

            window.WorkspaceHost.ReattachAllDetachedTabs();
            await Settle(500);
            backdrop.Hide();
            await Settle(500);

            foreach (string material in Materials)
            {
                var scenario = new Case(
                    $"{material}_dark_left_desktop",
                    material,
                    AppUiSettings.ThemeDark,
                    AppUiSettings.AccentIndigo,
                    UiRailLayoutSettings.PositionLeft,
                    Collapsed: false,
                    HighContrast: false,
                    Detached: false);

                Apply(settings, navigation, scenario);
                Place(window, screenBounds.Position, scale, 60, 50, 1280, 760);
                await Settle(700);

                await CaptureCaseAsync(
                    scenario, window, window, backdrop: null, navigation, outputDirectory);
            }

            backdrop.Close();
        }

        private static async Task CaptureCaseAsync(
            Case scenario,
            MainWindow window,
            Window target,
            Window? backdrop,
            TabControl navigation,
            string outputDirectory)
        {
            // Revisions before the navigation surface existed have no named
            // rail host to measure, so the same left strip is measured
            // geometrically instead. Both revisions then answer the same
            // question: does the desktop show through the navigation?
            PixelRect? rail = scenario.Detached
                ? null
                : ScreenFrame.RegionOf(
                    ScreenFrame.FindNamed(window, "PART_NavigationRail"), window);

            string railSource = rail is null ? "fallback-strip" : "rail-element";

            if (rail is null && !scenario.Detached &&
                scenario.Rail == UiRailLayoutSettings.PositionLeft)
            {
                double scale = window.RenderScaling;
                PixelPoint origin = window.PointToScreen(new Point(8, 120));

                rail = new PixelRect(
                    origin.X,
                    origin.Y,
                    (int)Math.Round(180 * scale),
                    (int)Math.Round((window.Bounds.Height - 200) * scale));
            }
            else if (rail is null)
            {
                railSource = "n/a";
            }

            PixelRect? strip = scenario.Detached
                ? null
                : ScreenFrame.RegionOf(FindSettingsStrip(window), window);

            PixelRect? content = scenario.Detached
                ? null
                : ScreenFrame.RegionOf(
                    navigation
                        .GetVisualDescendants()
                        .OfType<ContentPresenter>()
                        .FirstOrDefault(part => part.Name == "PART_SelectedContentHost"),
                    window);

            var frames = new List<(string Backdrop, ScreenFrame Frame)>();

            if (backdrop is null)
            {
                frames.Add(("desktop", await CaptureAsync(target, outputDirectory,
                    $"{scenario.Label}_desktop")));
            }
            else
            {
                foreach ((string slug, IBrush brush) in new (string, IBrush)[]
                {
                    ("white", Brushes.White),
                    ("black", Brushes.Black),
                })
                {
                    backdrop.Background = brush;
                    await Settle(450);
                    target.Activate();
                    await Settle(350);

                    frames.Add((slug, await CaptureAsync(
                        target, outputDirectory, $"{scenario.Label}_{slug}")));
                }
            }

            content ??= frames[0].Frame.Bounds;

            RegionDifference railBleed = RegionDifference.Empty;
            RegionDifference stripBleed = RegionDifference.Empty;
            RegionDifference contentBleed = RegionDifference.Empty;

            if (frames.Count == 2)
            {
                ScreenFrame white = frames[0].Frame;
                ScreenFrame black = frames[1].Frame;

                railBleed = rail is { } railRegion
                    ? white.Difference(black, railRegion)
                    : RegionDifference.Empty;
                stripBleed = strip is { } stripRegion
                    ? white.Difference(black, stripRegion)
                    : RegionDifference.Empty;
                contentBleed = white.Difference(black, content.Value);
            }

            WindowTransparencyLevel requested =
                target.TransparencyLevelHint.FirstOrDefault(
                    WindowTransparencyLevel.None);
            WindowTransparencyLevel actual = target.ActualTransparencyLevel;

            foreach ((string slug, ScreenFrame frame) in frames)
            {
                string key = BaselineKey(scenario, slug);
                RegionDifference versusNone = RegionDifference.Empty;

                if (scenario.Material == AppUiSettings.MaterialNone)
                    Baselines[key] = frame;
                else if (Baselines.TryGetValue(key, out ScreenFrame? baseline))
                    versusNone = frame.Difference(baseline, content.Value);

                string verdict = Verdict(
                    scenario, requested, actual,
                    railBleed, stripBleed, contentBleed, versusNone);

                Report.Add(string.Join('\t',
                    $"{scenario.Label}_{slug}",
                    scenario.Material,
                    scenario.Theme,
                    scenario.Accent,
                    scenario.Rail,
                    scenario.Collapsed,
                    scenario.HighContrast,
                    scenario.Detached ? "detached" : "main",
                    slug,
                    requested,
                    actual,
                    Alpha(target, "PageBackgroundBrush"),
                    Alpha(target, "NavigationSurfaceBrush"),
                    railSource,
                    Number(railBleed.Mean),
                    Number(railBleed.ChangedShare),
                    Number(stripBleed.Mean),
                    Number(stripBleed.ChangedShare),
                    Number(contentBleed.Mean),
                    Number(contentBleed.ChangedShare),
                    Number(versusNone.Mean),
                    verdict));

                Console.WriteLine(
                    $"{scenario.Label}_{slug}: requested={requested} actual={actual} " +
                    $"rail={Number(railBleed.Mean)} strip={Number(stripBleed.Mean)} " +
                    $"content={Number(contentBleed.Mean)} vsNone={Number(versusNone.Mean)} " +
                    $"-> {verdict}");

                if (verdict == "platform-fallback")
                    Fallbacks.Add($"{scenario.Label}_{slug}: requested {requested}, got {actual}");
                else if (verdict.StartsWith("fail", StringComparison.Ordinal))
                    Failures.Add($"{scenario.Label}_{slug}: {verdict}");
            }
        }

        private static string Verdict(
            Case scenario,
            WindowTransparencyLevel requested,
            WindowTransparencyLevel actual,
            RegionDifference railBleed,
            RegionDifference stripBleed,
            RegionDifference contentBleed,
            RegionDifference versusNone)
        {
            if (Leaks(railBleed))
                return "fail-navigation-rail-leaks";

            if (Leaks(stripBleed))
                return "fail-settings-strip-leaks";

            WindowTransparencyLevel expected = scenario.HighContrast
                ? WindowTransparencyLevel.None
                : scenario.Material switch
                {
                    AppUiSettings.MaterialAcrylic => WindowTransparencyLevel.AcrylicBlur,
                    AppUiSettings.MaterialMica => WindowTransparencyLevel.Mica,
                    _ => WindowTransparencyLevel.None,
                };

            if (requested != expected)
                return $"fail-requested-{requested}-expected-{expected}";

            if (expected == WindowTransparencyLevel.None)
            {
                // Nothing is requested, so the platform-reported level is
                // whatever Win32 considers its baseline (it reports
                // "Transparent" for an ordinary window). Only the pixels
                // decide: nothing behind the window may show through it.
                return Leaks(contentBleed)
                    ? "fail-opaque-window-leaks"
                    : "opaque-ok";
            }

            if (actual != requested)
                return "platform-fallback";

            bool visible =
                (contentBleed.Measured &&
                    (contentBleed.Mean > MaterialMeanThreshold ||
                        contentBleed.ChangedShare > MaterialChangedThreshold)) ||
                (versusNone.Measured &&
                    (versusNone.Mean > MaterialMeanThreshold ||
                        versusNone.ChangedShare > MaterialChangedThreshold));

            return visible ? "material-visible" : "fail-material-invisible";
        }

        private static bool Leaks(RegionDifference difference) =>
            difference.Measured &&
            (difference.Mean > NavigationMeanTolerance ||
                difference.ChangedShare > NavigationChangedTolerance);

        private static string BaselineKey(Case scenario, string backdrop) =>
            string.Join('|',
                scenario.Theme, scenario.Accent, scenario.Rail,
                scenario.Collapsed, scenario.HighContrast, scenario.Detached,
                backdrop);

        // A theme swap, an accent swap, and the DWM material cross-fade all
        // animate. Capturing on a fixed delay caught windows mid-transition
        // and reported the difference between two different frames as
        // background bleeding through, so a capture is only accepted once
        // two consecutive read-backs are the same picture.
        private static async Task<ScreenFrame> CaptureAsync(
            Window target, string outputDirectory, string name)
        {
            await Settle(200);

            ScreenFrame frame = ScreenFrame.CaptureWindow(target);

            for (int attempt = 0; attempt < 16; attempt++)
            {
                await Settle(250);

                ScreenFrame next = ScreenFrame.CaptureWindow(target);

                if (next.Bounds == frame.Bounds &&
                    next.Difference(frame, next.Bounds).Mean <= 0.05)
                {
                    frame = next;
                    break;
                }

                frame = next;

                if (attempt == 15)
                    Unstable.Add(name);
            }

            frame.Save(Path.Combine(outputDirectory, name + ".png"));

            return frame;
        }

        private static void Apply(
            SettingsViewModel settings, TabControl navigation, Case scenario)
        {
            settings.HighContrast = scenario.HighContrast;
            settings.ThemeChoice = scenario.Theme;
            settings.AccentTheme = scenario.Accent;
            settings.RailPosition = scenario.Rail;
            settings.RailCollapsed = scenario.Collapsed;
            settings.WindowMaterial = scenario.Material;

            // The Settings page carries both navigation surfaces under test:
            // the primary rail and the category strip.
            if (navigation.ItemCount > SettingsTabIndex)
                navigation.SelectedIndex = SettingsTabIndex;
        }

        private static void Place(
            Window window,
            PixelPoint screenOrigin,
            double scale,
            double x,
            double y,
            double width,
            double height)
        {
            window.WindowState = WindowState.Normal;
            window.Width = width;
            window.Height = height;
            window.Position = new PixelPoint(
                screenOrigin.X + (int)Math.Round(x * scale),
                screenOrigin.Y + (int)Math.Round(y * scale));
        }

        private static Visual? FindSettingsStrip(Visual root) =>
            root.GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault(control => control.Name == "SettingsCategories")
                ?.GetVisualDescendants()
                .OfType<WrapPanel>()
                .FirstOrDefault();

        private static string Alpha(Window window, string key) =>
            window.TryFindResource(key, window.ActualThemeVariant, out object? value) &&
            value is ISolidColorBrush brush
                ? brush.Color.A.ToString(CultureInfo.InvariantCulture)
                : "missing";

        private static string Number(double value) => value < 0
            ? "n/a"
            : value.ToString("0.###", CultureInfo.InvariantCulture);

        private static async Task Settle(int milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(milliseconds);
            Dispatcher.UIThread.RunJobs();
        }

        private static IEnumerable<Case> BuildCases()
        {
            // Every layout variant runs "none" first so the later materials
            // have a same-layout baseline to be compared against.
            foreach (string theme in new[]
            {
                AppUiSettings.ThemeDark,
                AppUiSettings.ThemeLight,
                AppUiSettings.ThemeSystem,
            })
            {
                foreach (string material in Materials)
                {
                    yield return new Case(
                        $"{material}_{theme}_left", material, theme,
                        AppUiSettings.AccentIndigo, UiRailLayoutSettings.PositionLeft,
                        Collapsed: false, HighContrast: false, Detached: false);
                }
            }

            foreach (string position in new[]
            {
                UiRailLayoutSettings.PositionRight,
                UiRailLayoutSettings.PositionTop,
            })
            {
                foreach (string material in Materials)
                {
                    yield return new Case(
                        $"{material}_dark_{position}", material, AppUiSettings.ThemeDark,
                        AppUiSettings.AccentIndigo, position,
                        Collapsed: false, HighContrast: false, Detached: false);
                }
            }

            foreach (string material in Materials)
            {
                yield return new Case(
                    $"{material}_dark_left_collapsed", material, AppUiSettings.ThemeDark,
                    AppUiSettings.AccentIndigo, UiRailLayoutSettings.PositionLeft,
                    Collapsed: true, HighContrast: false, Detached: false);
            }

            foreach (string material in Materials)
            {
                yield return new Case(
                    $"{material}_dark_highcontrast", material, AppUiSettings.ThemeDark,
                    AppUiSettings.AccentIndigo, UiRailLayoutSettings.PositionLeft,
                    Collapsed: false, HighContrast: true, Detached: false);
            }

            foreach (string material in Materials)
            {
                yield return new Case(
                    $"{material}_dark_detached", material, AppUiSettings.ThemeDark,
                    AppUiSettings.AccentIndigo, UiRailLayoutSettings.PositionLeft,
                    Collapsed: false, HighContrast: false, Detached: true);
            }

            foreach (string accent in new[]
            {
                AppUiSettings.AccentIndigo,
                AppUiSettings.AccentTeal,
                AppUiSettings.AccentRose,
                AppUiSettings.AccentAmber,
                AppUiSettings.AccentViolet,
            })
            {
                yield return new Case(
                    $"acrylic_dark_{accent}", AppUiSettings.MaterialAcrylic,
                    AppUiSettings.ThemeDark, accent, UiRailLayoutSettings.PositionLeft,
                    Collapsed: false, HighContrast: false, Detached: false);
            }
        }

        private sealed record Case(
            string Label,
            string Material,
            string Theme,
            string Accent,
            string Rail,
            bool Collapsed,
            bool HighContrast,
            bool Detached);

        private sealed class FixedDatabasePathProvider : IAppDatabasePathProvider
        {
            private readonly string _path;

            public FixedDatabasePathProvider(string path) => _path = path;

            public string GetDatabasePath() => _path;
        }

        private sealed class NoSteamRootLocator : ISteamRootLocator
        {
            public bool TryLocate(out string steamPath)
            {
                steamPath = string.Empty;
                return false;
            }
        }

        private sealed class NoSteamFallbackScanner : ISteamFallbackScanner
        {
            public SteamFallbackScanResult Scan(
                SteamDiscoveryOptions options,
                IProgress<SteamFallbackScanProgress>? progress = null,
                CancellationToken cancellationToken = default)
            {
                return new SteamFallbackScanResult();
            }
        }
    }
}
