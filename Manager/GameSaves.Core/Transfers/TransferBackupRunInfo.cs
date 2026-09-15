namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// One discovered backup run: its manifest plus where it lives on disk and its storage format.
    /// Uncompressed folders, ZIPs, and 7z archives share this identical catalog model.
    /// </summary>
    public sealed record TransferBackupRunInfo(
        string BackupRootPath,
        string ManifestPath,
        TransferBackupManifest Manifest,
        BackupContainerFormat ContainerFormat = BackupContainerFormat.Folder,
        VerificationStrength Verification = VerificationStrength.ManifestMatch)
    {
        public bool IsRestoreRun =>
            Manifest.Kind.Equals(
                OverwriteBackupContext.RestoreKind,
                StringComparison.OrdinalIgnoreCase);

        public bool IsManualRun =>
            Manifest.Kind.Equals(
                OverwriteBackupContext.ManualKind,
                StringComparison.OrdinalIgnoreCase);

        public bool IsFolder => ContainerFormat == BackupContainerFormat.Folder;
        public bool IsZip => ContainerFormat == BackupContainerFormat.Zip;
        public bool IsSevenZip => ContainerFormat == BackupContainerFormat.SevenZip;
        public bool IsArchive => IsZip || IsSevenZip;

        public bool IsPayloadVerified => Verification == VerificationStrength.PayloadVerified;
        public bool IsSidecarMatch => Verification == VerificationStrength.SidecarManifestMatch;
    }
}
