using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class TransferPreviewItemRowViewModel
    {
        public TransferPreviewItemRowViewModel(TransferPreviewItem item)
        {
            Item = item;
        }

        public TransferPreviewItem Item { get; }

        public string SourcePath => Item.SourcePath;

        public string TargetPath => Item.TargetPath;

        public bool SourceExists => Item.SourceExists;

        public bool TargetExists => Item.TargetExists;

        public int FileCount => Item.FileCount;

        public long TotalBytes => Item.TotalBytes;

        public string TotalSizeDisplay => FormatBytes(TotalBytes);

        public string MappingTemplate => Item.MappingTemplate;

        public string StatusText => Item.StatusText;

        public TransferConflictStatus ConflictStatus => Item.ConflictStatus;

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