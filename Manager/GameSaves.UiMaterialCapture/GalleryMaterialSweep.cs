using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.App.Views;
using GameSaves.UiCapture.Gallery;

namespace GameSaves.UiMaterialCapture
{
    /// <summary>
    /// The truthful half of gallery capture. Acrylic and Mica are drawn by the
    /// Windows compositor, never by the application, so a screenshot that
    /// claims to show one has to be read back off the composited screen. The
    /// same is true of anything involving more than one window: Avalonia's own
    /// render contains one window and cannot show how two of them sit together.
    ///
    /// Every entry records the transparency level the platform actually
    /// granted next to the one that was asked for. When Windows substitutes or
    /// denies a level, the capture is kept, marked as a fallback, and dropped
    /// from the website selection rather than presented as proof of a material.
    /// </summary>
    internal static class GalleryMaterialSweep
    {
        // Deterministic desktop placement, in physical pixels from the primary
        // screen's top-left. Fixed so two runs compose the windows identically.
        private const int OriginX = 80;
        private const int OriginY = 60;

        private static readonly List<GalleryManifestEntry> Entries = new();
        private static readonly List<string> Fallbacks = new();
        private static readonly List<string> Unstable = new();

        /// <summary>Scenarios the screen refused to give up; reported, not hidden.</summary>
        private static readonly List<string> Failures = new();

        /// <summary>Set once the desktop is gone, so later passes do not re-wait.</summary>
        private static bool _desktopLost;

        public static async Task<int> RunAsync(
            IClassicDesktopStyleApplicationLifetime lifetime,
            string galleryRoot,
            string commit,
            IReadOnlyList<GalleryScenario> scenarios,
            string subdirectory)
        {
            if (_desktopLost)
                return Entries.Count;

            var window = (MainWindow)lifetime.MainWindow!;
            var viewModel = (MainWindowViewModel)window.DataContext!;

            string directory = Path.Combine(galleryRoot, subdirectory);
            Directory.CreateDirectory(directory);

            await Settle(2500);

            Screen screen = window.Screens.Primary ?? window.Screens.All[0];
            double scale = screen.Scaling;
            PixelRect screenBounds = screen.Bounds;

            // A neutral surface behind the window. Acrylic blurs whatever is
            // underneath, and the thing underneath would otherwise be this
            // machine's desktop: its wallpaper, its icons, and whatever else
            // happens to be open. A flat panel keeps the capture deterministic
            // and keeps someone's desktop out of a published screenshot.
            var backdrop = new Window
            {
                Title = "gallery backdrop",
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(0x10, 0x14, 0x20), 0),
                        new GradientStop(Color.FromRgb(0x27, 0x2F, 0x45), 1),
                    },
                },
                Width = screenBounds.Width / scale,
                Height = screenBounds.Height / scale,
            };

            backdrop.Show();
            backdrop.Position = screenBounds.Position;
            await Settle(400);

            foreach (GalleryScenario scenario in scenarios)
            {
                try
                {
                    await CaptureAsync(
                        lifetime, window, viewModel, backdrop, screenBounds, scale,
                        directory, subdirectory, commit, scenario);
                }
                catch (DesktopUnavailableException)
                {
                    // The screen went away and did not come back. Every
                    // remaining scenario would fail the same way, so the run
                    // says so once and stops instead of recording eighty
                    // identical failures.
                    Console.Error.WriteLine(DesktopUnavailableMessage);
                    _desktopLost = true;
                    break;
                }
                catch (Exception error) when (error is InvalidOperationException
                    or IOException or UnauthorizedAccessException)
                {
                    // One scenario failing is a gap in the gallery, reported as
                    // such. Aborting the run would throw away every capture
                    // that did work.
                    Failures.Add($"{scenario.FileName}: {error.Message}");
                    Console.WriteLine($"{scenario.FileName}: FAILED - {error.Message}");
                }

                // Written after every capture, not once at the end: an
                // interactive run occupies a screen for minutes and a crash
                // partway through should not throw away what it proved.
                GalleryManifest.WriteFragment(
                    galleryRoot, "windows", "GameSaves.UiMaterialCapture",
                    commit, Entries);
            }

            CloseExtraWindows(lifetime, window, backdrop);
            window.WorkspaceHost.ReattachAllDetachedTabs();
            await Settle(400);
            backdrop.Close();

            IReadOnlyList<GalleryManifestEntry> marked =
                GalleryManifest.MarkDuplicates(Entries);

            GalleryManifest.WriteFragment(
                galleryRoot, "windows", "GameSaves.UiMaterialCapture", commit, marked);

            (int total, int selected) = GalleryManifest.Merge(galleryRoot, commit);

            Console.WriteLine();
            Console.WriteLine(
                $"Gallery manifest: {total} image(s), {selected} selected for the website.");

            foreach (string fallback in Fallbacks)
                Console.WriteLine("PLATFORM FALLBACK: " + fallback);

            foreach (string capture in Unstable)
                Console.WriteLine("UNSTABLE CAPTURE: " + capture);

            foreach (string failure in Failures)
                Console.WriteLine("NOT CAPTURED: " + failure);

            return Entries.Count;
        }

        private static async Task CaptureAsync(
            IClassicDesktopStyleApplicationLifetime lifetime,
            MainWindow window,
            MainWindowViewModel viewModel,
            Window backdrop,
            PixelRect screenBounds,
            double scale,
            string directory,
            string subdirectory,
            string commit,
            GalleryScenario scenario)
        {
            CloseExtraWindows(lifetime, window, backdrop);
            window.WorkspaceHost.ReattachAllDetachedTabs();
            await Settle(250);

            GalleryScene.Apply(window, viewModel, scenario);
            await Settle(350);

            PixelRect frame = await FitFrameAsync(
                window, screenBounds, scale, scenario.Width, scenario.Height);

            var notes = new List<string>
            {
                "Composited by Windows over a neutral gradient panel, not over the desktop.",
            };

            if (frame.Width != scenario.Width || frame.Height != scenario.Height)
            {
                notes.Add(
                    $"Window frame settled at {frame.Width}x{frame.Height}; the saved " +
                    $"image is the requested {scenario.Width}x{scenario.Height} region " +
                    "anchored at its top-left.");
            }

            var target = new PixelRect(
                frame.X, frame.Y, scenario.Width, scenario.Height);

            await ArrangeExtraWindowsAsync(
                lifetime, window, viewModel, backdrop, target, scale, scenario, notes);

            WindowTransparencyLevel requested =
                window.TransparencyLevelHint.FirstOrDefault(WindowTransparencyLevel.None);
            WindowTransparencyLevel actual = window.ActualTransparencyLevel;

            WindowTransparencyLevel expected = scenario.HighContrast
                ? WindowTransparencyLevel.None
                : scenario.RequestedMaterial switch
                {
                    GalleryMaterials.Acrylic => WindowTransparencyLevel.AcrylicBlur,
                    GalleryMaterials.Mica => WindowTransparencyLevel.Mica,
                    _ => WindowTransparencyLevel.None,
                };

            bool granted = requested == expected && actual == requested;
            string effective = granted && expected != WindowTransparencyLevel.None
                ? scenario.RequestedMaterial
                : GalleryMaterials.None;

            if (scenario.HighContrast)
            {
                notes.Add(
                    "High Contrast forces opaque surfaces; no window material is " +
                    "composited, which is the application's intended behaviour.");
            }
            else if (scenario.RequestedMaterial != GalleryMaterials.None && !granted)
            {
                string fallback =
                    $"{scenario.FileName}: requested {expected}, platform granted {actual}.";
                notes.Add("Platform fallback: " + fallback);
                Fallbacks.Add(fallback);
            }

            string path = Path.Combine(directory, scenario.FileName);
            ScreenFrame captured = await StableCaptureAsync(target, scenario.FileName);
            captured.Save(path);

            Console.WriteLine(
                $"{scenario.FileName}: requested={requested} actual={actual} " +
                $"effective={effective}");

            Entries.Add(new GalleryManifestEntry
            {
                FileName = scenario.FileName,
                RelativePath = subdirectory + "/" + scenario.FileName,
                Page = scenario.Page,
                Subpage = scenario.SettingsCategory is { } index &&
                    index < GalleryScenario.SettingsCategories.Count
                        ? GalleryScenario.SettingsCategories[index]
                        : null,
                Width = captured.Bounds.Width,
                Height = captured.Bounds.Height,
                Theme = scenario.ThemeSlug,
                Accent = scenario.Accent,
                HighContrast = scenario.HighContrast,
                TextScale = scenario.TextScale,
                ReduceMotion = scenario.ReduceMotion,
                RequestedMaterial = scenario.RequestedMaterial,
                EffectiveMaterial = effective,
                ActualTransparencyLevel = actual.ToString(),
                RailPosition = scenario.RailPosition,
                RailCollapsed = scenario.RailCollapsed,
                WorkspaceScenario = scenario.Workspace,
                DataScenario = scenario.DataScenario,
                ProviderScenario = scenario.ProviderScenario,
                CaptureEngine = GalleryEngines.WindowsScreenReadback,
                Platform = "windows-desktop",
                RenderScaling = scale,
                // A material that was not granted proves nothing about that
                // material, so it never reaches the website however the plan
                // rated it.
                GalleryCandidate = scenario.GalleryCandidate &&
                    (scenario.RequestedMaterial == GalleryMaterials.None ||
                        scenario.HighContrast ||
                        granted),
                GalleryOrder = scenario.GalleryOrder,
                Category = scenario.Category,
                Caption = scenario.Caption,
                Alt = scenario.Alt,
                Notes = notes,
                Commit = commit,
                Sha256 = GalleryManifest.Sha256Of(path),
                PerceptualHash = GalleryManifest.AverageHash(
                    captured.Pixels,
                    captured.Bounds.Width,
                    captured.Bounds.Height,
                    captured.Bounds.Width * 4),
            });
        }

        /// <summary>
        /// Sizes the window until the rectangle Windows composites is exactly
        /// the requested pixel size. Avalonia sizes the client area in
        /// device-independent units, and the frame adds a border, so the two
        /// are only equal after the difference has been measured and removed.
        /// </summary>
        private static async Task<PixelRect> FitFrameAsync(
            Window window, PixelRect screenBounds, double scale, int width, int height)
        {
            window.WindowState = WindowState.Normal;
            window.Width = width / scale;
            window.Height = height / scale;
            window.Position = new PixelPoint(
                screenBounds.X + OriginX, screenBounds.Y + OriginY);

            await Settle(300);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                PixelRect frame = ScreenFrame.FrameBounds(window);

                if (frame.Width == width && frame.Height == height)
                    return frame;

                window.Width += (width - frame.Width) / scale;
                window.Height += (height - frame.Height) / scale;
                await Settle(250);
            }

            return ScreenFrame.FrameBounds(window);
        }

        /// <summary>
        /// Places the floating and detached windows a scenario asks for, inside
        /// the captured region and offset like tool windows over an editor, so
        /// the picture reads as one arranged workspace instead of windows
        /// dropped wherever Windows felt like putting them.
        /// </summary>
        private static async Task ArrangeExtraWindowsAsync(
            IClassicDesktopStyleApplicationLifetime lifetime,
            MainWindow window,
            MainWindowViewModel viewModel,
            Window backdrop,
            PixelRect target,
            double scale,
            GalleryScenario scenario,
            List<string> notes)
        {
            double left = target.X / scale;
            double top = target.Y / scale;
            double frameWidth = target.Width / scale;
            double frameHeight = target.Height / scale;

            switch (scenario.Workspace)
            {
                case GalleryWorkspaces.FloatingPanel:
                {
                    IWorkspaceLayoutPage layout =
                        viewModel.WorkspacePageFor(scenario.Page);
                    WorkspacePanelDefinition panel = WorkspaceLayoutCatalog
                        .PanelsFor(scenario.Page)
                        .LastOrDefault(definition => definition.CanFloat)
                        ?? WorkspaceLayoutCatalog.PanelsFor(scenario.Page)[^1];

                    layout.FloatPanel(
                        panel.Key,
                        left + (frameWidth * 0.34),
                        top + (frameHeight * 0.28),
                        Math.Min(520, frameWidth * 0.46),
                        Math.Min(360, frameHeight * 0.52));

                    notes.Add($"Floating panel: {panel.Title}.");
                    await Settle(700);
                    break;
                }

                case GalleryWorkspaces.DetachedTab:
                {
                    window.WorkspaceHost.ApplyDetachedTabs(new[]
                    {
                        UiDetachedWindowSettings.TryCreate(
                            UiRailLayoutSettings.TabBackups,
                            left + (frameWidth * 0.30),
                            top + (frameHeight * 0.22),
                            frameWidth * 0.64,
                            frameHeight * 0.70)!,
                    });

                    notes.Add("Detached window: Backups.");
                    await Settle(900);
                    break;
                }

                case GalleryWorkspaces.MultipleWindows:
                {
                    window.WorkspaceHost.ApplyDetachedTabs(new[]
                    {
                        UiDetachedWindowSettings.TryCreate(
                            UiRailLayoutSettings.TabBackups,
                            left + (frameWidth * 0.22),
                            top + (frameHeight * 0.14),
                            frameWidth * 0.58,
                            frameHeight * 0.56)!,
                        UiDetachedWindowSettings.TryCreate(
                            UiRailLayoutSettings.TabHistory,
                            left + (frameWidth * 0.34),
                            top + (frameHeight * 0.36),
                            frameWidth * 0.60,
                            frameHeight * 0.56)!,
                    });

                    notes.Add("Detached windows: Backups and History.");
                    await Settle(1100);
                    break;
                }

                default:
                    return;
            }

            // Bring every extra surface above the main window, in the order it
            // was placed, so the cascade reads front to back.
            foreach (Window extra in lifetime.Windows
                .Where(candidate => candidate != window && candidate != backdrop))
            {
                extra.Activate();
                await Settle(200);
            }
        }

        // A theme swap, an accent swap, and the DWM material cross-fade all
        // animate, so a capture is only accepted once two consecutive
        // read-backs are the same picture.
        private static async Task<ScreenFrame> StableCaptureAsync(
            PixelRect region, string name)
        {
            await Settle(250);

            ScreenFrame frame = await ReadBackAsync(region);

            for (int attempt = 0; attempt < 16; attempt++)
            {
                await Settle(250);

                ScreenFrame next = await ReadBackAsync(region);

                if (next.Difference(frame, region).Mean <= 0.05)
                    return next;

                frame = next;

                if (attempt == 15)
                    Unstable.Add(name);
            }

            return frame;
        }

        // A screen read-back can be refused for reasons that have nothing to
        // do with this run: a lock screen, a UAC prompt, or any other secure
        // desktop takes the screen away. A sweep that occupies a machine for
        // several minutes will meet one, and losing every capture taken so far
        // to a momentary refusal is not acceptable, so a refusal is waited out.
        private static async Task<ScreenFrame> ReadBackAsync(PixelRect region)
        {
            bool announcedLock = false;
            bool announcedCover = false;

            for (int attempt = 0; attempt < DesktopWaitMinutes * 4; attempt++)
            {
                // Refuse before reading, not after: once another application's
                // pixels are in the byte array they are in this process, and
                // the point is that they never get there.
                if (!ScreenFrame.IsOwnedByThisProcess(region))
                {
                    if (!announcedCover)
                    {
                        announcedCover = true;
                        Console.WriteLine(
                            "Another application is in front of the capture area. " +
                            "Waiting for it to go away rather than photographing it.");
                    }

                    await Settle(15_000);
                    continue;
                }

                try
                {
                    return ScreenFrame.Capture(region);
                }
                catch (InvalidOperationException)
                {
                    if (!announcedLock)
                    {
                        announcedLock = true;
                        Console.WriteLine(
                            "The screen is not readable (locked session or secure " +
                            $"desktop). Waiting up to {DesktopWaitMinutes} minutes.");
                    }

                    await Settle(15_000);
                }
            }

            throw new DesktopUnavailableException();
        }

        /// <summary>How long to wait for a locked session to come back.</summary>
        private const int DesktopWaitMinutes = 10;

        private const string DesktopUnavailableMessage =
            "The capture area never became readable and entirely this application's, " +
            "so no further capture was attempted. This harness composites through " +
            "Windows and reads the result back off the screen, so it needs an " +
            "unlocked, interactive session that nobody is using: a locked screen " +
            "refuses the read-back, and any other window in front of the capture " +
            "area would be photographed instead of the application. Leave the " +
            "machine alone and run it again. The deterministic half " +
            "(GameSaves.UiCapture, mode \"gallery\") needs neither.";

        /// <summary>
        /// Raised when the screen refused every read-back for long enough that
        /// waiting further is pointless. Distinct from a per-scenario failure,
        /// because it means the whole run cannot continue.
        /// </summary>
        private sealed class DesktopUnavailableException : InvalidOperationException
        {
            public DesktopUnavailableException()
                : base(DesktopUnavailableMessage)
            {
            }
        }

        private static void CloseExtraWindows(
            IClassicDesktopStyleApplicationLifetime lifetime,
            Window window,
            Window backdrop)
        {
            foreach (Window extra in lifetime.Windows
                .Where(candidate => candidate != window && candidate != backdrop)
                .ToArray())
            {
                extra.Close();
            }
        }

        private static async Task Settle(int milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(milliseconds);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
