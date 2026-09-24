using GameSaves.Core.Catalog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameSaves.Infrastructure.Catalog
{
    /// <summary>
    /// Result of game engine fingerprinting analysis.
    /// </summary>
    public sealed record EngineFingerprintResult(
        GameEngineKind Engine,
        DetectionConfidence Confidence,
        IReadOnlyList<string> Evidence,
        string? CompanyName = null,
        string? ProductName = null);

    /// <summary>
    /// Detects game engine signatures from directory structures and signature files.
    /// Supports Unreal Engine, Unity, Godot, Ren'Py, Source Engine, and RPG Maker.
    /// </summary>
    public sealed class GameEngineFingerprinter
    {
        public EngineFingerprintResult Detect(
            IEnumerable<string> relativePaths,
            string? gameRootDirectory = null)
        {
            var paths = relativePaths
                .Select(p => p.Replace('\\', '/').Trim('/'))
                .ToList();

            // 1. Unity
            var unityResult = CheckUnity(paths, gameRootDirectory);
            if (unityResult != null)
                return unityResult;

            // 2. Unreal Engine
            var unrealResult = CheckUnreal(paths);
            if (unrealResult != null)
                return unrealResult;

            // 3. Godot
            var godotResult = CheckGodot(paths);
            if (godotResult != null)
                return godotResult;

            // 4. Ren'Py
            var renpyResult = CheckRenPy(paths);
            if (renpyResult != null)
                return renpyResult;

            // 5. Source Engine
            var sourceResult = CheckSource(paths);
            if (sourceResult != null)
                return sourceResult;

            // 6. RPG Maker
            var rpgMakerResult = CheckRpgMaker(paths);
            if (rpgMakerResult != null)
                return rpgMakerResult;

            return new EngineFingerprintResult(
                GameEngineKind.Custom,
                DetectionConfidence.Low,
                Array.Empty<string>());
        }

        private static EngineFingerprintResult? CheckUnity(List<string> paths, string? gameRootDirectory)
        {
            var evidence = new List<string>();
            string? companyName = null;
            string? productName = null;

            bool hasDataDir = false;
            string? dataDirName = null;

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);
                string dirName = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;

                if (fileName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);
                else if (fileName.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);
                else if (fileName.Equals("boot.config", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);

                if (path.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ||
                    dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("/_Data/", StringComparison.OrdinalIgnoreCase))
                {
                    hasDataDir = true;
                    if (dataDirName == null)
                    {
                        string segment = path.Split('/')[0];
                        if (segment.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                            dataDirName = segment;
                    }
                }
            }

            // A *_Data folder alone (user_data, save_data) is not Unity evidence: it only counts
            // next to a real Unity marker or an app.info inside it.
            bool hasAppInfo = dataDirName != null &&
                paths.Contains($"{dataDirName}/app.info", StringComparer.OrdinalIgnoreCase);

            if (hasDataDir && dataDirName != null && (evidence.Count > 0 || hasAppInfo) && !evidence.Contains(dataDirName))
                evidence.Add(dataDirName);

            // Attempt to read app.info if present on disk
            if (!string.IsNullOrWhiteSpace(gameRootDirectory) && dataDirName != null && evidence.Count > 0)
            {
                string appInfoPath = Path.Combine(gameRootDirectory, dataDirName, "app.info");
                if (File.Exists(appInfoPath))
                {
                    try
                    {
                        // app.info comes from the untrusted game folder and its names end up in
                        // path templates: read only the two lines we need and keep only plain names.
                        string[] lines = File.ReadLines(appInfoPath).Take(2).ToArray();
                        if (lines.Length == 2)
                        {
                            companyName = AsSafeName(lines[0]);
                            productName = AsSafeName(lines[1]);
                            evidence.Add($"{dataDirName}/app.info ({companyName}/{productName})");
                        }
                    }
                    catch
                    {
                        // Ignore file read exceptions in probing
                    }
                }
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.EndsWith("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("boot.config", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("app.info", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.Unity,
                    confidence,
                    evidence,
                    companyName,
                    productName);
            }

            return null;
        }

        /// <summary>
        /// Returns <paramref name="value"/> trimmed when it can be used as a single path segment,
        /// otherwise null (separators, drive colons, "." / "..", other invalid characters).
        /// </summary>
        internal static string? AsSafeName(string? value)
        {
            string? name = value?.Trim();

            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.Length > 100)
                return null;

            // Windows rules on every OS: templates are expanded on Windows.
            foreach (char c in name)
            {
                if (char.IsControl(c) || "<>:\"/\\|?*".Contains(c))
                    return null;
            }

            return name;
        }

        private static EngineFingerprintResult? CheckUnreal(List<string> paths)
        {
            var evidence = new List<string>();

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);

                if (path.StartsWith("Binaries/Win64", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("Binaries/Win32", StringComparison.OrdinalIgnoreCase))
                {
                    if (!evidence.Contains("Binaries/Win64"))
                        evidence.Add("Binaries/Win64");
                }

                if (path.StartsWith("Engine/Binaries", StringComparison.OrdinalIgnoreCase))
                {
                    if (!evidence.Contains("Engine/Binaries"))
                        evidence.Add("Engine/Binaries");
                }

                if (fileName.Equals("DefaultEngine.ini", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("Config/DefaultEngine.ini", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }

                if (fileName.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);

                if (path.Contains("Saved/SaveGames", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("DefaultEngine.ini", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.Unreal,
                    confidence,
                    evidence);
            }

            return null;
        }

        private static EngineFingerprintResult? CheckGodot(List<string> paths)
        {
            var evidence = new List<string>();

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);

                if (fileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);
                else if (fileName.Equals("project.godot", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("project.binary", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.EndsWith("project.godot", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.Godot,
                    confidence,
                    evidence);
            }

            return null;
        }

        private static EngineFingerprintResult? CheckRenPy(List<string> paths)
        {
            var evidence = new List<string>();

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);

                if (path.StartsWith("renpy/", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("renpy", StringComparison.OrdinalIgnoreCase))
                {
                    if (!evidence.Contains("renpy/"))
                        evidence.Add("renpy/");
                }

                if (path.StartsWith("game/saves", StringComparison.OrdinalIgnoreCase))
                {
                    if (!evidence.Contains("game/saves"))
                        evidence.Add("game/saves");
                }

                if (fileName.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".rpyc", StringComparison.OrdinalIgnoreCase))
                {
                    if (evidence.Count < 5)
                        evidence.Add(path);
                }
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.Equals("renpy/", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Equals("game/saves", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.RenPy,
                    confidence,
                    evidence);
            }

            return null;
        }

        private static EngineFingerprintResult? CheckSource(List<string> paths)
        {
            var evidence = new List<string>();

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);

                if (fileName.Equals("gameinfo.txt", StringComparison.OrdinalIgnoreCase))
                    evidence.Add(path);
                else if (fileName.Equals("hl2.exe", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Equals("srcds.exe", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }
                else if (path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                {
                    if (!evidence.Any(e => e.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)))
                        evidence.Add(path);
                }
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.EndsWith("gameinfo.txt", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.Source,
                    confidence,
                    evidence);
            }

            return null;
        }

        private static EngineFingerprintResult? CheckRpgMaker(List<string> paths)
        {
            var evidence = new List<string>();

            foreach (string path in paths)
            {
                string fileName = Path.GetFileName(path);

                if (fileName.Equals("Game.rgss3a", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("Game.rgss2a", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("Game.rgssad", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }
                else if (path.EndsWith("data/System.json", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("data/Actors.json", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }
                else if (path.StartsWith("www/save", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(path);
                }
            }

            if (evidence.Count > 0)
            {
                var confidence = evidence.Any(e => e.StartsWith("Game.rgss", StringComparison.OrdinalIgnoreCase) ||
                                                   e.Contains("System.json", StringComparison.OrdinalIgnoreCase))
                    ? DetectionConfidence.Certain
                    : DetectionConfidence.High;

                return new EngineFingerprintResult(
                    GameEngineKind.RpgMaker,
                    confidence,
                    evidence);
            }

            return null;
        }
    }
}
