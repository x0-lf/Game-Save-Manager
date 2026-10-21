using GameSaves.Core.Transfers;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Xml.Linq;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Registers scheduled backups and syncs with Windows Task Scheduler through its COM
    /// API, late bound so no interop package is needed. Each job is one task
    /// in the <c>\Game Save Manager</c> folder, named by the job id, which a
    /// standard user may create. A task runs as the user who saved it, only
    /// while that user is signed in, with least privilege: no password is
    /// stored and the user's DPAPI-protected secrets stay readable.
    /// </summary>
    public sealed class WindowsBackupTaskScheduler : IBackupTaskScheduler
    {
        internal const string FolderPath = @"\Game Save Manager";

        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;
        private const int TaskEnumHidden = 1;

        // Task Scheduler's own results, reported before or instead of an exit code.
        private const int TaskHasNotRun = 0x41303;
        private const int TaskIsRunning = 0x41301;
        private const int TaskTerminated = 0x41306;
        private const int TaskAlreadyRunning = unchecked((int)0x8004131F);

        // HRESULT_FROM_WIN32 of ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND:
        // no such task or folder.
        private const int FileNotFound = unchecked((int)0x80070002);
        private const int PathNotFound = unchecked((int)0x80070003);

        private static readonly XNamespace TaskXml = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        public IReadOnlyList<BackupTaskStatus> GetTasks()
        {
            if (!OperatingSystem.IsWindows())
                return [];

            dynamic? folder = OpenFolder();

            if (folder is null)
                return [];

            string currentUser = CurrentUserSid();
            dynamic tasks = folder.GetTasks(TaskEnumHidden);
            int count = tasks.Count;
            var statuses = new List<BackupTaskStatus>(count);

            for (int index = 1; index <= count; index++)
            {
                dynamic task = tasks[index];

                if (!Guid.TryParseExact((string)task.Name, "D", out Guid jobId))
                    continue;

                (string? userId, string? program, string? arguments, BackupSchedule? schedule) =
                    ReadTaskXml((string)task.Xml);

                // Another Windows user's backups are theirs to manage.
                if (!string.Equals(userId, currentUser, StringComparison.OrdinalIgnoreCase))
                    continue;

                statuses.Add(new BackupTaskStatus(
                    jobId,
                    (BackupTaskState)(int)task.State,
                    program,
                    arguments,
                    schedule,
                    RunTime((DateTime)task.LastRunTime),
                    (int)task.LastTaskResult,
                    RunTime((DateTime)task.NextRunTime)));
            }

            return statuses;
        }

        public void Register(BackupTaskDefinition definition)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Scheduled backups need Windows Task Scheduler.");

            string xml = RenderTaskXml(definition, CurrentUserSid(), DateTime.Now);

            // Creates the folder on first use. An update replaces the whole
            // definition, or fails and leaves the existing task as it was.
            Connect().GetFolder(@"\").RegisterTask(
                $@"{FolderPath}\{definition.JobId:D}",
                xml,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonInteractiveToken,
                null);
        }

        public void Remove(Guid jobId)
        {
            if (!OperatingSystem.IsWindows())
                return;

            dynamic? folder = OpenFolder();

            if (folder is null)
                return;

            try
            {
                folder.DeleteTask(jobId.ToString("D"), 0);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                // Already gone: the goal of removing it is met.
            }

            // Leave no empty folder behind once the last task is gone.
            try
            {
                if ((int)folder.GetTasks(TaskEnumHidden).Count == 0)
                    Connect().GetFolder(@"\").DeleteFolder(FolderPath.TrimStart('\\'), 0);
            }
            catch (Exception)
            {
                // Another task or a concurrent registration keeps it; harmless.
            }
        }

        /// <summary>The task's last result in words: a run's exit code, or Task Scheduler's own result.</summary>
        public static string DescribeLastResult(int? result) => result switch
        {
            null or TaskHasNotRun => "Not run yet",
            (int)ScheduledBackupOutcome.Completed => "Completed",
            1 => "Failed: the command line was malformed",
            (int)ScheduledBackupOutcome.Refused => "Refused before anything was written",
            (int)ScheduledBackupOutcome.AlreadyRunning => "Skipped: the previous run was still in progress",
            (int)ScheduledBackupOutcome.Failed => "Failed: nothing that existed was changed",
            TaskIsRunning => "Running",
            TaskTerminated => "Stopped by Task Scheduler",
            TaskAlreadyRunning => "Skipped: the previous run was still in progress",
            _ => $"Windows reported 0x{result:X8}"
        };

        /// <summary>
        /// The task definition: the current user, signed in, least privilege;
        /// one calendar trigger whose first run is still ahead, so registering
        /// never counts as a missed run; a missed run starts when it can; a
        /// second instance is never started; batteries neither block nor stop
        /// a run.
        /// </summary>
        internal static string RenderTaskXml(BackupTaskDefinition definition, string userId, DateTime localNow)
        {
            BackupSchedule schedule = definition.Schedule;
            DateOnly startDate = DateOnly.FromDateTime(localNow);

            if (startDate.ToDateTime(schedule.Time) <= localNow)
                startDate = startDate.AddDays(1);

            XElement scheduleElement = schedule.Frequency == BackupScheduleFrequency.Weekly
                ? new XElement(TaskXml + "ScheduleByWeek",
                    new XElement(TaskXml + "DaysOfWeek", new XElement(TaskXml + schedule.Day.ToString())),
                    new XElement(TaskXml + "WeeksInterval", 1))
                : new XElement(TaskXml + "ScheduleByDay",
                    new XElement(TaskXml + "DaysInterval", 1));

            return new XElement(TaskXml + "Task",
                new XAttribute("version", "1.2"),
                new XElement(TaskXml + "RegistrationInfo",
                    new XElement(TaskXml + "Description",
                        $"Game Save Manager {definition.Subject}. Change or remove it in the app.")),
                new XElement(TaskXml + "Triggers",
                    new XElement(TaskXml + "CalendarTrigger",
                        new XElement(TaskXml + "StartBoundary",
                            startDate.ToDateTime(schedule.Time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(TaskXml + "Enabled", true),
                        scheduleElement)),
                new XElement(TaskXml + "Principals",
                    new XElement(TaskXml + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskXml + "UserId", userId),
                        new XElement(TaskXml + "LogonType", "InteractiveToken"),
                        new XElement(TaskXml + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskXml + "Settings",
                    new XElement(TaskXml + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskXml + "StartWhenAvailable", true),
                    new XElement(TaskXml + "DisallowStartIfOnBatteries", false),
                    new XElement(TaskXml + "StopIfGoingOnBatteries", false),
                    new XElement(TaskXml + "Enabled", true)),
                new XElement(TaskXml + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(TaskXml + "Exec",
                        new XElement(TaskXml + "Command", definition.ProgramPath),
                        new XElement(TaskXml + "Arguments", definition.Arguments),
                        new XElement(TaskXml + "WorkingDirectory", Path.GetDirectoryName(definition.ProgramPath)))))
                .ToString();
        }

        /// <summary>
        /// Reads back what drift detection and the panel need. A schedule the
        /// app does not offer, such as one edited in Task Scheduler, reads as null.
        /// </summary>
        internal static (string? UserId, string? Program, string? Arguments, BackupSchedule? Schedule) ReadTaskXml(string xml)
        {
            XElement task = XDocument.Parse(xml).Root!;
            XElement? exec = task.Element(TaskXml + "Actions")?.Element(TaskXml + "Exec");

            return (
                (string?)task.Element(TaskXml + "Principals")?.Element(TaskXml + "Principal")?.Element(TaskXml + "UserId"),
                ((string?)exec?.Element(TaskXml + "Command"))?.Trim().Trim('"'),
                ((string?)exec?.Element(TaskXml + "Arguments"))?.Trim(),
                task.Element(TaskXml + "Triggers")?.Elements().ToList() is [{ Name.LocalName: "CalendarTrigger" } trigger]
                    ? ReadSchedule(trigger)
                    : null);
        }

        private static BackupSchedule? ReadSchedule(XElement trigger)
        {
            if (trigger.Element(TaskXml + "Repetition") is not null ||
                !DateTime.TryParse((string?)trigger.Element(TaskXml + "StartBoundary"),
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
                return null;

            TimeOnly time = TimeOnly.FromDateTime(start);

            if (trigger.Element(TaskXml + "ScheduleByDay") is { } byDay)
                return (int?)byDay.Element(TaskXml + "DaysInterval") is null or 1
                    ? new BackupSchedule(BackupScheduleFrequency.Daily, time)
                    : null;

            if (trigger.Element(TaskXml + "ScheduleByWeek") is { } byWeek &&
                (int?)byWeek.Element(TaskXml + "WeeksInterval") is null or 1 &&
                byWeek.Element(TaskXml + "DaysOfWeek")?.Elements().ToList() is [{ } day] &&
                Enum.TryParse(day.Name.LocalName, out DayOfWeek dayOfWeek))
                return new BackupSchedule(BackupScheduleFrequency.Weekly, time, dayOfWeek);

            return null;
        }

        // Task Scheduler reports "never" as a date before 2000.
        private static DateTimeOffset? RunTime(DateTime value) =>
            value.Year < 2000 ? null : new DateTimeOffset(value);

        // COM interop maps these to FileNotFoundException and
        // DirectoryNotFoundException, so match the HRESULT, not the type.
        private static bool IsNotFound(Exception ex) =>
            ex.HResult is FileNotFound or PathNotFound;

        [SupportedOSPlatform("windows")]
        private static dynamic Connect()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service;
        }

        // Null until the first task is registered.
        [SupportedOSPlatform("windows")]
        private static dynamic? OpenFolder()
        {
            try
            {
                return Connect().GetFolder(FolderPath);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string CurrentUserSid()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User!.Value;
        }
    }
}
