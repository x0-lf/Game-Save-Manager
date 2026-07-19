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
    }
}
