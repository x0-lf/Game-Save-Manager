namespace GameSaves.Core.Transfers
{
    /// <summary>
    /// An unattended backup the user created on purpose: one game for one
    /// Steam profile into one destination. It stores identities rather than
    /// save paths, so every run discovers the game and profile again and
    /// rebuilds the preview before anything is written.
    /// </summary>
    public sealed record ScheduledBackupJob(
        Guid Id,
        string SteamAppId,
        string GameName,
        string SteamAccountId,
        string DestinationRoot,
        bool IncludeSteamUserDataGameFolder,
        bool IncludeApprovedMappings,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? LastRunUtc = null,
        string? LastOutcome = null);

    /// <summary>
    /// The opt-in list of scheduled backups. Nothing is scheduled until the
    /// user adds a job, and removing a job revokes it: a later trigger for
    /// that id is refused and recorded.
    /// </summary>
    public interface IScheduledBackupJobRepository
    {
        IReadOnlyList<ScheduledBackupJob> GetAll();

        void Add(ScheduledBackupJob job);

        void RecordOutcome(Guid id, DateTimeOffset runUtc, string outcome);

        void Delete(Guid id);
    }
}
