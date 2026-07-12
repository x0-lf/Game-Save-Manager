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
    }
}
