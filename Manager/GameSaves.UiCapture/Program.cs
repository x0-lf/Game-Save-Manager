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

        public static int Main(string[] args)
        {
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

            IStartupInitializer initializer =
                provider.GetRequiredService<IStartupInitializer>();
            await initializer.InitializeAllAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            TabControl tabs = window
                .GetVisualDescendants()
                .OfType<TabControl>()
                .First();

            int written = 0;

            foreach (ThemeVariant theme in new[]
                { ThemeVariant.Light, ThemeVariant.Dark })
            {
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

            PopulateInstalledGames(viewModel.InstalledGames);
            // Populated rows imply Steam was found; leaving the missing flag
            // set would render a banner contradicting the table (round 33).
            viewModel.IsSteamMissing = false;
            tabs.SelectedIndex = 1;

            foreach (ThemeVariant theme in new[]
                { ThemeVariant.Light, ThemeVariant.Dark })
            {
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
