using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.UiCapture.Gallery;

namespace GameSaves.UiCapture
{
    /// <summary>
    /// The deterministic half of gallery capture. Avalonia renders the window
    /// itself, so the result is byte-stable for a given commit, but it can
    /// never contain a Windows compositor backdrop. Every entry it writes
    /// therefore reports an effective material of "none", whatever was
    /// requested, and Acrylic/Mica scenarios are not routed here at all.
    /// </summary>
    internal static class GallerySweep
    {
        private const string Platform = "windows-headless";

        public static IReadOnlyList<GalleryManifestEntry> Run(
            Window window,
            MainWindowViewModel viewModel,
            string galleryRoot,
            string subdirectory,
            string commit,
            IReadOnlyList<GalleryScenario> scenarios,
            List<string> accessibilityRows)
        {
            string directory = Path.Combine(galleryRoot, subdirectory);
            Directory.CreateDirectory(directory);

            var entries = new List<GalleryManifestEntry>(scenarios.Count);

            foreach (GalleryScenario scenario in scenarios)
            {
                GalleryScene.Apply(window, viewModel, scenario);

                window.Width = scenario.Width;
                window.Height = scenario.Height;

                Settle();

                GalleryScene.LayoutAudit audit = GalleryScene.Audit(window);

                if (scenario.TextScale != UiAccessibilitySettings.DefaultTextScale ||
                    scenario.Variant.StartsWith("text-", StringComparison.Ordinal))
                {
                    accessibilityRows.Add(string.Join('\t',
                        GalleryScenario.PageTitle(scenario.Page),
                        (scenario.TextScale * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                        $"{scenario.Width}x{scenario.Height}",
                        audit.Verdict,
                        audit.ClippedElements.ToString(CultureInfo.InvariantCulture),
                        audit.OverflowingElements.ToString(CultureInfo.InvariantCulture),
                        string.Join(" | ", audit.Details)));
                }

                string path = Path.Combine(directory, scenario.FileName);
                (int width, int height, string hash) = Capture(window, path);

                if (width != scenario.Width || height != scenario.Height)
                {
                    throw new InvalidOperationException(
                        $"{scenario.FileName}: captured {width}x{height}, " +
                        $"expected {scenario.Width}x{scenario.Height}.");
                }

                var notes = new List<string>();

                if (scenario.RequestedMaterial != GalleryMaterials.None)
                {
                    notes.Add(
                        "Headless rendering contains no compositor backdrop; " +
                        "the effective material is none.");
                }

                if (scenario.HighContrast)
                {
                    notes.Add(
                        "High Contrast forces opaque surfaces, so no window " +
                        "material is composited.");
                }

                if (audit.Verdict != "PASS")
                    notes.Add($"Layout audit {audit.Verdict}: {string.Join("; ", audit.Details)}");

                entries.Add(new GalleryManifestEntry
                {
                    FileName = scenario.FileName,
                    RelativePath = subdirectory + "/" + scenario.FileName,
                    Page = scenario.Page,
                    Subpage = scenario.SettingsCategory is { } index &&
                        index < GalleryScenario.SettingsCategories.Count
                            ? GalleryScenario.SettingsCategories[index]
                            : null,
                    Width = width,
                    Height = height,
                    Theme = scenario.ThemeSlug,
                    Accent = scenario.Accent,
                    HighContrast = scenario.HighContrast,
                    TextScale = scenario.TextScale,
                    ReduceMotion = scenario.ReduceMotion,
                    RequestedMaterial = scenario.RequestedMaterial,
                    EffectiveMaterial = GalleryMaterials.None,
                    ActualTransparencyLevel = "none (headless: no platform compositor)",
                    RailPosition = scenario.RailPosition,
                    RailCollapsed = scenario.RailCollapsed,
                    WorkspaceScenario = scenario.Workspace,
                    DataScenario = scenario.DataScenario,
                    ProviderScenario = scenario.ProviderScenario,
                    CaptureEngine = GalleryEngines.Headless,
                    Platform = Platform,
                    RenderScaling = window.RenderScaling,
                    // A gallery candidate must also survive the layout audit:
                    // a clipped control disqualifies an image whatever the
                    // plan asked for.
                    GalleryCandidate = scenario.GalleryCandidate && audit.ClippedElements == 0,
                    GalleryOrder = scenario.GalleryOrder,
                    Category = scenario.Category,
                    Caption = scenario.Caption,
                    Alt = scenario.Alt,
                    Notes = notes,
                    Commit = commit,
                    Sha256 = GalleryManifest.Sha256Of(path),
                    PerceptualHash = hash,
                });
            }

            return entries;
        }

        /// <summary>
        /// Writes the classified text-scale report. 150% rows are recorded
        /// exactly as measured: a defect there is the point of the sweep, not
        /// something to hide by dropping the row.
        /// </summary>
        public static void WriteAccessibilityReport(
            string galleryRoot, IReadOnlyList<string> rows)
        {
            var lines = new List<string>
            {
                "# Accessibility layout report",
                string.Empty,
                "Measured on the arranged visual tree, not read off the picture.",
                string.Empty,
                "- PASS: nothing measured wrong.",
                "- MINOR: content is cut off by a scroller, so it is off-screen",
                "  but still reachable by scrolling.",
                "- FAIL: a control was arranged smaller than it asked to be, or is",
                "  cut off by a clip nothing can scroll, so text or an action cannot",
                "  be read or reached.",
                string.Empty,
                "| Page | Text size | Size | Verdict | Unreachable | Scrolled out | Detail |",
                "| --- | --- | --- | --- | ---: | ---: | --- |",
            };

            foreach (string row in rows)
            {
                string[] cells = row.Split('\t');
                lines.Add("| " + string.Join(" | ", cells.Select(Escape)) + " |");
            }

            File.WriteAllLines(
                Path.Combine(galleryRoot, "accessibility-layout-report.md"), lines);
        }

        private static string Escape(string value) =>
            value.Replace("|", "\\|", StringComparison.Ordinal);

        // Layout, then the render timer, then layout again: a style trigger
        // that starts a transition only queues work on the first pass, and a
        // capture taken there catches the frame mid-transition.
        private static void Settle()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        }

        private static (int Width, int Height, string Hash) Capture(
            Window window, string path)
        {
            using WriteableBitmap? frame = window.CaptureRenderedFrame();

            if (frame is null)
            {
                throw new InvalidOperationException(
                    $"Headless rendering produced no frame for {Path.GetFileName(path)}; " +
                    "check that Skia rendering is enabled.");
            }

            string hash;

            using (ILockedFramebuffer buffer = frame.Lock())
            {
                byte[] pixels = new byte[buffer.RowBytes * buffer.Size.Height];
                Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);

                hash = GalleryManifest.AverageHash(
                    pixels, buffer.Size.Width, buffer.Size.Height, buffer.RowBytes);
            }

            frame.Save(path, new PngBitmapEncoderOptions());

            return (frame.PixelSize.Width, frame.PixelSize.Height, hash);
        }
    }
}
