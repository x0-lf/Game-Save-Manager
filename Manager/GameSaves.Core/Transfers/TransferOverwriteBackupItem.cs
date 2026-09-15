namespace GameSaves.Core.Transfers
{
    public sealed record TransferOverwriteBackupItem(
        string OriginalFile,
        string BackupFile,
        long Bytes,
        string Sha256,
        DateTimeOffset BackedUpUtc,
        string? RelativePath = null)
    {
        /// <summary>
        /// Returns the normalized, forward-slash relative payload path inside the backup container
        /// (e.g. "files/C/Steam/userdata/1/2/save.dat").
        /// </summary>
        public string GetRelativePayloadPath()
        {
            if (!string.IsNullOrWhiteSpace(RelativePath))
            {
                return NormalizeRelativePath(RelativePath);
            }

            return ExtractRelativePayloadPath(BackupFile);
        }

        /// <summary>
        /// Resolves the payload file on disk. A manifest can come from an archive or a
        /// remote container that this machine did not write, so the recorded absolute
        /// <see cref="BackupFile"/> is honoured only while it stays inside the run root.
        /// Everything else resolves through the relative payload path, which keeps
        /// backups portable across machines, drives, and folder relocations without
        /// letting a manifest nominate an arbitrary file on this machine.
        /// </summary>
        public string ResolveBackupFile(string backupRootPath)
        {
            if (IsUnderRoot(BackupFile, backupRootPath) && File.Exists(BackupFile))
                return BackupFile;

            string relative = GetRelativePayloadPath().Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(backupRootPath, relative);
        }

        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        /// <summary>
        /// True when a manifest-supplied payload path is safe to append to a run root.
        /// Manifests inside imported archives and downloaded containers are untrusted
        /// input, and <see cref="Path.Combine(string, string)"/> silently discards its
        /// first argument when the second is rooted, so an unchecked value can address
        /// any file on the machine.
        /// </summary>
        public static bool IsSafeRelativePayloadPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            if (Path.IsPathRooted(relativePath))
                return false;

            // "C:file.txt" is drive-relative, not rooted, and still escapes the root.
            if (relativePath.Length >= 2 && relativePath[1] == ':')
                return false;

            char[] invalid = Path.GetInvalidFileNameChars();

            foreach (string segment in relativePath.Split('/', '\\'))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    return false;

                if (segment is "." or "..")
                    return false;

                if (segment.IndexOfAny(invalid) >= 0)
                    return false;

                // Windows drops a trailing dot or space when it resolves a name,
                // so "save." and "save" can address the same file.
                if (segment != segment.TrimEnd('.', ' '))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True when this item's payload path is usable as a contained relative path.
        /// </summary>
        public bool HasSafeRelativePayloadPath() =>
            IsSafeRelativePayloadPath(GetRelativePayloadPath());

        private static bool IsUnderRoot(string? path, string? root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string ExtractRelativePayloadPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/');
            int idx = normalized.IndexOf("/files/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return normalized[(idx + 1)..];
            }

            if (normalized.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            string fileName = Path.GetFileName(path);
            return $"files/{fileName}";
        }
    }
}
