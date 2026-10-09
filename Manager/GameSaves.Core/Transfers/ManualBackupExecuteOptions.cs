namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Execution options for a manual backup. Every run is a fresh timestamped
    /// run (a compressed container by default, or a folder), so there is no
    /// overwrite concept: nothing existing is ever replaced or deleted.
    /// </summary>
    public sealed class ManualBackupExecuteOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;

        /// <summary>How the run is labelled in History; a scheduled run says so.</summary>
        public TransferRunKind HistoryKind { get; init; } = TransferRunKind.ManualBackup;
    }
}
