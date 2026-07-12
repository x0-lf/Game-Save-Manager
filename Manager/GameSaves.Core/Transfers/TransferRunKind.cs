namespace GameSaves.Core.Transfers
{
    public enum TransferRunKind
    {
        TransferCopy = 0,
        Restore = 1,
        ManualBackup = 2,
        Cleanup = 3,
        Sync = 4
    }
}
