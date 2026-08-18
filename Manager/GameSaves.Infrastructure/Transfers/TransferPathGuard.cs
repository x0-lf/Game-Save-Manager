namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Path normalization and containment checks shared by transfer preview
    /// and transfer execution. All checks are conservative: on any failure to
    /// normalize a path the check reports "not contained" so execution is blocked.
    /// </summary>
    internal static class TransferPathGuard
    {
        public static string? TryNormalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when a remote-supplied name is safe to append to a local root.
        /// A remote listing is untrusted input: a hostile or compromised server
        /// can return any name it likes, and <see cref="Path.Combine"/> silently
        /// discards its first argument when the second is rooted, so an
        /// unchecked name can escape the backup base entirely.
        /// </summary>
        /// <param name="relativePath">
        /// A forward-slash separated relative path, or a single name.
        /// </param>
        public static bool IsSafeRemoteRelativePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            // A backslash is a separator on Windows but an ordinary character
            // over SFTP, so "..\\..\\x" reaches here as one segment.
            if (relativePath.Contains('\\', StringComparison.Ordinal))
                return false;

            if (Path.IsPathRooted(relativePath))
                return false;

            foreach (string segment in relativePath.Split('/'))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    return false;

                if (segment is "." or "..")
                    return false;

                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;

                // Windows drops a trailing dot or space when it resolves a
                // name, so "run." and "run" can address the same directory.
                if (segment != segment.TrimEnd('.', ' '))
                    return false;
            }

            return true;
        }

        public static bool PathsEqual(string? left, string? right)
        {
            string? normalizedLeft = TryNormalize(left);
            string? normalizedRight = TryNormalize(right);

            if (normalizedLeft is null || normalizedRight is null)
                return false;

            return normalizedLeft.Equals(
                normalizedRight,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="root"/> itself
        /// or a descendant of it. False whenever either path cannot be normalized.
        /// </summary>
        public static bool IsUnderRoot(string? path, string? root)
        {
            string? normalizedPath = TryNormalize(path);
            string? normalizedRoot = TryNormalize(root);

            if (normalizedPath is null || normalizedRoot is null)
                return false;

            if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            return normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="path"/> is a strict descendant of
        /// <paramref name="root"/> (not the root itself).
        /// </summary>
        public static bool IsStrictlyUnderRoot(string? path, string? root)
        {
            return IsUnderRoot(path, root) && !PathsEqual(path, root);
        }

        /// <summary>
        /// True when the last segment of <paramref name="path"/> equals
        /// <paramref name="segment"/> (ordinal, case-insensitive).
        /// </summary>
        public static bool EndsWithSegment(string? path, string segment)
        {
            string? normalized = TryNormalize(path);

            if (normalized is null || string.IsNullOrWhiteSpace(segment))
                return false;

            return Path.GetFileName(normalized)
                .Equals(segment, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that a Steam userdata game-folder root is contained in the
        /// profile's userdata path and ends with the game AppID folder.
        /// </summary>
        public static bool IsValidUserDataGameFolderRoot(
            string? gameFolderRoot,
            string? profileUserDataPath,
            string appId)
        {
            return IsStrictlyUnderRoot(gameFolderRoot, profileUserDataPath) &&
                   EndsWithSegment(gameFolderRoot, appId);
        }
    }
}
