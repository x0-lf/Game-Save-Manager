using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System;

namespace GameSaves.App.Models
{
    /// <summary>
    /// One scheduled job (a backup or a sync) beside its Windows task: the
    /// schedule, the task's state and runs, and any drift between the two.
    /// </summary>
    public sealed class ScheduledJobRowViewModel
    {
        public ScheduledJobRowViewModel(
            Guid jobId,
            string title,
            string subtitle,
            string? lastOutcome,
            BackupTaskStatus? task,
            string? taskError,
            string expectedProgram,
            string expectedArguments)
        {
            JobId = jobId;
            Title = title;
            Subtitle = subtitle;
            LastOutcome = lastOutcome;
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
            else if (!string.Equals(task.Arguments, expectedArguments, StringComparison.OrdinalIgnoreCase))
                Problem = "The Windows task's arguments no longer match this job. Apply a schedule to update it.";
        }

        public Guid JobId { get; }

        /// <summary>The game of a backup job, or the profile of a sync job.</summary>
        public string Title { get; }

        public string Subtitle { get; }

        public string? LastOutcome { get; }

        public BackupTaskStatus? WindowsTask { get; }

        public string ScheduleDisplay { get; }

        public string RunsDisplay { get; } = "";

        public bool HasRuns => RunsDisplay.Length > 0;

        /// <summary>Drift between the job and its task, or null when they agree.</summary>
        public string? Problem { get; }

        public string LastOutcomeDisplay => LastOutcome is null
            ? "No result recorded yet."
            : $"Last result: {LastOutcome}";

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
