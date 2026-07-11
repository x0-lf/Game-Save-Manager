using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class BackupRestoreItemResultRowViewModel
    {
        public BackupRestoreItemResultRowViewModel(BackupRestoreItemResult result)
        {
            Result = result;
        }

        public BackupRestoreItemResult Result { get; }

        public string BackupFile => Result.BackupItem.BackupFile;

        public string TargetFile => Result.TargetFile;

        public string SizeDisplay => FormatBytes(Result.Bytes);

        public bool Restored => Result.Restored;

        public string Status => Result.Status.ToString();

        public string? Error => Result.Error;

        public bool HasPreRestoreBackup =>
            !string.IsNullOrWhiteSpace(Result.PreRestoreBackupFile);

        public string PreRestoreBackupDisplay => HasPreRestoreBackup
            ? $"Replaced version backed up to: {Result.PreRestoreBackupFile}"
            : string.Empty;

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
