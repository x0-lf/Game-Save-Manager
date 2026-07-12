using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class BackupCleanupItemRowViewModel
    {
        public BackupCleanupItemRowViewModel(BackupCleanupItemResult result)
        {
            Result = result;
        }

        public BackupCleanupItemResult Result { get; }

        public string RunDisplay =>
            $"{Result.Run.Manifest.Game} - {Result.Run.Manifest.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        public string BackupRootPath => Result.Run.BackupRootPath;

        public string SizeDisplay => FormatBytes(Result.Bytes);

        public string Status => Result.Status.ToString();

        public string? Error => Result.Error;

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            double kb = bytes / 1024.0;

            if (kb < 1024)
                return $"{kb:0.##} KB";

            double mb = kb / 1024.0;

            if (mb < 1024)
                return $"{mb:0.##} MB";

            double gb = mb / 1024.0;

            return $"{gb:0.##} GB";
        }
    }
}
