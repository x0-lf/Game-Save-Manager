namespace GameSaves.Core.Transfers
{
    public enum BackupCleanupItemStatus
    {
        Unknown = 0,
        DryRun = 1,
        Deleted = 2,

        // The run folder is not strictly inside the application backup base;
        // it is never deleted
        SkippedOutsideBase = 3,

        // The folder no longer looks like a backup run (missing manifest);
        // it is never deleted
        SkippedInvalidRun = 4,

        Failed = 5
    }
}
