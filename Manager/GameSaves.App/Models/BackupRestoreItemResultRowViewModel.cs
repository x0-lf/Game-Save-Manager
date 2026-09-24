using GameSaves.App.Common;
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

        public string SizeDisplay => ByteSize.Format(Result.Bytes);

        public bool Restored => Result.Restored;

        public string Status => Result.Status.ToString();

        public string? Error => Result.Error;

        public bool HasPreRestoreBackup =>
            !string.IsNullOrWhiteSpace(Result.PreRestoreBackupFile);

        public string PreRestoreBackupDisplay => HasPreRestoreBackup
            ? $"Replaced version backed up to: {Result.PreRestoreBackupFile}"
            : string.Empty;
    }
}
