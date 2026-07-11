namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Controls which sources are included when building a transfer preview.
    /// </summary>
    public sealed class TransferPreviewOptions
    {
        public static TransferPreviewOptions Default { get; } = new();

        /// <summary>
        /// Include the whole game-specific Steam userdata folder
        /// (&lt;SteamRoot&gt;\userdata\&lt;AccountId&gt;\&lt;AppId&gt;) as a first-class
        /// preview item, independent of approved mappings.
        /// </summary>
        public bool IncludeSteamUserDataGameFolder { get; init; } = true;

        /// <summary>
        /// Include items expanded from approved save-path mappings.
        /// </summary>
        public bool IncludeApprovedMappings { get; init; } = true;
    }
}
