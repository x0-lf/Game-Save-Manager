using GameSaves.Core.Transfers;

namespace GameSaves.Core.Sync
{
    public sealed class SyncOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;

        /// <summary>Copy local-only runs to the remote.</summary>
        public bool Upload { get; init; } = true;

        /// <summary>Copy remote-only runs to the local backup base.</summary>
        public bool Download { get; init; } = true;

        /// <summary>
        /// When set, only plan items with these run names are copied; other
        /// actionable items are reported as deselected. Null copies everything
        /// the plan allows.
        /// </summary>
        public IReadOnlyCollection<string>? OnlyRunNames { get; init; }

        /// <summary>Reported after every copied file during execution.</summary>
        public IProgress<SyncProgress>? Progress { get; init; }

        /// <summary>
        /// When true, uploads backup runs as single compressed container archives
        /// (.7z or .zip) with sidecar manifests, reducing remote API requests to &lt;= 2.
        /// </summary>
        public bool ArchiveSync { get; init; } = false;

        /// <summary>
        /// Archive container format to use when ArchiveSync is active. Defaults to SevenZip.
        /// </summary>
        public BackupContainerFormat ArchiveFormat { get; init; } = BackupContainerFormat.SevenZip;

        /// <summary>
        /// Compression preset when exporting runs for archive sync. Defaults to Optimal.
        /// </summary>
        public BackupCompressionPreset CompressionPreset { get; init; } = BackupCompressionPreset.Optimal;
    }
}
