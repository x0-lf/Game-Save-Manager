namespace GameSaves.Core.Transfers
{
    public enum SaveTransferItemStatus
    {
        Unknown = 0,
        DryRun = 1,
        Copied = 2,
        SkippedSourceMissing = 3,
        SkippedTargetExists = 4,
        SkippedSamePath = 5,
        Failed = 6,
        // The computed target file path escaped the expected target root
        // (path traversal safety check). Nothing was copied
        SkippedUnsafePath = 7,

        // Safe Mode: the pre-overwrite backup of the target file failed,
        // so the overwrite was refused. Nothing was copied
        SkippedBackupFailed = 8,

        // The whole preview item was blocked (same source and target path,
        // containment failure) and the user chose to skip blocked items.
        // Nothing was copied for this item
        SkippedBlocked = 9
    }
}