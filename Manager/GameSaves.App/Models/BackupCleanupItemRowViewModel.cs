using GameSaves.App.Common;
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

        public string SizeDisplay => ByteSize.Format(Result.Bytes);

        public string Status => Result.Status.ToString();

        public string? Error => Result.Error;
    }
}
