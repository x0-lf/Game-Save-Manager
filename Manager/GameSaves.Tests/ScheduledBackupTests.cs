using GameSaves.Core.Platform;
using GameSaves.Core.Profiles;
using GameSaves.Core.Steam;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.OneDrive;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

/// <summary>
/// BACKUP-003: the unattended backup foundation. Everything runs offline
/// against temporary folders and fakes: no Steam install, browser or network.
/// </summary>
public sealed class ScheduledBackupTests : IDisposable
{
    private const string AppId = "1234";
    private const string AccountId = "111";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 3, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _temp = new();
    private readonly RecordingHistoryRepository _history = new();
    private readonly InMemoryScheduledBackupJobRepository _jobs = new();

    public void Dispose() => _temp.Dispose();

    private string DatabasePath => _temp.GetPath("gamesave.db");

    private string Destination => _temp.GetPath("backups");

    private string UserData => _temp.GetPath("steam", "userdata", AccountId);

    // ---- the runner ----

    [Fact]
    public async Task ARun_AddsANewBackupRun_LeavesExistingFilesAlone_AndRecordsIt()
    {
        Save();
        string older = ExistingBackup();
        ScheduledBackupJob job = AddJob();
        ScheduledBackupRunner runner = Runner();

        ScheduledBackupOutcome outcome = await runner.RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Completed, outcome);
        Assert.Equal("keep me", File.ReadAllText(older));

        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledBackup, run.Kind);
        Assert.Equal(1, run.FilesBackedUp);
        Assert.Null(run.BlockedReason);
        Assert.True(File.Exists(run.BackupRootPath));

        Assert.Equal("Completed: Backed up 1 file(s).", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.False(File.Exists(runner.LockPath(job.Id)));
    }

    [Fact]
    public async Task ARemovedJob_IsRefused_AndTheRefusalIsRecorded()
    {
        ScheduledBackupOutcome outcome = await Runner().RunAsync(Guid.NewGuid());

        Assert.Equal(ScheduledBackupOutcome.Refused, outcome);
        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledBackup, run.Kind);
        Assert.Contains("no longer exists", run.BlockedReason);
    }

    [Theory]
    [InlineData("destination", "destination folder is not available")]
    [InlineData("game", "no longer installed")]
    [InlineData("profile", "was not found")]
    [InlineData("saves", "nothing to back up")]
    public async Task AJobThatNoLongerMatchesTheMachine_IsRefused_WithoutWritingAnything(
        string missing,
        string reason)
    {
        if (missing != "saves")
            Save();

        string older = ExistingBackup();
        string unplugged = _temp.GetPath("unplugged");
        ScheduledBackupJob job = AddJob(missing == "destination" ? unplugged : Destination);

        ScheduledBackupOutcome outcome = await Runner(
            games: missing == "game" ? [] : null,
            profiles: missing == "profile" ? [] : null).RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Refused, outcome);
        Assert.Contains(reason, Assert.Single(_history.Records).BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Refused:", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.Equal(new[] { older }, Directory.GetFileSystemEntries(Destination));
        Assert.False(Directory.Exists(unplugged));
    }

    [Fact]
    public async Task ASecondRunOfTheSameJob_IsSkipped_WhileTheFirstStillHoldsTheLock()
    {
        Save();
        ExistingBackup();
        ScheduledBackupJob job = AddJob();
        ScheduledBackupRunner runner = Runner();

        using (new FileStream(runner.LockPath(job.Id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ScheduledBackupOutcome.AlreadyRunning, await runner.RunAsync(job.Id));
        }

        Assert.Contains("still in progress", Assert.Single(_history.Records).BlockedReason);
        Assert.StartsWith("Skipped:", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.Single(Directory.GetFileSystemEntries(Destination));

        // A lock file left behind without a holder does not block the next run.
        Assert.Equal(ScheduledBackupOutcome.Completed, await runner.RunAsync(job.Id));
    }

    [Fact]
    public async Task ABackupThatStopsPartWay_IsRecordedAsFailed_AndDeletesNothing()
    {
        string save = Save();
        string older = ExistingBackup();
        ScheduledBackupJob job = AddJob();

        ScheduledBackupOutcome outcome = await Runner(wrap: inner => new StopsDuringExecution(inner))
            .RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Failed, outcome);
        Assert.Contains("not enough space", Assert.Single(_history.Records).BlockedReason);
        Assert.StartsWith("Failed:", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.Equal(new[] { older }, Directory.GetFileSystemEntries(Destination));
        Assert.Equal("keep me", File.ReadAllText(older));
        Assert.Equal("slot one", File.ReadAllText(save));
    }

    // ---- the job store ----

    [Fact]
    public void TheJobStore_RoundTripsAJob_ItsLastOutcome_AndItsRemoval()
    {
        var store = new SqliteScheduledBackupJobRepository(MigratedDatabase.Create(_temp, "jobs.db"));
        ScheduledBackupJob job = NewJob(Destination);

        store.Add(job);
        Assert.Equal(job, Assert.Single(store.GetAll()));

        store.RecordOutcome(job.Id, Now.AddHours(1), "Completed: Backed up 1 file(s).");
        Assert.Equal(
            job with { LastRunUtc = Now.AddHours(1), LastOutcome = "Completed: Backed up 1 file(s)." },
            Assert.Single(store.GetAll()));

        store.Delete(job.Id);
        Assert.Empty(store.GetAll());
    }

    // ---- the headless host ----

    [Fact]
    public void OnlyTheRunJobSwitch_SkipsTheWindow()
    {
        Assert.Null(GameSaves.App.Program.RunScheduledJob([]));
        Assert.Null(GameSaves.App.Program.RunScheduledJob(["--some-window-option"]));
        Assert.Equal(1, GameSaves.App.Program.RunScheduledJob(["--run-job"]));
        Assert.Equal(1, GameSaves.App.Program.RunScheduledJob(["--run-job", "not-a-job-id"]));
    }

    [Fact]
    public void TheHeadlessCommand_ComposesWithoutAWindow_GuardsSignIn_AndRecordsInTheDatabase()
    {
        string database = MigratedDatabase.Create(_temp, "app.db");
        bool signInGuarded = false;

        int? exitCode = GameSaves.App.Program.RunScheduledJob(
            ["--run-job", Guid.NewGuid().ToString()],
            services =>
            {
                signInGuarded = services.Any(d => d.ServiceType == typeof(IOneDriveInteractiveAuthorizer));
                services.AddSingleton<IAppDatabasePathProvider>(new TestDatabasePathProvider(database));
            });

        Assert.Equal((int)ScheduledBackupOutcome.Refused, exitCode);
        Assert.True(signInGuarded);
        Assert.Equal(1, MigratedDatabase.Scalar(
            database,
            "SELECT COUNT(*) FROM transfer_runs WHERE kind = 'ScheduledBackup';"));
    }

    [Fact]
    public async Task TheUnattendedGuards_RefuseEverySignInThatWouldOpenABrowser()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddUnattendedSignInGuards()
            .BuildServiceProvider();

        GoogleAuthorizationException google = await Assert.ThrowsAsync<GoogleAuthorizationException>(() =>
            services.GetRequiredService<IGoogleInstalledAppAuthorizer>()
                .ConnectAsync(null!, Guid.NewGuid(), null!, [], CancellationToken.None));
        Assert.Equal(GoogleAuthorizationFailure.BrowserFailed, google.Failure);

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            services.GetRequiredService<IOneDriveInteractiveAuthorizer>()
                .AuthorizeAsync("https://login.invalid/", "http://127.0.0.1:1/", "state"));
    }

    // ---- helpers ----

    private static SteamGame Game() =>
        new(AppId, "Test Game", "TestGame", "", "", "", true, SteamDiscoveryConfidence.High);

    private SteamProfile Profile() => new(AccountId, null, "Player", UserData, 1, true);

    private string Save()
    {
        string path = Path.Combine(UserData, AppId, "remote", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "slot one");
        return path;
    }

    // Something the user already keeps in the destination: no run may touch it.
    private string ExistingBackup()
    {
        Directory.CreateDirectory(Destination);
        string path = Path.Combine(Destination, "older-backup.txt");
        File.WriteAllText(path, "keep me");
        return path;
    }

    private static ScheduledBackupJob NewJob(string destination) =>
        new(Guid.NewGuid(), AppId, "Test Game", AccountId, destination,
            IncludeSteamUserDataGameFolder: true,
            IncludeApprovedMappings: false,
            CreatedUtc: Now);

    private ScheduledBackupJob AddJob(string? destination = null)
    {
        ScheduledBackupJob job = NewJob(destination ?? Destination);
        _jobs.Add(job);
        return job;
    }

    private ScheduledBackupRunner Runner(
        IReadOnlyList<SteamGame>? games = null,
        IReadOnlyList<SteamProfile>? profiles = null,
        Func<IManualBackupService, IManualBackupService>? wrap = null)
    {
        var backups = new ManualBackupService(
            new EmptySteamDiscoveryService(),
            new EmptyMappingRepository(),
            new WindowsPlatformProvider(),
            new TransferOverwriteBackupService(
                new TestDatabasePathProvider(DatabasePath),
                new BackupStoragePreference(() => BackupContainerFormat.Zip)),
            _history);

        return new ScheduledBackupRunner(
            _jobs,
            new FixedSteamDiscovery(games ?? [Game()]),
            new FixedProfileDetector(profiles ?? [Profile()]),
            wrap is null ? backups : wrap(backups),
            _history,
            new TestDatabasePathProvider(DatabasePath),
            new FixedUtcClock(Now));
    }

    private sealed class FixedSteamDiscovery(IReadOnlyList<SteamGame> games) : ISteamDiscoveryService
    {
        public SteamDiscoveryResult Discover(
            SteamDiscoveryOptions? options = null,
            IProgress<SteamFallbackScanProgress>? fallbackProgress = null,
            CancellationToken cancellationToken = default)
        {
            // An unattended run never falls back to scanning the disks.
            Assert.Equal(SteamFallbackScanMode.Never, options?.FallbackScanMode);

            var result = new SteamDiscoveryResult();
            result.Games.AddRange(games);
            return result;
        }
    }

    private sealed class FixedProfileDetector(IReadOnlyList<SteamProfile> profiles) : ISteamProfileDetector
    {
        public IReadOnlyList<SteamProfile> DetectProfiles(
            SteamDiscoveryResult discovery,
            CancellationToken cancellationToken = default) => profiles;

        public IReadOnlyList<SteamProfile> DetectProfiles(
            string steamRoot,
            CancellationToken cancellationToken = default) => profiles;
    }

    // The preview passes, then the disk fills up while the run is written.
    private sealed class StopsDuringExecution(IManualBackupService inner) : IManualBackupService
    {
        public Task<ManualBackupPlan> CreatePreviewAsync(
            SteamGame game,
            SteamProfile profile,
            string destinationRoot,
            ManualBackupOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.CreatePreviewAsync(game, profile, destinationRoot, options, cancellationToken);

        public Task<ManualBackupResult> ExecuteAsync(
            ManualBackupPlan plan,
            ManualBackupExecuteOptions options,
            CancellationToken cancellationToken = default) =>
            throw new IOException("There is not enough space on the disk.");
    }
}
