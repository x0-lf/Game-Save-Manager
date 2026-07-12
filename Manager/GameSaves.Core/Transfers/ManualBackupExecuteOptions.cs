namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Execution options for a manual backup. Every run writes into a fresh
    /// timestamped folder, so there is no overwrite concept: nothing existing
    /// is ever replaced or deleted.
    /// </summary>
    public sealed class ManualBackupExecuteOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;
    }
}
