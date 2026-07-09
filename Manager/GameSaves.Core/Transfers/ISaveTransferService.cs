namespace GameSaves.Core.Transfers
{
    public interface ISaveTransferService
    {
        Task<SaveTransferResult> ExecuteAsync(
            TransferPreviewPlan plan,
            SaveTransferOptions options,
            CancellationToken cancellationToken = default);
    }
}