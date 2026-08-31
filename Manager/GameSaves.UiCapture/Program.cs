using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameSaves.App;
using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Platform;
using GameSaves.Core.Save;
using GameSaves.Core.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.UiCapture
{
    // Deterministic screenshot harness. Boots the real App class (so the real
    // styles and theme resources load) on the headless platform with Skia
    // rendering, composes the real DI graph against a throwaway temporary
    // database and a Steam locator that finds nothing, and writes one PNG per
    // (tab, theme, window size) combination. Real user data is never read:
    // the database path, Steam root, and therefore every list in every view
    // are fully synthetic, which also keeps machine-specific names out of the
    // captures handed to review agents.
    public static class Program
    {
        // Found by HeadlessUnitTestSession.StartNew(typeof(Program)).
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<GameSaves.App.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false,
                })
                .WithInterFont();

        private static readonly string[] TabSlugs =
        {
            "dashboard",
            "installed-games",
            "profiles",
            "transfer-preview",
            "manual-backup",
            "backups",
            "sync",
            "history",
            "settings",
        };

        private static readonly (string Slug, int Width, int Height)[] Sizes =
        {
            ("narrow", 800, 600),
            ("wide", 1400, 900),
        };

        // Set by the "layout" argument: capture only the workspace-layout
        // acceptance matrix, so a layout change can be reviewed without
        // rewriting the product-look captures the rest of this harness owns.
        private static bool _layoutOnly;

        // Set by the "rail" argument: capture only the navigation rail's
        // Scan/Refresh action across every page and rail arrangement.
        private static bool _railOnly;

        public static int Main(string[] args)
        {
            _layoutOnly = args.Length > 1 &&
                string.Equals(args[1], "layout", StringComparison.OrdinalIgnoreCase);
            _railOnly = args.Length > 1 &&
                string.Equals(args[1], "rail", StringComparison.OrdinalIgnoreCase);

            string outputDirectory = args.Length > 0
                ? args[0]
                : Path.Combine("artifacts", "ui-captures");
            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                "gamesave-ui-capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            // Work from inside the temporary directory and hand the app a
            // relative database path, so no absolute local path (which embeds
            // the account name) can appear in any captured pixel.
            string originalDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = tempRoot;

            try
            {
                using HeadlessUnitTestSession session =
                    HeadlessUnitTestSession.StartNew(typeof(Program));

                int written = session
                    .Dispatch(() => CaptureAllAsync(outputDirectory),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                Console.WriteLine($"Wrote {written} captures.");
                return 0;
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                // The only local deletion permitted is a temporary directory
                // this run itself created.
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

        private static async Task<int> CaptureAllAsync(
            string outputDirectory)
        {
            using ServiceProvider provider = AppServices.Build(services =>
            {
                services.AddSingleton<IAppDatabasePathProvider>(
                    new GameSaves.Infrastructure.Platform
                        .SchemaInitializingAppDatabasePathProvider(
                            new FixedDatabasePathProvider("capture.db")));
                services.AddSingleton<ISteamRootLocator>(
                    new NoSteamRootLocator());
                // The discovery service falls back to scanning well-known
                // install directories even when the locator finds nothing,
                // which would put this machine's real game names into the
                // captures. The fallback must find nothing either.
                services.AddSingleton<ISteamFallbackScanner>(
                    new NoSteamFallbackScanner());
                services.AddSingleton<IUiSettingsStore>(
                    new UiSettingsStore("ui-settings.json"));
            });

            MainWindowViewModel viewModel =
                provider.GetRequiredService<MainWindowViewModel>();
            var window = new GameSaves.App.Views.MainWindow
            {
                DataContext = viewModel,
            };
            // Subpixel (LCD) text antialiasing writes coloured fringes into
            // the PNGs, which review agents then report as words changing
            // colour mid-sentence. Greyscale antialiasing renders what a
            // human perceives on a real display.
            Avalonia.Media.TextOptions.SetTextRenderingMode(
                window, Avalonia.Media.TextRenderingMode.Antialias);
            window.Show();

            // The real app applies the theme during framework initialization
            // (App.axaml.cs). The harness builds its own service graph, so it
            // must do the same or every capture renders the raw token values
            // with no accent, transparency, text-scale or motion overrides —
            // which is not what any user ever sees.
            var themeService = provider.GetRequiredService<ThemeService>();
            IUiSettingsStore settingsStore = provider.GetRequiredService<IUiSettingsStore>();
            themeService.Apply(settingsStore.Load());

            IStartupInitializer initializer =
                provider.GetRequiredService<IStartupInitializer>();
            await initializer.InitializeAllAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            TabControl tabs = window
                .GetVisualDescendants()
                .OfType<TabControl>()
                .First();

            int written = 0;

            if (_railOnly)
            {
                return RailScanSweep.Run(
                    window,
                    tabs,
                    viewModel,
                    outputDirectory,
                    name => Shot(window, outputDirectory, name));
            }

            if (_layoutOnly)
            {
                // The table acceptance cases are about columns, so the layout
                // sweep runs against populated rows rather than an empty state.
                PopulateInstalledGames(viewModel.InstalledGames);
                viewModel.IsSteamMissing = false;

                return LayoutSweep.Run(
                    window,
                    tabs,
                    viewModel,
                    outputDirectory,
                    name => Shot(window, outputDirectory, name));
            }

            foreach (ThemeVariant theme in new[]
                { ThemeVariant.Light, ThemeVariant.Dark })
            {
                // Application-level, so ThemeService's overrides (written into
                // Application.Resources) are recomputed for this variant. A
                // window-level variant alone would pair one variant's surfaces
                // with the other variant's accent.
                Application.Current!.RequestedThemeVariant = theme;
                window.RequestedThemeVariant = theme;
                string themeSlug = theme == ThemeVariant.Dark ? "dark" : "light";

                foreach ((string sizeSlug, int width, int height) in Sizes)
                {
                    window.Width = width;
                    window.Height = height;

                    for (int tab = 0; tab < TabSlugs.Length; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        Dispatcher.UIThread.RunJobs();

                        string fileName =
                            $"{tab:00}-{TabSlugs[tab]}_{themeSlug}_{sizeSlug}_empty.png";
                        using var frame = window.CaptureRenderedFrame();
                        if (frame is null)
                        {
                            throw new InvalidOperationException(
                                "Headless rendering produced no frame; " +
                                "check that Skia rendering is enabled.");
                        }

                        frame.Save(
                            Path.Combine(outputDirectory, fileName),
                            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        written++;
                    }
                }
            }

            // Accent sweep. Every accent-driven control state in the product —
            // radios, checkboxes, toggle switches, slider tracks and thumbs,
            // the selected inner tab's underline and the selected rail
            // section — is on one of these two pages, so one capture per
            // accent per variant is enough to prove the accent actually
            // reaches them instead of stopping at the eight semantic tokens.
            foreach (string accent in new[]
            {
                AppUiSettings.AccentIndigo,
                AppUiSettings.AccentTeal,
                AppUiSettings.AccentRose,
                AppUiSettings.AccentAmber,
                AppUiSettings.AccentViolet,
            })
            {
                foreach (ThemeVariant theme in new[]
                    { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    // The variant must be set on the Application, not the
                    // window: ThemeService computes its overrides against
                    // Application.ActualThemeVariant and writes them into
                    // Application.Resources, which outrank a window-level
                    // variant. Setting only the window would pair one
                    // variant's surfaces with the other variant's text.
                    Application.Current!.RequestedThemeVariant = theme;
                    window.RequestedThemeVariant = theme;
                    string themeSlug = theme == ThemeVariant.Dark ? "dark" : "light";

                    themeService.Apply(
                        settingsStore.Load() with { AccentTheme = accent });

                    window.Width = 1400;
                    window.Height = 900;
                    tabs.SelectedIndex = 8;
                    Dispatcher.UIThread.RunJobs();

                    using var accentFrame = window.CaptureRenderedFrame();
                    if (accentFrame is null)
                    {
                        throw new InvalidOperationException(
                            "Headless rendering produced no accent frame.");
                    }

                    accentFrame.Save(
                        Path.Combine(
                            outputDirectory,
                            $"accent-{accent}_{themeSlug}_settings.png"),
                        new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    written++;
                }
            }

            // Back to the shipped accent so the remaining captures are the
            // product's default look.
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            themeService.Apply(settingsStore.Load());

            written += CaptureWorkspaceStates(window, tabs, viewModel, outputDirectory);

            PopulateInstalledGames(viewModel.InstalledGames);
            // Populated rows imply Steam was found; leaving the missing flag
            // set would render a banner contradicting the table (round 33).
            viewModel.IsSteamMissing = false;
            tabs.SelectedIndex = 1;

            foreach (ThemeVariant theme in new[]
                { ThemeVariant.Light, ThemeVariant.Dark })
            {
                // Application-level, so ThemeService's overrides (written into
                // Application.Resources) are recomputed for this variant. A
                // window-level variant alone would pair one variant's surfaces
                // with the other variant's accent.
                Application.Current!.RequestedThemeVariant = theme;
                window.RequestedThemeVariant = theme;
                string themeSlug = theme == ThemeVariant.Dark ? "dark" : "light";

                foreach ((string sizeSlug, int width, int height) in Sizes)
                {
                    window.Width = width;
                    window.Height = height;
                    Dispatcher.UIThread.RunJobs();

                    string fileName =
                        $"01-installed-games_{themeSlug}_{sizeSlug}_populated.png";
                    using var frame = window.CaptureRenderedFrame();
                    if (frame is null)
                    {
                        throw new InvalidOperationException(
                            "Headless rendering produced no populated frame.");
                    }

                    frame.Save(
                        Path.Combine(outputDirectory, fileName),
                        new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    written++;
                }
            }

            return written;
        }

        // The workspace layout is state, not a static page, so a screenshot of
        // the default arrangement proves almost nothing on its own. These
        // captures walk the states a reviewer actually has to judge: a section
        // collapsed, a section docked to another region, a section hidden, and
        // then the same page after Reset — which must come back identical to
        // the default capture taken earlier in this run.
        private static int CaptureWorkspaceStates(
            GameSaves.App.Views.MainWindow window,
            TabControl tabs,
            MainWindowViewModel viewModel,
            string outputDirectory)
        {
            IWorkspaceLayoutPage layout = viewModel.Workspace;
            IReadOnlyList<WorkspacePanelDefinition> panels =
                WorkspaceLayoutCatalog.PanelsFor(UiRailLayoutSettings.TabDashboard);

            // Steam is absent in the harness, so the stat sections are hidden
            // by their own bindings; drive the sections that are actually on
            // screen in that state.
            viewModel.IsSteamMissing = true;

            window.Width = 1400;
            window.Height = 900;
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            int written = 0;

            written += Shot(window, outputDirectory, "workspace-00-default");

            layout.SetCollapsed(panels[2].Key, true);
            written += Shot(window, outputDirectory, "workspace-01-collapsed");

            layout.MovePanel(panels[2].Key, UiPanelRegion.Left, int.MaxValue);
            written += Shot(window, outputDirectory, "workspace-02-docked-left");

            layout.SetCollapsed(panels[2].Key, false);
            layout.SetHidden(panels[2].Key, true);
            written += Shot(window, outputDirectory, "workspace-03-hidden");

            layout.ResetPage();
            written += Shot(window, outputDirectory, "workspace-04-reset");

            // 1080p is the stated target, so the default layout is also
            // captured at exactly that content width.
            window.Width = 1920;
            window.Height = 1080;
            Dispatcher.UIThread.RunJobs();
            written += Shot(window, outputDirectory, "workspace-05-reset-1080p");

            window.Width = 1400;
            window.Height = 900;
            viewModel.IsSteamMissing = false;
            Dispatcher.UIThread.RunJobs();

            return written;
        }

        private static int Shot(Window window, string outputDirectory, string name)
        {
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            if (frame is null)
                throw new InvalidOperationException($"No frame for {name}.");

            frame.Save(
                Path.Combine(outputDirectory, name + ".png"),
                new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

            return 1;
        }

        private static void PopulateInstalledGames(InstalledGamesViewModel viewModel)
        {
            viewModel.Games.Clear();
            viewModel.Games.Add(Game(
                "107410", "Arma 3", "SteamLibrary/steamapps/common/Arma 3",
                "SteamLibrary", 3, 0, 0, true, 42, 157286400, "Ready"));
            viewModel.Games.Add(Game(
                "220", "Half-Life 2", "SteamLibrary/steamapps/common/Half-Life 2",
                "SteamLibrary", 1, 1, 0, true, 8, 4194304, "Review pending"));
            viewModel.Games.Add(Game(
                "999001", "A Long Game Title Used To Prove Column Alignment",
                "ArchiveLibrary/steamapps/common/A Long Game Title",
                "ArchiveLibrary", 0, 2, 1, false, 0, 0, "Needs attention"));
            viewModel.Games.Add(Game(
                "730", "Counter-Strike 2", "FastLibrary/steamapps/common/Counter-Strike 2",
                "FastLibrary", 2, 0, 0, true, 16, 67108864, "Ready"));
            viewModel.SelectedGame = viewModel.Games[0];
            viewModel.StatusMessage = "4 installed games found.";
        }

        private static InstalledGameRowViewModel Game(
            string appId,
            string name,
            string gamePath,
            string libraryPath,
            int approved,
            int pending,
            int needsFix,
            bool savePathExists,
            int fileCount,
            long totalBytes,
            string status)
        {
            return new InstalledGameRowViewModel(new InstalledGameSaveStatus(
                new SteamGame(
                    appId,
                    name,
                    name,
                    libraryPath,
                    $"manifests/{appId}.acf",
                    gamePath,
                    FolderExists: true,
                    SteamDiscoveryConfidence.High),
                needsFix > 0
                    ? GameSaveStatusKind.NeedsFixOnly
                    : pending > 0
                        ? GameSaveStatusKind.MappingMissing
                        : GameSaveStatusKind.Ready,
                status,
                approved,
                pending,
                needsFix,
                savePathExists,
                fileCount,
                totalBytes,
                Array.Empty<SavePathVerificationResult>(),
                Error: null));
        }

        private sealed class FixedDatabasePathProvider : IAppDatabasePathProvider
        {
            private readonly string _path;

            public FixedDatabasePathProvider(string path) => _path = path;

            public string GetDatabasePath() => _path;
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

        private sealed class NoSteamRootLocator : ISteamRootLocator
        {
            public bool TryLocate(out string steamPath)
            {
                steamPath = string.Empty;
                return false;
            }
        }
    }
}
