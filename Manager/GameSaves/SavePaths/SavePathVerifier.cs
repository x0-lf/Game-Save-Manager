namespace GameSave.SavePaths
{
    public sealed class SavePathVerifier
    {
        private readonly SavePathExpander _expander = new();

        public List<SavePathVerificationResult> Verify(
            SteamGame game,
            string? steamRoot,
            IEnumerable<SavePathMapping> mappings)
        {
            var results = new List<SavePathVerificationResult>();

            foreach (SavePathMapping mapping in mappings)
            {
                foreach (string candidatePath in _expander.ExpandCandidatePaths(mapping.PathTemplate, game, steamRoot))
                {
                    results.Add(VerifyCandidate(game, mapping, candidatePath));
                }
            }

            return results
                .OrderByDescending(result => result.Confidence)
                .ThenBy(result => result.NormalizedPath)
                .ToList();
        }

        private static SavePathVerificationResult VerifyCandidate(
            SteamGame game,
            SavePathMapping mapping,
            string normalizedPath)
        {
            try
            {
                bool directoryExists = Directory.Exists(normalizedPath);
                bool fileExists = File.Exists(normalizedPath);
                bool exists = mapping.PathKind switch
                {
                    SavePathKind.Directory => directoryExists,
                    SavePathKind.File => fileExists,
                    SavePathKind.Glob => directoryExists || fileExists,
                    _ => directoryExists || fileExists
                };

                bool isDirectory = directoryExists;
                int fileCount = 0;
                long totalBytes = 0;

                if (directoryExists)
                {
                    (fileCount, totalBytes) = CountDirectoryFiles(normalizedPath);
                }
                else if (fileExists)
                {
                    var info = new FileInfo(normalizedPath);
                    fileCount = 1;
                    totalBytes = info.Length;
                }

                int confidence = CalculateConfidence(
                    mapping,
                    game,
                    normalizedPath,
                    exists,
                    fileCount);

                return new SavePathVerificationResult(
                    mapping,
                    game.AppId,
                    game.Name,
                    mapping.PathTemplate,
                    normalizedPath,
                    exists,
                    isDirectory,
                    fileCount,
                    totalBytes,
                    confidence,
                    null);
            }
            catch (Exception ex)
            {
                return new SavePathVerificationResult(
                    mapping,
                    game.AppId,
                    game.Name,
                    mapping.PathTemplate,
                    normalizedPath,
                    false,
                    false,
                    0,
                    0,
                    0,
                    ex.Message);
            }
        }

        private static (int FileCount, long TotalBytes) CountDirectoryFiles(string directory)
        {
            int count = 0;
            long bytes = 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (string file in Directory.EnumerateFiles(directory, "*", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    count++;
                    bytes += info.Length;
                }
                catch
                {
                    // Ignore files that disappear or become inaccessible during verification.
                }
            }

            return (count, bytes);
        }

        private static int CalculateConfidence(
            SavePathMapping mapping,
            SteamGame game,
            string normalizedPath,
            bool exists,
            int fileCount)
        {
            int score = 35;

            // Mapping matched by Steam AppID.
            score += 20;

            if (exists)
                score += 25;

            if (fileCount > 0)
                score += 10;

            if (IsReasonableLocation(normalizedPath, game))
                score += 5;

            if (mapping.SourceName.Contains("PCGamingWiki", StringComparison.OrdinalIgnoreCase) ||
                mapping.SourceName.Contains("Manual", StringComparison.OrdinalIgnoreCase) ||
                mapping.SourceName.Contains("Curated", StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }

            return Math.Clamp(score, 0, 100);
        }

        private static bool IsReasonableLocation(string path, SteamGame game)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return IsChildOf(path, userProfile) ||
                   IsChildOf(path, game.GamePath) ||
                   IsChildOf(path, game.LibraryPath);
        }

        private static bool IsChildOf(string path, string parent)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
                return false;

            try
            {
                string normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string normalizedParent = Path.GetFullPath(parent)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
                       normalizedPath.StartsWith(
                           normalizedParent + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}