using GameSaves.App.Common;
using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class SaveTransferItemResultRowViewModel
    {
        public SaveTransferItemResultRowViewModel(
            SaveTransferItemResult result)
        {
            Result = result;
        }

        public SaveTransferItemResult Result { get; }

        public string SourceFile => Result.SourceFile;

        public string TargetFile => Result.TargetFile;

        public long Bytes => Result.Bytes;

        public string SizeDisplay => ByteSize.Format(Bytes);

        public bool Copied => Result.Copied;

        public string Status => Result.Status.ToString();

        public string? Error => Result.Error;

        public string? BackupFile => Result.BackupFile;

        public bool HasBackup => !string.IsNullOrWhiteSpace(Result.BackupFile);

        public string BackupDisplay => HasBackup
            ? $"Previous version backed up to: {Result.BackupFile}"
            : string.Empty;
    }
}