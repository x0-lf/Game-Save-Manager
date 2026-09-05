using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameSaves.UiCapture.Gallery
{
    /// <summary>
    /// One row of machine-readable metadata per written PNG. The website reads
    /// this instead of inferring meaning from file names, and the assertions
    /// read it to check coverage.
    /// </summary>
    public sealed record GalleryManifestEntry
    {
        public required string FileName { get; init; }

        /// <summary>Path relative to the gallery root, using forward slashes.</summary>
        public required string RelativePath { get; init; }

        public required string Page { get; init; }

        public string? Subpage { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public required string Theme { get; init; }

        public required string Accent { get; init; }

        public required bool HighContrast { get; init; }

        public required double TextScale { get; init; }

        public required bool ReduceMotion { get; init; }

        public required string RequestedMaterial { get; init; }

        /// <summary>
        /// What the pixels can actually be said to contain. Never copied from
        /// the request: a denied or unavailable material reports "none".
        /// </summary>
        public required string EffectiveMaterial { get; init; }

        /// <summary>The transparency level the platform reported, when known.</summary>
        public string? ActualTransparencyLevel { get; init; }

        public required string RailPosition { get; init; }

        public required bool RailCollapsed { get; init; }

        public required string WorkspaceScenario { get; init; }

        public required string DataScenario { get; init; }

        public required string ProviderScenario { get; init; }

        public required string CaptureEngine { get; init; }

        public required string Platform { get; init; }

        public double RenderScaling { get; init; } = 1.0;

        public required bool GalleryCandidate { get; init; }

        public int GalleryOrder { get; init; }

        public required string Category { get; init; }

        public string Caption { get; init; } = string.Empty;

        public string Alt { get; init; } = string.Empty;

        /// <summary>Warnings, platform fallbacks, and known defects.</summary>
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public required string Commit { get; init; }

        public required string Sha256 { get; init; }

        /// <summary>
        /// 64-bit average hash of the image, as 16 hex digits. Near-duplicates
        /// are flagged rather than deleted: an accidental repeat and a real
        /// platform fallback look the same here, and only one of them is a bug.
        /// </summary>
        public string PerceptualHash { get; init; } = string.Empty;

        /// <summary>Set when another entry has an identical or near-identical image.</summary>
        public string? DuplicateOf { get; init; }
    }

    public sealed record GalleryManifestDocument
    {
        public required string GeneratedBy { get; init; }

        public required string Commit { get; init; }

        public required IReadOnlyList<GalleryManifestEntry> Images { get; init; }
    }

    /// <summary>
    /// Writes manifest fragments and merges them. Two harnesses contribute to
    /// one gallery (headless for deterministic renders, Windows for real
    /// composition), so each writes its own fragment and then rebuilds the
    /// combined <c>gallery-manifest.json</c> and <c>gallery-selected.json</c>
    /// from whatever fragments exist. Running only one harness therefore still
    /// produces a valid, if partial, manifest.
    /// </summary>
    public static class GalleryManifest
    {
        public const string CombinedFileName = "gallery-manifest.json";
        public const string SelectedFileName = "gallery-selected.json";
        private const string FragmentPrefix = "manifest-";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Sha256Of(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// A 64-bit average hash: the image is reduced to an 8x8 grid of mean
        /// luminance and each cell becomes one bit against the overall mean.
        /// Two images whose hashes differ in at most a few bits look the same
        /// to a person, which is exactly what "redundant gallery shot" means.
        /// </summary>
        public static string AverageHash(
            ReadOnlySpan<byte> bgra, int width, int height, int stride)
        {
            if (width <= 0 || height <= 0)
                return string.Empty;

            Span<double> cells = stackalloc double[64];
            Span<int> counts = stackalloc int[64];

            for (int y = 0; y < height; y++)
            {
                int cellY = Math.Min(7, y * 8 / height);
                int row = y * stride;

                for (int x = 0; x < width; x++)
                {
                    int index = (Math.Min(7, x * 8 / width)) + (cellY * 8);
                    int pixel = row + (x * 4);

                    // Rec. 601 luma from BGRA.
                    cells[index] +=
                        (0.114 * bgra[pixel]) +
                        (0.587 * bgra[pixel + 1]) +
                        (0.299 * bgra[pixel + 2]);
                    counts[index]++;
                }
            }

            double total = 0;

            for (int index = 0; index < 64; index++)
            {
                cells[index] = counts[index] == 0 ? 0 : cells[index] / counts[index];
                total += cells[index];
            }

            double mean = total / 64.0;
            ulong bits = 0;

            for (int index = 0; index < 64; index++)
            {
                if (cells[index] > mean)
                    bits |= 1UL << index;
            }

            return bits.ToString("x16", CultureInfo.InvariantCulture);
        }

        public static int HammingDistance(string left, string right)
        {
            if (left.Length != 16 || right.Length != 16)
                return int.MaxValue;

            ulong a = ulong.Parse(left, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ulong b = ulong.Parse(right, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            return System.Numerics.BitOperations.PopCount(a ^ b);
        }

        /// <summary>
        /// Marks entries whose image is within <paramref name="threshold"/>
        /// bits of an earlier one. Nothing is deleted; the note is what keeps
        /// a redundant frame out of the website without losing the evidence.
        /// </summary>
        public static IReadOnlyList<GalleryManifestEntry> MarkDuplicates(
            IReadOnlyList<GalleryManifestEntry> entries, int threshold = 2)
        {
            var result = new List<GalleryManifestEntry>(entries.Count);
            var seen = new List<GalleryManifestEntry>();

            foreach (GalleryManifestEntry entry in entries)
            {
                GalleryManifestEntry? twin = entry.PerceptualHash.Length != 16
                    ? null
                    : seen.FirstOrDefault(candidate =>
                        candidate.Width == entry.Width &&
                        candidate.Height == entry.Height &&
                        HammingDistance(candidate.PerceptualHash, entry.PerceptualHash) <= threshold);

                result.Add(twin is null ? entry : entry with { DuplicateOf = twin.RelativePath });
                seen.Add(entry);
            }

            return result;
        }

        public static void WriteFragment(
            string galleryRoot,
            string fragmentName,
            string generatedBy,
            string commit,
            IReadOnlyList<GalleryManifestEntry> entries)
        {
            Directory.CreateDirectory(galleryRoot);

            File.WriteAllText(
                Path.Combine(galleryRoot, FragmentPrefix + fragmentName + ".json"),
                JsonSerializer.Serialize(
                    new GalleryManifestDocument
                    {
                        GeneratedBy = generatedBy,
                        Commit = commit,
                        Images = entries,
                    },
                    Options));
        }

        /// <summary>
        /// Rebuilds the combined manifest and the ordered website selection
        /// from every fragment in the gallery root.
        /// </summary>
        public static (int Total, int Selected) Merge(string galleryRoot, string commit)
        {
            var entries = new List<GalleryManifestEntry>();

            foreach (string fragment in Directory
                .EnumerateFiles(galleryRoot, FragmentPrefix + "*.json")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                GalleryManifestDocument? document =
                    JsonSerializer.Deserialize<GalleryManifestDocument>(
                        File.ReadAllText(fragment), Options);

                if (document is not null)
                    entries.AddRange(document.Images);
            }

            IReadOnlyList<GalleryManifestEntry> ordered = entries
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();

            File.WriteAllText(
                Path.Combine(galleryRoot, CombinedFileName),
                JsonSerializer.Serialize(
                    new GalleryManifestDocument
                    {
                        GeneratedBy = "GameSaves gallery capture",
                        Commit = commit,
                        Images = ordered,
                    },
                    Options));

            IReadOnlyList<GalleryManifestEntry> selected = ordered
                .Where(entry => entry.GalleryCandidate)
                .OrderBy(entry => entry.GalleryOrder)
                .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();

            File.WriteAllText(
                Path.Combine(galleryRoot, SelectedFileName),
                JsonSerializer.Serialize(
                    new GalleryManifestDocument
                    {
                        GeneratedBy = "GameSaves gallery capture (website selection)",
                        Commit = commit,
                        Images = selected,
                    },
                    Options));

            return (ordered.Count, selected.Count);
        }

        /// <summary>
        /// The commit the capture was taken at, so a stale image can be traced
        /// back to the revision that produced it. "unknown" when git is not
        /// available, never a fabricated value.
        /// </summary>
        public static string CurrentCommit()
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });

                if (process is null)
                    return "unknown";

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);

                return output.Length == 40 ? output : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
