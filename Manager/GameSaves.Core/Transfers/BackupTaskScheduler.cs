using System.Globalization;

namespace GameSaves.Core.Transfers
{
    public enum BackupScheduleFrequency
    {
        Daily = 0,
        Weekly = 1
    }

    /// <summary>
    /// When Windows starts a scheduled backup: every day, or one day a week,
    /// at a local time. <see cref="Day"/> only matters for a weekly schedule.
    /// </summary>
    public sealed record BackupSchedule(
        BackupScheduleFrequency Frequency,
        TimeOnly Time,
        DayOfWeek Day = DayOfWeek.Monday)
    {
        public override string ToString()
        {
            string time = Time.ToString("HH:mm", CultureInfo.InvariantCulture);

            return Frequency == BackupScheduleFrequency.Weekly
                ? $"Every {Day} at {time}"
                : $"Daily at {time}";
        }
    }

    /// <summary>The Windows task the app registers for one scheduled backup job.</summary>
    public sealed record BackupTaskDefinition(
        Guid JobId,
        string GameName,
        string ProgramPath,
        string Arguments,
        BackupSchedule Schedule);

    /// <summary>The task states Windows reports; the values are Task Scheduler's own.</summary>
    public enum BackupTaskState
    {
        Unknown = 0,
        Disabled = 1,
        Queued = 2,
        Ready = 3,
        Running = 4
    }

    /// <summary>
    /// A registered task as Windows reports it. <see cref="Schedule"/> is null
    /// when the trigger was changed outside the app into something it does not
    /// offer; <see cref="LastRun"/> and <see cref="NextRun"/> are null when
    /// there is none.
    /// </summary>
    public sealed record BackupTaskStatus(
        Guid JobId,
        BackupTaskState State,
        string? ProgramPath,
        string? Arguments,
        BackupSchedule? Schedule,
        DateTimeOffset? LastRun,
        int? LastResult,
        DateTimeOffset? NextRun);

    /// <summary>
    /// Windows Task Scheduler, as far as scheduled backups need it. Only the
    /// Windows implementation touches the real scheduler; tests use a fake.
    /// </summary>
    public interface IBackupTaskScheduler
    {
        /// <summary>
        /// Every scheduled backup task registered for the current user,
        /// including tasks whose job no longer exists.
        /// </summary>
        IReadOnlyList<BackupTaskStatus> GetTasks();

        /// <summary>Creates the job's task, or replaces its definition.</summary>
        void Register(BackupTaskDefinition definition);

        /// <summary>Removes the job's task. A task that is already gone is not an error.</summary>
        void Remove(Guid jobId);
    }
}
