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
        SkippedUnsafePath = 7
    }
}