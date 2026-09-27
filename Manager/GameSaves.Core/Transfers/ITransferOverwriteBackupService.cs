namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Backs up target files before they are overwritten by a transfer copy.
    /// One session per executed plan; the session records every backed-up file
    /// and persists a manifest so overwrites stay auditable and reversible.
    /// </summary>
    public interface ITransferOverwriteBackupService
    {
        /// <summary>
        /// Starts a backup run. <paramref name="baseDirectory"/> overrides where
        /// the run folder is created; null uses the application backup base,
        /// which is the location the backup history reads from.
        /// </summary>
        ITransferOverwriteBackupSession BeginSession(
            OverwriteBackupContext context,
            string? baseDirectory = null);
    }

    public interface ITransferOverwriteBackupSession : IDisposable
    {
        string BackupRootPath { get; }

        int FilesBackedUp { get; }

        /// <summary>
        /// Copies the target file into the backup run folder and returns the
        /// backup record. Throws when the backup cannot be completed; callers
        /// must then skip the overwrite (Safe Mode).
        /// </summary>
        TransferOverwriteBackupItem BackUpFile(string targetFile);

        /// <summary>
        /// Writes the manifest for this run, and packs the run into its
        /// container when the run is compressed. Idempotent; a session with no
        /// backed-up files writes nothing. Afterwards
        /// <see cref="BackupRootPath"/> names the run as stored: the folder, or
        /// the container file.
        /// </summary>
        void Complete();

        /// <summary>
        /// Where a file returned by <see cref="BackUpFile"/> lives once the run
        /// is complete: unchanged for a folder run, inside the container for a
        /// compressed one (for example <c>run.zip\files\C\save.dat</c>).
        /// </summary>
        string LocateBackupFile(string backupFile) => backupFile;
    }
}
