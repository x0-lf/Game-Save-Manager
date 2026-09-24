using GameSaves.App.Common;
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

        public TransferSourceType SourceType => Item.SourceType;

        public string SourceTypeText => Item.SourceType switch
        {
            TransferSourceType.SteamUserDataGameFolder => "Steam userdata folder",
            TransferSourceType.ApprovedMapping => "Approved mapping",
            _ => "Manual path"
        };

        public string SourcePath => Item.SourcePath;

        public string TargetPath => Item.TargetPath;

        public bool SourceExists => Item.SourceExists;

        public bool TargetExists => Item.TargetExists;

        public string SourceExistsText => Item.SourceExists ? "Exists" : "Missing";

        public string TargetExistsText => Item.TargetExists ? "Exists" : "New";

        public int FileCount => Item.FileCount;

        public long TotalBytes => Item.TotalBytes;

        public string TotalSizeDisplay => ByteSize.Format(TotalBytes);

        public string MappingTemplate => Item.MappingTemplate ?? string.Empty;

        public string StatusText => Item.StatusText;

        public string ActionText => Item.ActionText;

        public TransferConflictStatus ConflictStatus => Item.ConflictStatus;
    }
}
