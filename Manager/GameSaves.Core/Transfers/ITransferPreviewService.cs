using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;

namespace GameSaves.Core.Transfers
{
    public interface ITransferPreviewService
    {
        Task<TransferPreviewPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile sourceProfile,
            SteamProfile targetProfile,
            TransferPreviewOptions? options = null,
            CancellationToken cancellationToken = default);
    }
}
