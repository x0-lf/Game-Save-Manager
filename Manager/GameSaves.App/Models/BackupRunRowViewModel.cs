using GameSaves.Core.Transfers;
using System;

namespace GameSaves.App.Models
{
    public sealed class BackupRunRowViewModel
    {
        public BackupRunRowViewModel(TransferBackupRunInfo run)
        {
            Run = run;
        }

        public TransferBackupRunInfo Run { get; }

        public string KindText => Run.IsRestoreRun
            ? "Pre-restore backup"
            : "Pre-overwrite backup";

        public string GameName => Run.Manifest.Game;

        public string SteamAppId => Run.Manifest.SteamAppId;

        public string ProfilesDisplay =>
            $"{Run.Manifest.SourceAccountId} → {Run.Manifest.TargetAccountId}";

        public string StartedDisplay =>
            Run.Manifest.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public int FileCount => Run.Manifest.FileCount;

        public string TotalSizeDisplay => FormatBytes(Run.Manifest.TotalBytes);

        public string BackupRootPath => Run.BackupRootPath;

        public string ListDisplay =>
            $"{StartedDisplay} — {GameName} ({SteamAppId}) — {FileCount} file(s), {TotalSizeDisplay}";

        public override string ToString() => ListDisplay;

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
