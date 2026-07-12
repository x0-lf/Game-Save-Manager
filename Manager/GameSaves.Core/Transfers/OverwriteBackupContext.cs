namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Describes who is asking for an overwrite-backup session and why,
    /// so transfer copies and restores can share the same backup engine.
    /// </summary>
    public sealed record OverwriteBackupContext(
        string Kind,
        string Game,
        string SteamAppId,
        string SourceAccountId,
        string TargetAccountId)
    {
        public const string TransferKind = "TransferOverwriteBackup";
        public const string RestoreKind = "RestoreOverwriteBackup";
        public const string ManualKind = "ManualBackup";
    }
}
