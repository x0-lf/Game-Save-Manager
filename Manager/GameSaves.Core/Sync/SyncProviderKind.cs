namespace GameSaves.Core.Sync
{
    /// <summary>
    /// Stable identity of a sync provider. Values are persisted in
    /// sync-settings.json, so existing numeric assignments must never change.
    /// LocalFolder, Sftp, and GoogleDrive are implemented; WebDav and OneDrive
    /// are declared but unavailable. SyncProviderCatalog is the authority.
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
