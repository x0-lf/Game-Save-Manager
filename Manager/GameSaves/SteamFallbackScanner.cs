using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace GameSave
{
    public sealed class SteamFallbackScanner
    {
        private static readonly string[] CommonRelativeCandidates =
        {
            "Steam",
            "SteamLibrary",
            Path.Combine("Games", "Steam"),
            Path.Combine("Games", "SteamLibrary"),
            Path.Combine("Program Files", "Steam"),
            Path.Combine("Program Files (x86)", "Steam")
        };

        public SteamFallbackScanResult Scan(
            SteamDiscoveryOptions options,
            IProgress<SteamFallbackScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new SteamFallbackScanResult();
            var foundLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string driveRoot in GetFixedReadyDriveRoots(result, options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                ScanCommonCandidatePaths(
                    driveRoot,
                    foundLibraries,
                    result,
                    options,
                    progress,
                    cancellationToken);

                if (options.EnableDeepFallbackScan)
                {
                    ScanDriveRecursively(
                        driveRoot,
                        foundLibraries,
                        result,
                        options,
                        progress,
                        cancellationToken);
                }
            }

            result.LibraryPaths.AddRange(foundLibraries.OrderBy(path => path));
            return result;
        }

        private static IEnumerable<string> GetFixedReadyDriveRoots(
            SteamFallbackScanResult result,
            SteamDiscoveryOptions options)
        {
            DriveInfo[] drives;

            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }
            catch (SecurityException)
            {
                yield break;
            }

            foreach (DriveInfo drive in drives)
            {
                bool usable;

                try
                {
                    usable = drive.DriveType == DriveType.Fixed && drive.IsReady;
                }
                catch (IOException)
                {
                    AddSkipped(result, drive.Name, options);
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    AddSkipped(result, drive.Name, options);
                    continue;
                }

                if (usable)
                    yield return drive.RootDirectory.FullName;
            }
        }

        private static void ScanCommonCandidatePaths(
            string driveRoot,
            HashSet<string> foundLibraries,
            SteamFallbackScanResult result,
            SteamDiscoveryOptions options,
            IProgress<SteamFallbackScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            foreach (string relativeCandidate in CommonRelativeCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string candidatePath = Path.Combine(driveRoot, relativeCandidate);

                TryAddSteamLibrary(
                    candidatePath,
                    foundLibraries,
                    result,
                    options,
                    progress);
            }
        }

        private static void ScanDriveRecursively(
            string driveRoot,
            HashSet<string> foundLibraries,
            SteamFallbackScanResult result,
            SteamDiscoveryOptions options,
            IProgress<SteamFallbackScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((driveRoot, 0));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = stack.Pop();

                if (current.Depth > options.FallbackMaxDepth)
                    continue;

                if (ShouldSkipDirectory(current.Path))
                    continue;

                if (IsReparsePoint(current.Path))
                    continue;

                result.DirectoriesScanned++;

                progress?.Report(new SteamFallbackScanProgress(
                    current.Path,
                    result.DirectoriesScanned,
                    foundLibraries.Count));

                TryAddSteamLibrary(
                    current.Path,
                    foundLibraries,
                    result,
                    options,
                    progress);

                if (current.Depth == options.FallbackMaxDepth)
                    continue;

                foreach (string childDirectory in SafeEnumerateDirectories(current.Path, result, options))
                {
                    if (ShouldSkipDirectory(childDirectory))
                        continue;

                    stack.Push((childDirectory, current.Depth + 1));
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(
            string path,
            SteamFallbackScanResult result,
            SteamDiscoveryOptions options)
        {
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> directories;

            try
            {
                directories = Directory.EnumerateDirectories(path, "*", enumerationOptions);
            }
            catch (UnauthorizedAccessException)
            {
                AddSkipped(result, path, options);
                yield break;
            }
            catch (SecurityException)
            {
                AddSkipped(result, path, options);
                yield break;
            }
            catch (IOException)
            {
                AddSkipped(result, path, options);
                yield break;
            }

            foreach (string directory in directories)
                yield return directory;
        }

        private static void TryAddSteamLibrary(
            string candidatePath,
            HashSet<string> foundLibraries,
            SteamFallbackScanResult result,
            SteamDiscoveryOptions options,
            IProgress<SteamFallbackScanProgress>? progress)
        {
            if (!TryNormalizePath(candidatePath, out string normalizedPath))
                return;

            if (!LooksLikeSteamLibrary(normalizedPath))
                return;

            if (foundLibraries.Add(normalizedPath))
            {
                progress?.Report(new SteamFallbackScanProgress(
                    normalizedPath,
                    result.DirectoriesScanned,
                    foundLibraries.Count));
            }
        }

        private static bool LooksLikeSteamLibrary(string path)
        {
            string steamAppsPath = Path.Combine(path, "steamapps");

            if (!Directory.Exists(steamAppsPath))
                return false;

            bool hasCommonFolder = Directory.Exists(Path.Combine(steamAppsPath, "common"));
            bool hasLibraryFoldersFile = File.Exists(Path.Combine(steamAppsPath, "libraryfolders.vdf"));
            bool hasManifestFile = false;

            try
            {
                hasManifestFile = Directory
                    .EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly)
                    .Any();
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }

            return hasCommonFolder || hasLibraryFoldersFile || hasManifestFile;
        }

        private static bool ShouldSkipDirectory(string path)
        {
            string normalizedPath;

            try
            {
                normalizedPath = Path.GetFullPath(path).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return true;
            }

            string directoryName = Path.GetFileName(normalizedPath);

            if (directoryName.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                return true;

            if (directoryName.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                return true;

            if (directoryName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                return true;

            if (directoryName.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalizedPath.Contains(
                    Path.Combine("ProgramData", "Microsoft"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedPath.Contains(
                    Path.Combine("AppData", "Local", "Temp"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedPath.Contains(
                    Path.Combine("AppData", "Local", "Microsoft", "Windows"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return true;
            }
        }

        private static bool TryNormalizePath(string rawPath, out string normalizedPath)
        {
            normalizedPath = string.Empty;

            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            try
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                normalizedPath = Path.GetFullPath(expandedPath);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private static void AddSkipped(
            SteamFallbackScanResult result,
            string path,
            SteamDiscoveryOptions options)
        {
            if (result.SkippedDirectories.Count >= options.MaxSkippedDirectoryLogEntries)
                return;

            result.SkippedDirectories.Add(path);
        }
    }
}