namespace GameSaves.Core.Sync
{
    /// <summary>
    /// An unattended sync the user created on purpose: one saved remote
    /// profile, the direction, and the archive switch, exactly as previewed
    /// when it was saved. It stores the profile's identity, not its settings,
    /// so every run reads the profile again and rebuilds the preview before
    /// anything is copied. A run copies whichever backup runs are missing on
    /// either side at that time, including runs made after the job was saved.
    /// One job per profile.
    /// </summary>
    public sealed record ScheduledSyncJob(
        Guid Id,
        Guid ProfileId,
        string ProfileName,
        bool Upload,
        bool Download,
        bool ArchiveSync,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? LastRunUtc = null,
        string? LastOutcome = null)
    {
        /// <summary>
        /// Why a provider cannot run with nobody at the keyboard, or null when
        /// it can. The Sync page refuses to schedule such a profile and every
        /// run checks again.
        /// </summary>
        public static string? UnattendedRefusal(SyncProviderKind kind) => kind switch
        {
            SyncProviderKind.LocalFolder or
            SyncProviderKind.GoogleDrive or
            SyncProviderKind.OneDrive or
            SyncProviderKind.WebDav => null,

            SyncProviderKind.Sftp =>
                "SFTP profiles cannot run unattended: their passwords and key passphrases are never stored, so a run with nobody at the keyboard could not sign in.",

            _ => $"The {kind} provider is not available in this version, so it cannot run unattended."
        };
    }

    /// <summary>
    /// The opt-in list of scheduled syncs. Nothing is scheduled until the user
    /// adds a job, and removing a job revokes it: a later trigger for that id
    /// is refused and recorded.
    /// </summary>
    public interface IScheduledSyncJobRepository
    {
        IReadOnlyList<ScheduledSyncJob> GetAll();

        void Add(ScheduledSyncJob job);

        void RecordOutcome(Guid id, DateTimeOffset runUtc, string outcome);

        void Delete(Guid id);
    }
}
