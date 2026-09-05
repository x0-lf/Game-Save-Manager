using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// The last mile: copies the website selection out of the QA archive into a
    /// dated delivery folder, converts each image to WebP when ffmpeg is on the
    /// PATH, and writes the one-line-per-image report the website is assembled
    /// from.
    ///
    /// Nothing here decides what is publishable. That was decided during
    /// capture, image by image, and is recorded in the manifest; this only
    /// moves what was already approved.
    /// </summary>
    public static class GalleryPublish
    {
        public sealed record Result(
            int Copied,
            int Converted,
            IReadOnlyList<string> Problems);

        public static Result Run(string galleryRoot, string destination)
        {
            var problems = new List<string>();

            string selectedPath =
                Path.Combine(galleryRoot, GalleryManifest.SelectedFileName);

            if (!File.Exists(selectedPath))
                return new Result(0, 0, new[] { $"No selection at {selectedPath}." });

            GalleryManifestDocument? document =
                JsonSerializer.Deserialize<GalleryManifestDocument>(
                    File.ReadAllText(selectedPath));

            if (document is null || document.Images.Count == 0)
                return new Result(0, 0, new[] { $"{selectedPath} contains no images." });

            Directory.CreateDirectory(destination);

            string? ffmpeg = FindFfmpeg();
            int copied = 0;
            int converted = 0;

            var report = new List<string>
            {
                "# Game Save Manager — website gallery",
                string.Empty,
                $"Generated from commit `{document.Commit}`.",
                $"{document.Images.Count} images, in the order they should appear.",
                string.Empty,
                "Each row is one image: the caption to print, the alt text to set,",
                "and the exact pixel size to declare so the page does not shift as",
                "images load.",
                string.Empty,
            };

            foreach (GalleryManifestEntry entry in document.Images)
            {
                string source = Path.Combine(
                    galleryRoot,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(source))
                {
                    problems.Add($"{entry.RelativePath}: missing, not published.");
                    continue;
                }

                string target = Path.Combine(destination, entry.FileName);
                File.Copy(source, target, overwrite: true);
                copied++;

                string webp = Path.ChangeExtension(target, ".webp");
                bool hasWebp = false;

                if (ffmpeg is not null)
                {
                    if (Convert(ffmpeg, target, webp))
                    {
                        converted++;
                        hasWebp = true;
                    }
                    else
                    {
                        problems.Add($"{entry.FileName}: WebP conversion failed.");
                    }
                }

                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"## {entry.GalleryOrder:00}. {entry.Caption}"));
                report.Add(string.Empty);
                report.Add($"- **File:** `{entry.FileName}`" +
                    (hasWebp ? $" (WebP: `{Path.GetFileName(webp)}`)" : string.Empty));
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"- **Size:** {entry.Width} x {entry.Height}"));
                report.Add($"- **Section:** {entry.Category}");
                report.Add($"- **Shows:** {Describe(entry)}");
                report.Add($"- **Alt text:** {entry.Alt}");

                if (entry.Notes.Count > 0)
                    report.Add($"- **Notes:** {string.Join(" ", entry.Notes)}");

                report.Add(string.Empty);
            }

            if (ffmpeg is null)
            {
                problems.Add(
                    "ffmpeg was not found on the PATH, so no WebP was written. " +
                    "The PNGs are complete and can be converted later.");
            }

            File.WriteAllLines(Path.Combine(destination, "gallery-report.md"), report);
            File.Copy(
                selectedPath,
                Path.Combine(destination, GalleryManifest.SelectedFileName),
                overwrite: true);

            return new Result(copied, converted, problems);
        }

        // The one-sentence description the website copies. Assembled from the
        // recorded scenario rather than written by hand, so it can never drift
        // from what the image actually shows.
        private static string Describe(GalleryManifestEntry entry)
        {
            var parts = new List<string>
            {
                GalleryScenario.PageTitle(entry.Page) +
                    (entry.Subpage is null ? string.Empty : " > " + entry.Subpage),
                entry.HighContrast
                    ? "High Contrast"
                    : entry.Theme + " theme",
                entry.Accent + " accent",
            };

            if (entry.EffectiveMaterial != GalleryMaterials.None)
                parts.Add(GalleryScenario.MaterialTitle(entry.EffectiveMaterial) + " window material");

            if (Math.Abs(entry.TextScale - 1.0) > 0.001)
            {
                parts.Add("text size " +
                    (entry.TextScale * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
            }

            if (entry.RailPosition != "left" || entry.RailCollapsed)
            {
                parts.Add(entry.RailPosition + " navigation" +
                    (entry.RailCollapsed ? ", collapsed" : string.Empty));
            }

            if (entry.WorkspaceScenario != GalleryWorkspaces.Default)
                parts.Add("workspace: " + entry.WorkspaceScenario);

            if (entry.ProviderScenario != GalleryProviders.None)
                parts.Add("sync: " + entry.ProviderScenario);

            return string.Join(", ", parts) + ".";
        }

        private static string? FindFfmpeg()
        {
            string command = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim('"'), command);

                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // An unusable PATH entry is not this tool's problem.
                }
            }

            return null;
        }

        // Lossless WebP: these are screenshots of text, and lossy compression
        // puts artefacts on every glyph edge at exactly the sizes a reader
        // looks at them.
        private static bool Convert(string ffmpeg, string source, string target)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(ffmpeg)
                {
                    ArgumentList =
                    {
                        "-y", "-loglevel", "error",
                        "-i", source,
                        "-c:v", "libwebp", "-lossless", "1", "-compression_level", "6",
                        target,
                    },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (process is null)
                    return false;

                process.WaitForExit(60_000);

                return process.ExitCode == 0 && File.Exists(target);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
