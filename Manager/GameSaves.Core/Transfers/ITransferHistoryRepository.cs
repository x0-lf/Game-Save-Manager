namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// Persists executed runs (transfer copies, restores, manual backups) into
    /// the SQLite transfer_runs / transfer_items tables. Recording is an audit
    /// concern: a recording failure must never fail the run itself.
    /// </summary>
    public interface ITransferHistoryRepository
    {
        long RecordRun(TransferRunRecord record);

        IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit);

        IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId);

        int CountRuns();
    }
}
