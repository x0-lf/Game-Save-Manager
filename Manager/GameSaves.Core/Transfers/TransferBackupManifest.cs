namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// The manifest.json schema written into every backup run folder or archive and read
    /// back by the backup history and catalog readers. Writers and readers share this record.
    /// </summary>
    public sealed record TransferBackupManifest(
        int SchemaVersion,
        string Kind,
        string Game,
        string SteamAppId,
        string SourceAccountId,
        string TargetAccountId,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<TransferOverwriteBackupItem> Items,
        string? Format = null,
        string? Compression = null,
        string? Notes = null)
    {
        public const int CurrentSchemaVersion = 2;
        public const int LegacySchemaVersion = 1;
        public const int MinSupportedSchemaVersion = 1;
        public const int MaxSupportedSchemaVersion = 2;

        public bool IsSupportedSchemaVersion =>
            SchemaVersion >= MinSupportedSchemaVersion && SchemaVersion <= MaxSupportedSchemaVersion;

        public bool TryValidate(out string? error)
        {
            if (SchemaVersion < MinSupportedSchemaVersion || SchemaVersion > MaxSupportedSchemaVersion)
            {
                error = $"Unsupported manifest schema version {SchemaVersion}. Supported versions are {MinSupportedSchemaVersion} through {MaxSupportedSchemaVersion}.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Game))
            {
                error = "Manifest is missing required Game name.";
                return false;
            }

            if (Items is null)
            {
                error = "Manifest items collection cannot be null.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Migrates this manifest to Schema v2 with guaranteed normalized relative paths.
        /// </summary>
        public TransferBackupManifest ToSchemaV2()
        {
            var upgradedItems = Items.Select(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.RelativePath))
                    return item;

                return item with { RelativePath = item.GetRelativePayloadPath() };
            }).ToList();

            return this with
            {
                SchemaVersion = CurrentSchemaVersion,
                Items = upgradedItems
            };
        }
    }
}
