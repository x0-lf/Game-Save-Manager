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
    /// </summary>
    public sealed record SanitizedTreeResult(
        IReadOnlyList<string> RelativePaths,
        string FormattedTree,
        int TotalFilesFound,
        int TotalDirectoriesFound);

    /// <summary>
    /// Traverses a game directory to extract structural file and folder layout while strictly
    /// scrubbing personal usernames, private file names, credentials, and sensitive tokens.
    /// </summary>
    public sealed class DirectoryTreeSanitizer
    {
        private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".key", ".pem", ".id_rsa", ".token", ".credentials", ".secret", ".pfx", ".cer"
        };

        private static readonly Regex UserDirectoryPattern = new(
            @"(?i)[\\/](Users|home)[\\/]([^\s\\/]+)",
            RegexOptions.Compiled);

        public SanitizedTreeResult Sanitize(
            string rootDirectory,
            int maxDepth = 3,
            int maxFiles = 200)
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

            string normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            void Traverse(string currentDir, int depth)
            {
                if (depth > maxDepth || fileCount >= maxFiles)
                    return;

                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch
                {
                    return;
                }

                foreach (string subDir in subDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (fileCount >= maxFiles)
                        break;

                    string dirName = Path.GetFileName(subDir);
                    if (IsIgnoredDirectory(dirName))
                        continue;

                    dirCount++;
                    string relativePath = GetSanitizedRelativePath(normalizedRoot, subDir);
                    relativePaths.Add(relativePath + "/");

                    string indent = new string(' ', depth * 2);
                    treeBuilder.AppendLine($"{indent}[DIR]  {SanitizeString(dirName)}");

                    Traverse(subDir, depth + 1);
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(currentDir);
                }
                catch
                {
                    return;
                }

                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (fileCount >= maxFiles)
                        break;

                    string fileName = Path.GetFileName(file);
                    if (IsIgnoredFile(fileName))
                        continue;

                    fileCount++;
                    string relativePath = GetSanitizedRelativePath(normalizedRoot, file);
                    relativePaths.Add(relativePath);

                    string indent = new string(' ', depth * 2);
                    string sanitizedName = SanitizeFileName(fileName);
                    treeBuilder.AppendLine($"{indent}[FILE] {sanitizedName}");
                }
            }

            Traverse(normalizedRoot, 1);

            return new SanitizedTreeResult(
                relativePaths,
                treeBuilder.ToString().TrimEnd(),
                fileCount,
                dirCount);
        }

        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/');

            // Scrub username in path
            string currentUsername = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(currentUsername))
            {
                normalized = normalized.Replace(currentUsername, "*", StringComparison.OrdinalIgnoreCase);
            }

            normalized = UserDirectoryPattern.Replace(normalized, "/$1/*");

            return normalized;
        }

        private static string GetSanitizedRelativePath(string root, string fullPath)
        {
            string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            return SanitizeRelativePath(rel);
        }

        private static string SanitizeString(string value)
        {
            string currentUsername = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(currentUsername) &&
                value.Equals(currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                return "[USERNAME]";
            }

            return value;
        }

        private static string SanitizeFileName(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (SensitiveExtensions.Contains(ext))
                return "[REDACTED_SECRET]" + ext;

            if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("credentials", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase))
            {
                return "[REDACTED_SECRET]";
            }

            return SanitizeString(fileName);
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
