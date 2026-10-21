using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Transfers;
using System.Xml.Linq;

namespace GameSaves.Tests;

/// <summary>
/// The Windows task definition the app registers, and what it reads back.
/// Everything here is pure XML: no test registers a real Windows task.
/// </summary>
public sealed class BackupTaskSchedulerTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string UserSid = "S-1-5-21-1000-2000-3000-1001";
    private const string ProgramPath = @"C:\Apps\Game Save Manager\GameSaves.App.exe";

    private static readonly Guid JobId = Guid.Parse("2b9a4c1e-7d55-4f0e-9a51-0c6f3e8d2a10");

    private static BackupTaskDefinition Definition(BackupSchedule schedule, string game = "Elden Ring") =>
        new(JobId, game, ProgramPath, $"--run-job {JobId:D}", schedule);

    private static readonly BackupSchedule DailyAtEight =
        new(BackupScheduleFrequency.Daily, new TimeOnly(20, 0));

    [Fact]
    public void TheTask_RunsTheJobAsTheCurrentUser_SignedIn_WithLeastPrivilege()
    {
        XElement task = XElement.Parse(WindowsBackupTaskScheduler.RenderTaskXml(
            Definition(DailyAtEight, game: "Tom & Jerry <Deluxe>"), UserSid, new DateTime(2026, 10, 9, 8, 0, 0)));

        XElement principal = Assert.Single(task.Element(Ns + "Principals")!.Elements());
        Assert.Equal(UserSid, (string?)principal.Element(Ns + "UserId"));
        Assert.Equal("InteractiveToken", (string?)principal.Element(Ns + "LogonType"));
        Assert.Equal("LeastPrivilege", (string?)principal.Element(Ns + "RunLevel"));

        // No password is stored, so nothing could run while the user is signed out.
        Assert.DoesNotContain("Password", task.ToString());
        Assert.DoesNotContain("S4U", task.ToString());

        XElement exec = Assert.Single(task.Element(Ns + "Actions")!.Elements());
        Assert.Equal("Exec", exec.Name.LocalName);
        Assert.Equal(ProgramPath, (string?)exec.Element(Ns + "Command"));
        Assert.Equal($"--run-job {JobId:D}", (string?)exec.Element(Ns + "Arguments"));
        Assert.Equal(@"C:\Apps\Game Save Manager", (string?)exec.Element(Ns + "WorkingDirectory"));

        XElement settings = task.Element(Ns + "Settings")!;
        Assert.Equal("IgnoreNew", (string?)settings.Element(Ns + "MultipleInstancesPolicy"));
        Assert.Equal("true", (string?)settings.Element(Ns + "StartWhenAvailable"));
        Assert.Equal("false", (string?)settings.Element(Ns + "DisallowStartIfOnBatteries"));
        Assert.Equal("false", (string?)settings.Element(Ns + "StopIfGoingOnBatteries"));

        // The game name is data, escaped by the XML writer.
        Assert.Contains("Tom & Jerry <Deluxe>", (string?)task.Element(Ns + "RegistrationInfo")!.Element(Ns + "Description"));
    }

    [Theory]
    [InlineData(8, 0, "2026-10-09T20:00:00")]   // later today
    [InlineData(20, 0, "2026-10-10T20:00:00")]  // exactly now: never a missed run
    [InlineData(21, 30, "2026-10-10T20:00:00")] // already passed today
    public void ADailyTrigger_FirstRunsAtTheNextOccurrence_SoRegisteringIsNeverAMissedRun(
        int nowHour, int nowMinute, string expectedStart)
    {
        XElement task = XElement.Parse(WindowsBackupTaskScheduler.RenderTaskXml(
            Definition(DailyAtEight), UserSid, new DateTime(2026, 10, 9, nowHour, nowMinute, 0)));

        XElement trigger = Assert.Single(task.Element(Ns + "Triggers")!.Elements());
        Assert.Equal("CalendarTrigger", trigger.Name.LocalName);
        Assert.Equal(expectedStart, (string?)trigger.Element(Ns + "StartBoundary"));
        Assert.Equal(1, (int?)trigger.Element(Ns + "ScheduleByDay")!.Element(Ns + "DaysInterval"));
        Assert.Null(trigger.Element(Ns + "Repetition"));
    }

    [Fact]
    public void AWeeklyTrigger_RunsOnThePickedDayEveryWeek()
    {
        XElement task = XElement.Parse(WindowsBackupTaskScheduler.RenderTaskXml(
            Definition(new BackupSchedule(BackupScheduleFrequency.Weekly, new TimeOnly(6, 30), DayOfWeek.Saturday)),
            UserSid,
            new DateTime(2026, 10, 9, 8, 0, 0)));

        XElement byWeek = task.Element(Ns + "Triggers")!.Element(Ns + "CalendarTrigger")!.Element(Ns + "ScheduleByWeek")!;
        Assert.Equal("Saturday", Assert.Single(byWeek.Element(Ns + "DaysOfWeek")!.Elements()).Name.LocalName);
        Assert.Equal(1, (int?)byWeek.Element(Ns + "WeeksInterval"));
    }

    [Theory]
    [InlineData(BackupScheduleFrequency.Daily, DayOfWeek.Monday)]
    [InlineData(BackupScheduleFrequency.Weekly, DayOfWeek.Sunday)]
    public void ReadingATaskBack_ReturnsWhatWasRegistered(BackupScheduleFrequency frequency, DayOfWeek day)
    {
        var schedule = new BackupSchedule(frequency, new TimeOnly(4, 15), day);

        var (userId, program, arguments, readSchedule) = WindowsBackupTaskScheduler.ReadTaskXml(
            WindowsBackupTaskScheduler.RenderTaskXml(Definition(schedule), UserSid, new DateTime(2026, 10, 9, 8, 0, 0)));

        Assert.Equal(UserSid, userId);
        Assert.Equal(ProgramPath, program);
        Assert.Equal($"--run-job {JobId:D}", arguments);
        Assert.Equal(schedule, readSchedule);
    }

    // What Windows hands back: a declaration, defaults dropped, reordered.
    private const string AsWindowsReturnsIt = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1000-2000-3000-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
            </Principal>
          </Principals>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-10-09T04:17:00</StartBoundary>
              <ScheduleByWeek>
                <WeeksInterval>1</WeeksInterval>
                <DaysOfWeek>
                  <Sunday />
                </DaysOfWeek>
              </ScheduleByWeek>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"C:\Apps\Game Save Manager\GameSaves.App.exe"</Command>
              <Arguments> --run-job 2b9a4c1e-7d55-4f0e-9a51-0c6f3e8d2a10 </Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void ReadingATask_AsWindowsReturnsIt_TrimsQuotesAndSpaces()
    {
        var (userId, program, arguments, schedule) = WindowsBackupTaskScheduler.ReadTaskXml(AsWindowsReturnsIt);

        Assert.Equal(UserSid, userId);
        Assert.Equal(ProgramPath, program);
        Assert.Equal($"--run-job {JobId:D}", arguments);
        Assert.Equal(new BackupSchedule(BackupScheduleFrequency.Weekly, new TimeOnly(4, 17), DayOfWeek.Sunday), schedule);
    }

    [Theory]
    [InlineData("<Sunday />", "<Sunday /><Monday />")]                                                   // two days
    [InlineData("<WeeksInterval>1</WeeksInterval>", "<WeeksInterval>2</WeeksInterval>")]                    // every other week
    [InlineData("<StartBoundary>", "<Repetition><Interval>PT1H</Interval></Repetition><StartBoundary>")]  // repeats hourly
    [InlineData("</CalendarTrigger>", "</CalendarTrigger><LogonTrigger />")]                               // a second trigger
    [InlineData("ScheduleByWeek", "ScheduleByMonthDayOfWeek")]                                            // monthly, not offered
    public void ASchedule_EditedInTaskScheduler_ReadsAsCustom(string original, string edited)
    {
        string xml = AsWindowsReturnsIt.Replace(original, edited);
        Assert.NotEqual(AsWindowsReturnsIt, xml);

        Assert.Null(WindowsBackupTaskScheduler.ReadTaskXml(xml).Schedule);
    }

    [Theory]
    [InlineData(null, "Not run yet")]
    [InlineData(0x41303, "Not run yet")]
    [InlineData(0, "Completed")]
    [InlineData(1, "Failed: the command line was malformed")]
    [InlineData(2, "Refused before anything was written")]
    [InlineData(3, "Skipped: the previous run was still in progress")]
    [InlineData(4, "Failed: nothing that existed was changed")]
    [InlineData(0x41301, "Running")]
    [InlineData(unchecked((int)0x800710E0), "Windows reported 0x800710E0")]
    public void TheLastResult_IsDescribedInWords(int? result, string expected) =>
        Assert.Equal(expected, WindowsBackupTaskScheduler.DescribeLastResult(result));

    [Fact]
    public void ASchedule_DescribesItself()
    {
        Assert.Equal("Daily at 20:00", DailyAtEight.ToString());
        Assert.Equal(
            "Every Friday at 07:05",
            new BackupSchedule(BackupScheduleFrequency.Weekly, new TimeOnly(7, 5), DayOfWeek.Friday).ToString());
    }
}
