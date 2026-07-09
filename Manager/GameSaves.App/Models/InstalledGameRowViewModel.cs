using GameSaves.Core.Save;
using GameSaves.Core.Steam;

namespace GameSaves.App.Models
{
    public sealed class InstalledGameRowViewModel
    {
        public InstalledGameRowViewModel(InstalledGameSaveStatus status)
        {
            StatusModel = status;

            GameName = status.Game.Name;
            AppId = status.Game.AppId;
            InstallPath = status.Game.GamePath;
            LibraryPath = status.Game.LibraryPath;
            ApprovedMappings = status.ApprovedMappings;
            PendingMappings = status.PendingMappings;
            NeedsFixMappings = status.NeedsFixMappings;
            SavePathExists = status.SavePathExists;
            FileCount = status.FileCount;
            TotalBytes = status.TotalBytes;
            Status = status.StatusText;
            StatusKind = status.Status;
            Error = status.Error;
        }

        public InstalledGameSaveStatus StatusModel { get; }

        public SteamGame Game => StatusModel.Game;

        public string GameName { get; }

        public string AppId { get; }

        public string InstallPath { get; }

        public string LibraryPath { get; }

        public int ApprovedMappings { get; }

        public int PendingMappings { get; }

        public int NeedsFixMappings { get; }

        public bool SavePathExists { get; }

        public int FileCount { get; }

        public long TotalBytes { get; }

        public string TotalSizeDisplay => FormatBytes(TotalBytes);

        public string Status { get; }

        public GameSaveStatusKind StatusKind { get; }

        public string? Error { get; }

        public string ComboDisplay =>
            $"{GameName} ({AppId}) — {Status}";

        public override string ToString()
        {
            return ComboDisplay;
        }

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