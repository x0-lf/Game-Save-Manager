namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// The container a new backup run is stored in (BACKUP-001). Read when a
    /// run begins, so a change in Settings applies to the next run and never
    /// to one already written. Existing runs keep the format they have.
    /// </summary>
    public sealed class BackupStoragePreference(Func<BackupContainerFormat> newRunFormat)
    {
        public BackupContainerFormat NewRunFormat => newRunFormat();
    }
}
