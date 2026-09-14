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
        /// Resolves the file on disk: if the recorded BackupFile exists at its absolute location,
        /// it is used directly. Otherwise, resolves relative to <paramref name="backupRootPath"/>.
        /// This enables portable backups to be restored across machines, drives, or folder relocations.
        /// </summary>
        public string ResolveBackupFile(string backupRootPath)
        {
            if (File.Exists(BackupFile))
                return BackupFile;

            string relative = GetRelativePayloadPath().Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(backupRootPath, relative);
        }

        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
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
