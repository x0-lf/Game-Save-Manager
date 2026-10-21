using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One scheduled backup job beside its Windows task: the schedule, the
    /// task's state and runs, and any drift between the two.
    /// </summary>
    public sealed class ScheduledJobRowViewModel
    {
        public ScheduledJobRowViewModel(
            ScheduledBackupJob job,
            BackupTaskStatus? task,
            string? taskError,
            string expectedProgram)
        {
            Job = job;
            WindowsTask = task;

            if (taskError is not null)
            {
                ScheduleDisplay = $"The Windows task could not be read: {taskError}";
                return;
            }

            if (task is null)
            {
                ScheduleDisplay = "Not scheduled";
                Problem = "No Windows task. Nothing runs until you apply a schedule.";
                return;
            }

            ScheduleDisplay = $"{task.Schedule?.ToString() ?? "Custom schedule set in Task Scheduler"} · {StateText(task.State)}";

            RunsDisplay =
                (task.LastRun is { } lastRun
                    ? $"Last run {Format(lastRun)}: {WindowsBackupTaskScheduler.DescribeLastResult(task.LastResult)}"
                    : "Not run yet") +
                (task.NextRun is { } nextRun ? $" · Next run {Format(nextRun)}" : " · No next run");

            if (!string.Equals(task.ProgramPath, expectedProgram, StringComparison.OrdinalIgnoreCase))
                Problem = $"The Windows task starts {task.ProgramPath ?? "another program"}, not this app. Apply a schedule to update it.";
            else if (!string.Equals(task.Arguments, Program.RunJobArguments(job.Id), StringComparison.OrdinalIgnoreCase))
                Problem = "The Windows task's arguments no longer match this job. Apply a schedule to update it.";
        }

        public ScheduledBackupJob Job { get; }

        public BackupTaskStatus? WindowsTask { get; }

        public string GameName => Job.GameName;

        public string ProfileAndDestinationDisplay =>
            $"Steam account {Job.SteamAccountId} · To {Job.DestinationRoot}";

        public string ScheduleDisplay { get; }

        public string RunsDisplay { get; } = "";

        public bool HasRuns => RunsDisplay.Length > 0;

        /// <summary>Drift between the job and its task, or null when they agree.</summary>
        public string? Problem { get; }

        public string LastOutcomeDisplay => Job.LastOutcome is null
            ? "No result recorded yet."
            : $"Last result: {Job.LastOutcome}";

        private static string StateText(BackupTaskState state) => state switch
        {
            BackupTaskState.Ready => "Ready",
            BackupTaskState.Running => "Running now",
            BackupTaskState.Queued => "Queued",
            BackupTaskState.Disabled => "Disabled in Task Scheduler",
            _ => "State unknown"
        };

        private static string Format(DateTimeOffset value) =>
            value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
