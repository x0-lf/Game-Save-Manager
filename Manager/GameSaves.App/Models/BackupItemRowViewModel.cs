using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class BackupItemRowViewModel
    {
        public BackupItemRowViewModel(TransferOverwriteBackupItem item)
        {
            Item = item;
        }

        public TransferOverwriteBackupItem Item { get; }

        public string OriginalFile => Item.OriginalFile;

        public string BackupFile => Item.BackupFile;

        public string SizeDisplay => FormatBytes(Item.Bytes);

        public string Sha256 => Item.Sha256;

        public string Sha256Short => Item.Sha256.Length > 12
            ? Item.Sha256[..12]
            : Item.Sha256;

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
