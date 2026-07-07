using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace GameSaves.Infrastructure
{
    public sealed class SteamLibraryFoldersReader
    {
        public IEnumerable<string> ReadLibraryPaths(string steamRoot)
        {
            foreach (string vdfPath in GetPossibleLibraryFoldersFiles(steamRoot))
            {
                if (!File.Exists(vdfPath))
                    continue;

                foreach (string libraryPath in ReadLibraryPathsFromFile(vdfPath))
                    yield return libraryPath;
            }
        }

        private static IEnumerable<string> GetPossibleLibraryFoldersFiles(string steamRoot)
        {
            yield return Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            yield return Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        }

        private static IEnumerable<string> ReadLibraryPathsFromFile(string vdfPath)
        {
            VProperty root;

            try
            {
                root = VdfConvert.Deserialize(File.ReadAllText(vdfPath));
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException ||
                ex is InvalidOperationException ||
                ex is ArgumentException)
            {
                yield break;
            }

            if (root.Value is not VObject libraryFoldersObject)
                yield break;

            foreach (VProperty child in libraryFoldersObject.Properties())
            {
                string? rawPath = ExtractLibraryPath(child.Value);

                if (!TryNormalizePath(rawPath, out string normalizedPath))
                    continue;

                if (!Directory.Exists(normalizedPath))
                    continue;

                yield return normalizedPath;
            }
        }

        private static string? ExtractLibraryPath(VToken token)
        {
            // Older Steam format:
            // "1" "D:\\SteamLibrary"
            if (token is VValue directValue)
                return directValue.ToString();

            // Modern Steam format:
            // "1"
            // {
            //     "path" "D:\\SteamLibrary"
            // }
            if (token is VObject objectValue &&
                objectValue["path"] is VValue pathValue)
            {
                return pathValue.ToString();
            }

            return null;
        }

        private static bool TryNormalizePath(string? rawPath, out string normalizedPath)
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
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                return false;
            }
        }
    }
}
