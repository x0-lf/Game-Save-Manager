namespace GameSaves.Core.Transfers
{
    public enum BackupRestoreItemStatus
    {
        Unknown = 0,
        DryRun = 1,
        Restored = 2,

        // The backup file recorded in the manifest no longer exists
        SkippedBackupMissing = 3,

        // The backup file's SHA-256 no longer matches the manifest;
        // a possibly corrupted backup is never restored
        SkippedHashMismatch = 4,

        // The current file exists and overwrite was not enabled
        SkippedTargetExists = 5,

        // The current file already has the exact backed-up content
        SkippedAlreadyMatches = 6,

        // Safe Mode: the pre-restore backup of the current file failed,
        // so the restore of this file was refused
        SkippedBackupFailed = 7,

        Failed = 8
    }
}
