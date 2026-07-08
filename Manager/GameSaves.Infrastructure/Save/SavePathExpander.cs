using GameSaves.Core.Steam;
using System.Collections.Generic;

namespace GameSaves.Infrastructure.Save
{
    public sealed class SavePathExpander
    {
        /*
         * This supports mappings like:
         *  %APPDATA%\StardewValley\Saves
         *  {SteamRoot}\userdata\*\{AppId}\remote
         *  {GameInstallPath}\saves
         *  {Documents}\My Games\SomeGame
         */
        public IEnumerable<string> ExpandCandidatePaths(
            string pathTemplate,
            SteamGame game,
            string? steamRoot)
        {
            if (string.IsNullOrWhiteSpace(pathTemplate))
                yield break;

            string expanded = pathTemplate.Trim().Trim('"');

            expanded = ReplaceKnownFolderTokens(expanded);
            expanded = ReplaceSteamTokens(expanded, game, steamRoot);
            expanded = ExpandTilde(expanded);
            expanded = Environment.ExpandEnvironmentVariables(expanded);

            foreach (string candidate in ExpandWildcards(expanded))
            {
                if (TryNormalize(candidate, out string normalized))
                    yield return normalized;
            }
        }

        private static string ReplaceKnownFolderTokens(string value)
        {
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["%USERPROFILE%"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["%APPDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["%LOCALAPPDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["%PROGRAMDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["%DOCUMENTS%"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ["{UserProfile}"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["{AppData}"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["{LocalAppData}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["{ProgramData}"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["{Documents}"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            foreach (var replacement in replacements)
            {
                if (!string.IsNullOrWhiteSpace(replacement.Value))
                {
                    value = value.Replace(
                        replacement.Key,
                        replacement.Value,
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            return value;
        }

        private static string ReplaceSteamTokens(
            string value,
            SteamGame game,
            string? steamRoot)
        {
            value = value.Replace("{AppId}", game.AppId, StringComparison.OrdinalIgnoreCase);
            value = value.Replace("{GameName}", game.Name, StringComparison.OrdinalIgnoreCase);
            value = value.Replace("{LibraryRoot}", game.LibraryPath, StringComparison.OrdinalIgnoreCase);
            value = value.Replace("{GameInstallPath}", game.GamePath, StringComparison.OrdinalIgnoreCase);
            value = value.Replace("{InstallDir}", game.InstallDirectory, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(steamRoot))
            {
                value = value.Replace("{SteamRoot}", steamRoot, StringComparison.OrdinalIgnoreCase);
                value = value.Replace("{SteamUserData}", Path.Combine(steamRoot, "userdata"), StringComparison.OrdinalIgnoreCase);
            }

            return value;
        }

        private static string ExpandTilde(string value)
        {
            if (value == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (value.StartsWith("~/", StringComparison.Ordinal) ||
                value.StartsWith("~\\", StringComparison.Ordinal))
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    value[2..]);
            }

            return value;
        }

        private static IEnumerable<string> ExpandWildcards(string path)
        {
            if (!ContainsWildcard(path))
            {
                yield return path;
                yield break;
            }

            string? root = Path.GetPathRoot(path);

            if (string.IsNullOrWhiteSpace(root))
                yield break;

            string rest = path[root.Length..];

            string[] parts = rest.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<string> currentPaths = new[] { root };

            foreach (string part in parts)
            {
                if (ContainsWildcard(part))
                {
                    currentPaths = currentPaths.SelectMany(parent => SafeEnumerateFileSystemEntries(parent, part));
                }
                else
                {
                    currentPaths = currentPaths.Select(parent => Path.Combine(parent, part));
                }
            }

            foreach (string candidate in currentPaths)
                yield return candidate;
        }

        private static IEnumerable<string> SafeEnumerateFileSystemEntries(string parent, string pattern)
        {
            if (!Directory.Exists(parent))
                yield break;

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries(parent, pattern, options);
            }
            catch
            {
                yield break;
            }

            foreach (string entry in entries)
                yield return entry;
        }

        private static bool ContainsWildcard(string value)
        {
            return value.Contains('*') || value.Contains('?');
        }

        private static bool TryNormalize(string path, out string normalized)
        {
            normalized = string.Empty;

            try
            {
                normalized = Path.GetFullPath(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}