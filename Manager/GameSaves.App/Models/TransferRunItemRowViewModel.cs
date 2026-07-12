using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class TransferRunItemRowViewModel
    {
        public TransferRunItemRowViewModel(TransferRunItemRecord item)
        {
            Item = item;
        }

        public TransferRunItemRecord Item { get; }

        public string SourceFile => Item.SourceFile;

        public string TargetFile => Item.TargetFile;

        public string SizeDisplay => FormatBytes(Item.Bytes);

        public string Status => Item.Status;

        public string? Error => Item.Error;

        public bool HasBackup => !string.IsNullOrWhiteSpace(Item.BackupFile);

        public string BackupDisplay => HasBackup
            ? $"Backed up to: {Item.BackupFile}"
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
