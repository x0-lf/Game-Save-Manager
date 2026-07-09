namespace GameSaves.Core.Transfers
{
    public sealed class SaveTransferOptions
    {
        public bool DryRun { get; init; } = true;

        public bool ConfirmExecution { get; init; } = false;

        public bool OverwriteExisting { get; init; } = false;

        public bool PreserveTimestamps { get; init; } = true;
    }
}