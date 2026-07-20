namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Stable identity of a sync provider. Values are persisted in
    /// sync-settings.json, so existing numeric assignments must never change.
    /// Only LocalFolder and Sftp are implemented today.
    /// </summary>
    public enum SyncProviderKind
    {
        Unknown = -1,
        LocalFolder = 0,
        Sftp = 1,
        GoogleDrive = 2,
        WebDav = 3,
        OneDrive = 4
    }
}
