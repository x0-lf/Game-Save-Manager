namespace GameSaves.Core.Transfers
{
    public sealed class SaveTransferOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;

        public bool OverwriteExisting { get; init; } = false;

        public bool PreserveTimestamps { get; init; } = true;

        // Safe Mode: when enabled, a target file is only overwritten after it
        // has been backed up successfully. A failed backup skips the overwrite.
        public bool BackupBeforeOverwrite { get; init; } = true;

        // When false (default), any blocked item (same source and target path,
        // containment failure) blocks the whole copy. When true, blocked items
        // are skipped and reported while the remaining safe items are copied.
        public bool SkipBlockedItems { get; init; } = false;
    }
}