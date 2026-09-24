using GameSaves.App.Common;
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

        public string SizeDisplay => ByteSize.Format(Item.Bytes);

        public string Status => Item.Status;

        public string? Error => Item.Error;

        public bool HasBackup => !string.IsNullOrWhiteSpace(Item.BackupFile);

        public string BackupDisplay => HasBackup
            ? $"Backed up to: {Item.BackupFile}"
            : string.Empty;
    }
}
