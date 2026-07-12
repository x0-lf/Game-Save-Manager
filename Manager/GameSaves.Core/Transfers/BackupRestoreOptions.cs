using GameSaves.Core.Profiles;

namespace GameSaves.Core.Transfers
{
    public sealed class BackupRestoreOptions
    {
        public bool DryRun { get; init; } = true;

        // Where to restore/copy the backup files. OriginalPath is the safe
        // default; SelectedSteamProfileUserData requires TargetProfile.
        public BackupRestoreTargetMode TargetMode { get; init; } =
            BackupRestoreTargetMode.OriginalPath;

        public SteamProfile? TargetProfile { get; init; }

        // The approved mapping to restore into when TargetMode is
        // ApprovedMappingLocation. The service re-resolves the mapping from
        // the database at execution time; a resolved path is never passed in.
        public long? TargetMappingId { get; init; }

        public bool ConfirmExecution { get; init; } = false;

        // When false, only files that are currently missing are restored.
        // Overwriting a current file with its backed-up version is opt-in.
        public bool OverwriteExisting { get; init; } = false;

        // Verify each backup file against the SHA-256 recorded in the
        // manifest before restoring it; mismatches are never restored.
        public bool VerifyHashes { get; init; } = true;

        // Safe Mode: before a current file is overwritten by a restore,
        // it is backed up itself. A failed backup skips the restore.
        public bool BackupBeforeOverwrite { get; init; } = true;

        public bool PreserveTimestamps { get; init; } = true;
    }
}
