using CommunityToolkit.Mvvm.ComponentModel;
using GameSaves.App.Common;
using GameSaves.Core.Transfers;
using System;

namespace GameSaves.App.Models
{
    public sealed partial class BackupRunRowViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VerificationDisplay))]
        [NotifyPropertyChangedFor(nameof(VerificationDetail))]
        [NotifyPropertyChangedFor(nameof(VerificationGlyph))]
        private VerificationStrength verification;

        public BackupRunRowViewModel(TransferBackupRunInfo run)
        {
            Run = run;
            verification = run.Verification;
        }

        public TransferBackupRunInfo Run { get; }

        public string KindText =>
            Run.IsManualRun ? "Manual backup"
            : Run.IsRestoreRun ? "Pre-restore backup"
            : "Pre-overwrite backup";

        public BackupContainerFormat ContainerFormat => Run.ContainerFormat;

        public string FormatDisplay => Run.ContainerFormat switch
        {
            BackupContainerFormat.Zip => "ZIP Archive",
            BackupContainerFormat.SevenZip => "7-Zip Archive",
            _ => "Folder"
        };

        public string VerificationDisplay => Verification switch
        {
            VerificationStrength.PayloadVerified => "Payload verified",
            VerificationStrength.ManifestMatch => "Manifest match",
            VerificationStrength.SidecarManifestMatch => "Sidecar manifest match",
            VerificationStrength.PayloadMismatch => "Payload mismatch",
            VerificationStrength.ManifestMismatch => "Manifest mismatch",
            VerificationStrength.MissingLocally => "Payload missing",
            VerificationStrength.Copied => "Copied (unverified)",
            _ => "Unverified"
        };

        public string VerificationGlyph => Verification switch
        {
            VerificationStrength.PayloadVerified => "✓✓",
            VerificationStrength.ManifestMatch => "✓",
            VerificationStrength.SidecarManifestMatch => "⚠",
            VerificationStrength.PayloadMismatch
                or VerificationStrength.ManifestMismatch
                or VerificationStrength.MissingLocally => "✕",
            _ => "◷"
        };

        public string VerificationDetail => Verification switch
        {
            VerificationStrength.PayloadVerified =>
                "All payload files in this backup were read and verified byte-for-byte against recorded SHA-256 cryptographic hashes.",
            VerificationStrength.ManifestMatch =>
                "The backup manifest is valid and its internal catalog was read. Payload file bytes have not yet been re-hashed.",
            VerificationStrength.SidecarManifestMatch =>
                "The backup metadata was read from an external sidecar file (.manifest.json). The archive container and payload bytes have not been verified.",
            VerificationStrength.PayloadMismatch =>
                "One or more payload files in this backup do not match their recorded SHA-256 hashes (possible corruption or tampering).",
            VerificationStrength.ManifestMismatch =>
                "The backup manifest is corrupt, incomplete, or conflicts with the catalog.",
            VerificationStrength.MissingLocally =>
                "The payload files of this backup could not be found on this computer, so they cannot be restored or verified.",
            VerificationStrength.Copied =>
                "This backup was copied or created, but neither its manifest nor its payload bytes have been verified yet.",
            _ => "No verification has been performed for this backup."
        };

        public string GameName => Run.Manifest.Game;

        public string SteamAppId => Run.Manifest.SteamAppId;

        public string ProfilesDisplay =>
            $"{Run.Manifest.SourceAccountId} → {Run.Manifest.TargetAccountId}";

        public string StartedDisplay =>
            Run.Manifest.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public int FileCount => Run.Manifest.FileCount;

        public string TotalSizeDisplay => ByteSize.Format(Run.Manifest.TotalBytes);

        public string BackupRootPath => Run.BackupRootPath;

        public string ListDisplay =>
            $"{StartedDisplay} — {GameName} ({SteamAppId}) — {FileCount} file(s), {TotalSizeDisplay}";

        public override string ToString() => ListDisplay;
    }
}
