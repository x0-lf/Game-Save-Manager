using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed partial class BackupItemRowViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusGlyph))]
        [NotifyPropertyChangedFor(nameof(IsVerifiedSuccess))]
        [NotifyPropertyChangedFor(nameof(IsVerifiedFailed))]
        private bool? isVerified;

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

        public string StatusDisplay => IsVerified switch
        {
            true => "Verified",
            false => "Mismatch",
            null => "Recorded"
        };

        public string StatusGlyph => IsVerified switch
        {
            true => "✓",
            false => "✕",
            null => "•"
        };

        public bool IsVerifiedSuccess => IsVerified == true;
        public bool IsVerifiedFailed => IsVerified == false;

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
