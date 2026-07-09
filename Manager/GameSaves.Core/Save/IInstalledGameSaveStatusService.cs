namespace GameSaves.Core.Save
{
    public interface IInstalledGameSaveStatusService
    {
        Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            CancellationToken cancellationToken = default);
    }
}