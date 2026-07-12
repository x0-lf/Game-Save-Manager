namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// A named, saved manual-backup configuration: destination folder plus
    /// source selection. Presets are user convenience data stored in SQLite.
    /// </summary>
    public sealed record ManualBackupPreset(
        long Id,
        string Name,
        string DestinationRoot,
        bool IncludeSteamUserDataGameFolder,
        bool IncludeApprovedMappings,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? LastUsedUtc);
}
