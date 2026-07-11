using GameSaves.Core.Platform;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Single source of truth for where backup runs live on disk, shared by
    /// the overwrite-backup writer and the backup-history reader.
    /// </summary>
    internal static class TransferBackupLocations
    {
        public const string ManifestFileName = "manifest.json";

        public static string GetBackupBasePath(IAppDatabasePathProvider databasePathProvider)
        {
            string appDataDirectory =
                Path.GetDirectoryName(databasePathProvider.GetDatabasePath())
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(appDataDirectory, "TransferBackups");
        }

        public static string MakeSafeName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();

            string cleaned = new string(
                value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            return string.IsNullOrWhiteSpace(cleaned)
                ? "Unknown"
                : cleaned;
        }
    }
}
