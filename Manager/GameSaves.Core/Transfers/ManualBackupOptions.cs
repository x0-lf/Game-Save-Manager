namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Controls which sources are included when building a manual backup preview.
    /// </summary>
    public sealed class ManualBackupOptions
    {
        public static ManualBackupOptions Default { get; } = new();

        public bool IncludeSteamUserDataGameFolder { get; init; } = true;

        public bool IncludeApprovedMappings { get; init; } = true;

        public bool HasAnySource =>
            IncludeSteamUserDataGameFolder || IncludeApprovedMappings;
    }
}
