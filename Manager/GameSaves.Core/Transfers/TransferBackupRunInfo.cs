namespace GameSaves.Core.Transfers
{
    /// <summary>One discovered backup run: its manifest plus where it lives on disk.</summary>
    public sealed record TransferBackupRunInfo(
        string BackupRootPath,
        string ManifestPath,
        TransferBackupManifest Manifest)
    {
        public bool IsRestoreRun =>
            Manifest.Kind.Equals(
                OverwriteBackupContext.RestoreKind,
                StringComparison.OrdinalIgnoreCase);
    }
}
