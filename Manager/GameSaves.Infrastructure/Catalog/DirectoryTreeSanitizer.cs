using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameSaves.Infrastructure.Catalog
{
    /// <summary>
    /// Result of sanitizing a directory tree for AI analysis.
    /// <see cref="RelativePaths"/> stay on this machine (engine fingerprinting) and are not scrubbed;
    /// <see cref="FormattedTree"/> is the text that may be sent to an AI model and is scrubbed.
    /// </summary>
    public sealed record SanitizedTreeResult(
        IReadOnlyList<string> RelativePaths,
        string FormattedTree,
        int TotalFilesFound,
        int TotalDirectoriesFound);

    /// <summary>
    /// Traverses a game directory to extract structural file and folder layout while excluding
    /// credential files and scrubbing usernames, long numeric account IDs and e-mail addresses
    /// from the formatted tree.
    /// </summary>
    public sealed class DirectoryTreeSanitizer
    {
        private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".key", ".pem", ".id_rsa", ".token", ".credentials", ".secret", ".pfx", ".cer"
        };

        // Junctions and symlinks can lead outside the game folder (or loop); skip them.
        private static readonly EnumerationOptions EnumerationOptions = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        private static readonly Regex EmailPattern = new(
            @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
            RegexOptions.Compiled);

        // SteamID64 (17 digits) and account IDs (8+ digits) identify a person.
        private static readonly Regex LongNumberPattern = new(
            @"\d{8,}",
            RegexOptions.Compiled);

        /// <param name="maxEntries">Budget for files and directories together.</param>
        public SanitizedTreeResult Sanitize(
            string rootDirectory,
            int maxDepth = 3,
            int maxEntries = 200)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return new SanitizedTreeResult(
                    Array.Empty<string>(),
                    "<directory does not exist>",
                    0,
                    0);
            }

            var relativePaths = new List<string>();
            var treeBuilder = new StringBuilder();
            int fileCount = 0;
            int dirCount = 0;

            string normalizedRoot = Path.GetFullPath(rootDirectory);

            void Traverse(string currentDir, int depth)
            {
                if (depth > maxDepth)
                    return;

                string indent = new string(' ', depth * 2);

                // Files first: root markers (UnityPlayer.dll, *.uproject, Game.rgss3a) must be
                // seen before subfolders spend the entry budget.
                foreach (string file in List(Directory.EnumerateFiles, currentDir))
                {
                    if (fileCount + dirCount >= maxEntries)
                        return;

                    string fileName = Path.GetFileName(file);
                    if (IsIgnoredFile(fileName) || IsSecretFile(fileName))
                        continue;

                    fileCount++;
                    relativePaths.Add(Path.GetRelativePath(normalizedRoot, file).Replace('\\', '/'));
                    treeBuilder.AppendLine($"{indent}[FILE] {Scrub(fileName)}");
                }

                foreach (string subDir in List(Directory.EnumerateDirectories, currentDir))
                {
                    if (fileCount + dirCount >= maxEntries)
                        return;

                    string dirName = Path.GetFileName(subDir);
                    if (IsIgnoredDirectory(dirName))
                        continue;

                    dirCount++;
                    relativePaths.Add(Path.GetRelativePath(normalizedRoot, subDir).Replace('\\', '/') + "/");
                    treeBuilder.AppendLine($"{indent}[DIR]  {Scrub(dirName)}");

                    Traverse(subDir, depth + 1);
                }
            }

            Traverse(normalizedRoot, 1);

            return new SanitizedTreeResult(
                relativePaths,
                treeBuilder.ToString().TrimEnd(),
                fileCount,
                dirCount);
        }

        private static List<string> List(
            Func<string, string, EnumerationOptions, IEnumerable<string>> enumerate,
            string directory)
        {
            try
            {
                return enumerate(directory, "*", EnumerationOptions)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (IOException)
            {
                // The folder vanished or cannot be read: treat it as empty.
                return new List<string>();
            }
        }

        /// <summary>
        /// Removes personal identifiers from a file or folder name before it can leave the machine.
        /// </summary>
        internal static string Scrub(string value)
        {
            string scrubbed = EmailPattern.Replace(value, "[EMAIL]");

            string userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
                scrubbed = scrubbed.Replace(userName, "[USERNAME]", StringComparison.OrdinalIgnoreCase);

            return LongNumberPattern.Replace(scrubbed, "[ID]");
        }

        private static bool IsSecretFile(string fileName)
        {
            return SensitiveExtensions.Contains(Path.GetExtension(fileName)) ||
                   fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith("credentials", StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
                   fileName.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredDirectory(string dirName)
        {
            return dirName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals(".claude", StringComparison.OrdinalIgnoreCase) ||
                   dirName.Equals(".gemini", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredFile(string fileName)
        {
            return fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
        }
    }
}
