namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Structural allowlist for intentionally mutable provider metadata.
    /// Remote paths use '/' regardless of the backing platform.
    /// </summary>
    internal static class RemoteProviderMetadataPath
    {
        internal const string SyncLog = ".gamesave-sync/sync-log.json";

        public static string Validate(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw InvalidPath();

            string normalized = relativePath.Trim().Replace('\\', '/');

            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath) ||
                normalized.Contains(':'))
            {
                throw InvalidPath();
            }

            string[] segments = normalized.Split('/', StringSplitOptions.None);

            if (segments.Length != 2 ||
                segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment) ||
                    segment is "." or "..") ||
                !string.Equals(segments[0], ".gamesave-sync", StringComparison.Ordinal) ||
                !string.Equals(segments[1], "sync-log.json", StringComparison.Ordinal))
            {
                throw InvalidPath();
            }

            return SyncLog;
        }

        private static ArgumentException InvalidPath() =>
            new(
                "Mutable provider metadata is restricted to .gamesave-sync/sync-log.json.",
                "relativePath");
    }
}
