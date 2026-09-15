using GameSaves.Core.Transfers;
using System.Collections.Generic;

namespace GameSaves.App.Models
{
    public sealed class TransferRunRowViewModel
    {
        public TransferRunRowViewModel(TransferRunInfo run)
        {
            Run = run;
        }

        public TransferRunInfo Run { get; }

        public long Id => Run.Id;

        public string KindText => Run.Kind switch
        {
            TransferRunKind.Restore => "Restore",
            TransferRunKind.ManualBackup => "Manual backup",
            TransferRunKind.Cleanup => "Cleanup",
            TransferRunKind.Sync => "Sync",
            _ => "Transfer copy"
        };

        public string GameName => Run.GameName;

        public string SteamAppId => Run.SteamAppId;

        public string ProfilesDisplay =>
            Run.SourceAccountId == Run.TargetAccountId
                ? Run.SourceAccountId
                : $"{Run.SourceAccountId} → {Run.TargetAccountId}";

        public string StartedDisplay =>
            Run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public string CountsDisplay =>
            $"{Run.FilesCopied} copied, {Run.FilesSkipped} skipped" +
            (Run.FilesFailed > 0 ? $", {Run.FilesFailed} failed" : "");

        public string BytesDisplay => FormatBytes(Run.BytesCopied);

        public bool IsDryRun => Run.DryRun;

        public bool WasBlocked => Run.WasBlocked;

        public string? BlockedReason => Run.BlockedReason;

        public string VerificationDisplay =>
            Run.DryRun ? "Dry run"
            : Run.WasBlocked ? "Blocked"
            : Run.FilesFailed > 0 ? "Transfer failed"
            : "Copied (unverified)";

        public string VerificationGlyph =>
            Run.DryRun ? "◷"
            : Run.WasBlocked ? "⚠"
            : Run.FilesFailed > 0 ? "✕"
            : "✓";

        public string VerificationTooltip =>
            Run.DryRun ? "Simulated dry run; no files were written."
            : Run.WasBlocked ? $"Run was blocked: {Run.BlockedReason}"
            : Run.FilesFailed > 0 ? $"{Run.FilesFailed} file(s) failed during transfer."
            : "Files were copied. Cryptographic payload bytes were not re-read post-transfer.";

        public string FlagsDisplay
        {
            get
            {
                var flags = new List<string>();

                if (Run.DryRun)
                    flags.Add("dry run");

                if (Run.OverwriteEnabled)
                    flags.Add("overwrite");

                if (Run.FilesBackedUp > 0)
                    flags.Add($"{Run.FilesBackedUp} backed up");

                if (Run.WasBlocked)
                    flags.Add("blocked");

                return string.Join(" · ", flags);
            }
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
