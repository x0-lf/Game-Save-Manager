using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;

namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// On-demand backup of one game's saves for one Steam profile into a fresh
    /// timestamped run folder with a SHA-256 manifest. Copy-only: source files
    /// are never modified, and existing backups are never overwritten.
    /// </summary>
    public interface IManualBackupService
    {
        Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default);

        Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default);
    }
}
