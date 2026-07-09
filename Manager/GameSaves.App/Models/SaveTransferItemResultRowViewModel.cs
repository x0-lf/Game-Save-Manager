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

        public string SizeDisplay => FormatBytes(Bytes);

        public bool Copied => Result.Copied;

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