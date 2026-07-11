namespace GameSaves.Core.Transfers
{
    public enum TransferConflictStatus
    {
        None = 0,
        SourceMissing = 1,
        TargetExists = 2,
        SameSourceAndTarget = 3,
        MappingNotProfileSpecific = 4,
        Error = 5,
        OutsideExpectedRoot = 6
    }
}