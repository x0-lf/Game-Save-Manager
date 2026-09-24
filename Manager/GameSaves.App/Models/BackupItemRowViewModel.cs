using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed partial class BackupItemRowViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusDisplay))]
        [NotifyPropertyChangedFor(nameof(StatusGlyph))]
        private bool? isVerified;

        public BackupItemRowViewModel(TransferOverwriteBackupItem item)
        {
            Item = item;
        }

        public TransferOverwriteBackupItem Item { get; }

        public string OriginalFile => Item.OriginalFile;

        public string BackupFile => Item.BackupFile;

        public string SizeDisplay => ByteSize.Format(Item.Bytes);

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
    }
}
