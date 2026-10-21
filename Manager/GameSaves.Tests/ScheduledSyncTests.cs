using GameSaves.Core.Platform;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.OneDrive;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

/// <summary>
/// BACKUP-008: the unattended sync runner, its store and the headless host.
/// Everything runs offline against temporary folders and fakes: the local
/// folder provider is the real engine over a temporary remote; cloud
/// providers are scripted. No test registers a Windows task.
/// </summary>
public sealed class ScheduledSyncTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _temp = new();
    private readonly RecordingHistoryRepository _history = new();
    private readonly InMemoryScheduledSyncJobRepository _jobs = new();
    private readonly InMemorySyncRemoteProfileRepository _profiles = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly ScriptedSyncProvider _scripted = new();
    private readonly BackupHistoryService _backups;

    public ScheduledSyncTests()
    {
        _backups = new BackupHistoryService(new TestDatabasePathProvider(DatabasePath));
        Directory.CreateDirectory(_backups.GetBackupBasePath());
        Directory.CreateDirectory(Remote);
    }

    public void Dispose() => _temp.Dispose();

    private string DatabasePath => _temp.GetPath("app", "gamesave.db");

    private string Remote => _temp.GetPath("remote");

    // ---- the runner, through the real engine on a local folder ----

    [Fact]
    public async Task ARun_CopiesTheLocalOnlyRun_ToTheRemote_AndRecordsOneScheduledSyncRow()
    {
        LocalRun("run-one", "local payload");
        ScheduledSyncJob job = AddJob(LocalProfile());
        ScheduledSyncRunner runner = Runner();

        ScheduledBackupOutcome outcome = await runner.RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Completed, outcome);
        Assert.True(File.Exists(Path.Combine(Remote, "run-one", "manifest.json")));

        // The engine records the executed run; the runner adds no second row.
        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledSync, run.Kind);
        Assert.Equal("Home NAS", run.GameName);
        Assert.Equal(1, run.FilesCopied);
        Assert.Null(run.BlockedReason);

        Assert.StartsWith("Completed: Uploaded 1 run(s), downloaded 0 run(s)", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.False(File.Exists(runner.LockPath(job.ProfileId)));
    }

    [Fact]
    public async Task ADownloadOnlyRun_CopiesTheRemoteOnlyRun_AndSendsNothing()
    {
        LocalRun("local-only", "stays here");
        RemoteRun("remote-only", "comes down");
        ScheduledSyncJob job = AddJob(LocalProfile(), upload: false, download: true);

        ScheduledBackupOutcome outcome = await Runner().RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Completed, outcome);
        Assert.True(File.Exists(Path.Combine(_backups.GetBackupBasePath(), "remote-only", "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(Remote, "local-only")));
        Assert.StartsWith("Completed: Uploaded 0 run(s), downloaded 1 run(s)", Assert.Single(_jobs.Jobs).LastOutcome);
    }

    [Fact]
    public async Task AConflict_IsSkipped_BothSidesKeepTheirContent_AndTheRunCompletesWithNothingCopied()
    {
        string local = LocalRun("same-name", "local version");
        string remote = RemoteRun("same-name", "remote version");
        ScheduledSyncJob job = AddJob(LocalProfile());

        ScheduledBackupOutcome outcome = await Runner().RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Completed, outcome);
        Assert.Equal("local version", File.ReadAllText(local));
        Assert.Equal("remote version", File.ReadAllText(remote));
        Assert.Equal("Completed: Nothing to copy: 0 run(s) in sync, 1 conflict(s) skipped.", Assert.Single(_jobs.Jobs).LastOutcome);

        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledSync, run.Kind);
        Assert.Null(run.BlockedReason);
        Assert.Equal(1, run.FilesSkipped);
    }

    // ---- refusals: nothing is copied, every one is recorded ----

    [Fact]
    public async Task ARemovedJob_IsRefused_AndTheRefusalIsRecorded()
    {
        ScheduledBackupOutcome outcome = await Runner().RunAsync(Guid.NewGuid());

        Assert.Equal(ScheduledBackupOutcome.Refused, outcome);
        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledSync, run.Kind);
        Assert.Contains("no longer exists", run.BlockedReason);
    }

    [Theory]
    [InlineData("profile", "no longer exists")]
    [InlineData("backup base", "local backup folder is not available")]
    [InlineData("sftp", "never stored")]
    [InlineData("mega", "not available")]
    [InlineData("token", "No stored sign-in")]
    [InlineData("unavailable", "The provider is unavailable: Sign in again")]
    [InlineData("rate limited", "rate limiting")]
    [InlineData("quota", "no free storage space")]
    [InlineData("plan errors", "The remote folder is read-only")]
    [InlineData("unreadable", "The remote could not be read: Timed out")]
    public async Task ACheckThatFails_RefusesTheRun_BeforeAnythingIsCopied(string failing, string reason)
    {
        LocalRun("run-one", "never sent");
        SyncRemoteProfile profile = failing switch
        {
            "sftp" => Profile(SyncProviderKind.Sftp, new SftpSyncRemoteSettings("nas", 22, "me", SftpAuthMethod.Password, null, "/backups")),
            "mega" => Profile(SyncProviderKind.Mega, null),
            "profile" or "backup base" => LocalProfile(),
            _ => Profile(SyncProviderKind.GoogleDrive, new GoogleDriveSyncRemoteSettings("me@example.com", GoogleDriveAuthorizationScopes.DriveFile))
        };
        ScheduledSyncJob job = AddJob(profile);

        if (failing == "profile")
            _profiles.Delete(profile.Id);
        if (failing == "backup base")
            Directory.Delete(_backups.GetBackupBasePath(), recursive: true);
        if (failing is not "token" and not "sftp" and not "mega" and not "profile" and not "backup base")
            await _secrets.StoreAsync(new SecretKey(profile.Id, SecretNames.OAuthTokenData), new byte[] { 1 });

        _scripted.Health = failing switch
        {
            "unavailable" => new ProviderHealthReport(ProviderHealthState.Unavailable, "Sign in again."),
            "rate limited" => new ProviderHealthReport(ProviderHealthState.RateLimited, "Slow down."),
            "quota" => new ProviderHealthReport(ProviderHealthState.QuotaExhausted, "Full."),
            _ => _scripted.Health
        };
        if (failing == "plan errors")
            _scripted.Plan = _scripted.Plan with
            {
                Warnings = [new TransferPreviewWarning("ReadOnly", "The remote folder is read-only.", TransferWarningSeverity.Error)],
                CanExecute = false
            };
        if (failing == "unreadable")
            _scripted.PreviewFailure = new TimeoutException("Timed out.");

        ScheduledBackupOutcome outcome = await Runner().RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Refused, outcome);
        TransferRunRecord run = Assert.Single(_history.Records);
        Assert.Equal(TransferRunKind.ScheduledSync, run.Kind);
        Assert.Contains(reason, run.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Refused:", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.Empty(_scripted.ExecuteOptions);
        Assert.Empty(Directory.GetFileSystemEntries(Remote));
    }

    [Fact]
    public async Task ASecondRunForTheSameProfile_IsSkipped_WhileTheFirstHoldsTheLock_WhateverJobStartedIt()
    {
        LocalRun("run-one", "payload");
        SyncRemoteProfile profile = LocalProfile();
        ScheduledSyncJob job = AddJob(profile);
        // A second job for the same profile (an older database, say) shares the lock.
        var twin = job with { Id = Guid.NewGuid() };
        _jobs.Jobs.Add(twin);
        ScheduledSyncRunner runner = Runner();

        using (new FileStream(runner.LockPath(profile.Id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(ScheduledBackupOutcome.AlreadyRunning, await runner.RunAsync(job.Id));
            Assert.Equal(ScheduledBackupOutcome.AlreadyRunning, await runner.RunAsync(twin.Id));
        }

        Assert.All(_history.Records, run => Assert.Contains("still in progress", run.BlockedReason));
        Assert.All(_jobs.Jobs, job => Assert.StartsWith("Skipped:", job.LastOutcome));
        Assert.Empty(Directory.GetFileSystemEntries(Remote));

        // A lock file left behind without a holder does not block the next run.
        Assert.Equal(ScheduledBackupOutcome.Completed, await runner.RunAsync(job.Id));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("throws")]
    [InlineData("cancelled")]
    public async Task ARunThatStopsPartWay_IsRecordedAsFailed_NeverAsSuccess(string how)
    {
        SyncRemoteProfile profile = Profile(SyncProviderKind.WebDav, null);
        await _secrets.StoreAsync(new SecretKey(profile.Id, SecretNames.WebDavPassword), new byte[] { 1 });
        ScheduledSyncJob job = AddJob(profile);

        SyncItem item = _scripted.Plan.Items[0];
        _scripted.Result = new SyncResult(_scripted.Plan, false, 0, 0, 0, 10,
            [new SyncItemResult(item, 10, SyncItemStatus.Incomplete, "Connection dropped.")], []);
        _scripted.ExecuteFailure = how switch
        {
            "throws" => new IOException("Disk full."),
            "cancelled" => new OperationCanceledException(),
            _ => null
        };

        ScheduledBackupOutcome outcome = await Runner().RunAsync(job.Id);

        Assert.Equal(ScheduledBackupOutcome.Failed, outcome);
        Assert.StartsWith("Failed:", Assert.Single(_jobs.Jobs).LastOutcome);
        Assert.Single(_scripted.ExecuteOptions);

        if (how != "incomplete")
            Assert.Contains("The run stopped", Assert.Single(_history.Records).BlockedReason);
    }

    [Fact]
    public async Task AnExecutedRun_AsksTheEngineForTheJobsDirection_ConfirmedAndRecordedAsScheduled()
    {
        SyncRemoteProfile profile = Profile(SyncProviderKind.OneDrive, null);
        await _secrets.StoreAsync(new SecretKey(profile.Id, SecretNames.OneDriveTokenData), new byte[] { 1 });
        ScheduledSyncJob job = AddJob(profile, upload: true, download: false, archive: true);

        Assert.Equal(ScheduledBackupOutcome.Completed, await Runner().RunAsync(job.Id));

        SyncOptions preview = Assert.Single(_scripted.PreviewOptions);
        Assert.True(preview.DryRun);
        Assert.True(preview.Upload);
        Assert.False(preview.Download);
        Assert.True(preview.ArchiveSync);

        SyncOptions executed = Assert.Single(_scripted.ExecuteOptions);
        Assert.False(executed.DryRun);
        Assert.True(executed.ConfirmExecution);
        Assert.True(executed.Upload);
        Assert.False(executed.Download);
        Assert.True(executed.ArchiveSync);
        Assert.Equal(TransferRunKind.ScheduledSync, executed.HistoryKind);
        Assert.Equal("Home NAS", executed.HistoryLabel);
    }

    [Theory]
    [InlineData(SyncProviderKind.LocalFolder, null)]
    [InlineData(SyncProviderKind.GoogleDrive, null)]
    [InlineData(SyncProviderKind.OneDrive, null)]
    [InlineData(SyncProviderKind.WebDav, null)]
    [InlineData(SyncProviderKind.Sftp, "never stored")]
    [InlineData(SyncProviderKind.Mega, "not available")]
    public void OnlyProvidersWithStoredSignIns_CanRunUnattended(SyncProviderKind kind, string? refusal)
    {
        string? actual = ScheduledSyncJob.UnattendedRefusal(kind);

        if (refusal is null)
            Assert.Null(actual);
        else
            Assert.Contains(refusal, actual);
    }

    // ---- the job store ----

    [Fact]
    public void TheJobStore_RoundTripsAJob_ItsLastOutcome_OnePerProfile_AndItsRemoval()
    {
        var store = new SqliteScheduledSyncJobRepository(MigratedDatabase.Create(_temp, "jobs.db"));
        var job = new ScheduledSyncJob(Guid.NewGuid(), Guid.NewGuid(), "Home NAS", true, false, true, Now);

        store.Add(job);
        Assert.Equal(job, Assert.Single(store.GetAll()));

        Assert.Throws<SqliteException>(() => store.Add(job with { Id = Guid.NewGuid() }));

        store.RecordOutcome(job.Id, Now.AddHours(1), "Completed: Uploaded 1 run(s).");
        Assert.Equal(
            job with { LastRunUtc = Now.AddHours(1), LastOutcome = "Completed: Uploaded 1 run(s)." },
            Assert.Single(store.GetAll()));

        store.Delete(job.Id);
        Assert.Empty(store.GetAll());
    }

    // ---- the headless host ----

    [Fact]
    public void TheRunSyncSwitch_SkipsTheWindow_AndRejectsAMalformedCommandLine()
    {
        Assert.Equal(1, GameSaves.App.Program.RunScheduledJob(["--run-sync"]));
        Assert.Equal(1, GameSaves.App.Program.RunScheduledJob(["--run-sync", "not-a-job-id"]));
        Assert.Equal(1, GameSaves.App.Program.RunScheduledJob(["--run-sync", Guid.NewGuid().ToString(), "extra"]));
        Assert.Equal($"--run-sync {Guid.Empty:D}", GameSaves.App.Program.RunSyncArguments(Guid.Empty));
    }

    [Fact]
    public void TheHeadlessSyncCommand_ComposesWithoutAWindow_GuardsSignIn_AndRecordsInTheDatabase()
    {
        string database = MigratedDatabase.Create(_temp, "host.db");
        bool signInGuarded = false;

        int? exitCode = GameSaves.App.Program.RunScheduledJob(
            ["--run-sync", Guid.NewGuid().ToString()],
            services =>
            {
                signInGuarded = services.Any(d => d.ServiceType == typeof(IOneDriveInteractiveAuthorizer));
                services.AddSingleton<IAppDatabasePathProvider>(new TestDatabasePathProvider(database));
            });

        Assert.Equal((int)ScheduledBackupOutcome.Refused, exitCode);
        Assert.True(signInGuarded);
        Assert.Equal(1, MigratedDatabase.Scalar(
            database,
            "SELECT COUNT(*) FROM transfer_runs WHERE kind = 'ScheduledSync';"));
    }

    // ---- helpers ----

    private string LocalRun(string name, string content) =>
        Run(Path.Combine(_backups.GetBackupBasePath(), name), content, Now);

    private string RemoteRun(string name, string content) =>
        Run(Path.Combine(Remote, name), content, Now.AddMinutes(1));

    // Returns the payload file, so a test can check it was left alone.
    private string Run(string root, string content, DateTimeOffset started)
    {
        TestData.CreateBackupRun(root, _temp.GetPath("saves", "original.sav"), content, started);
        return Path.Combine(root, "files", "payload.sav");
    }

    private SyncRemoteProfile LocalProfile() =>
        Profile(SyncProviderKind.LocalFolder, new LocalFolderSyncRemoteSettings(Remote));

    private SyncRemoteProfile Profile(SyncProviderKind kind, SyncRemoteProfileSettings? settings) =>
        _profiles.Create(new SyncRemoteProfile(
            Guid.NewGuid(), "Home NAS", kind, null, null, settings, Now, Now, null, null, null));

    private ScheduledSyncJob AddJob(
        SyncRemoteProfile profile,
        bool upload = true,
        bool download = true,
        bool archive = false)
    {
        var job = new ScheduledSyncJob(Guid.NewGuid(), profile.Id, profile.DisplayName, upload, download, archive, Now);
        _jobs.Add(job);
        return job;
    }

    private ScheduledSyncRunner Runner() => new(
        _jobs,
        _profiles,
        _secrets,
        new LocalOrScriptedFactory(_backups, _history, _scripted),
        _backups,
        _history,
        new TestDatabasePathProvider(DatabasePath),
        new FixedUtcClock(Now));

    /// <summary>
    /// The real engine over a temporary folder for Local folder profiles, so
    /// create-only copying and conflicts are the engine's own; the scripted
    /// provider for every cloud kind, so no network is ever reached.
    /// </summary>
    private sealed class LocalOrScriptedFactory(
        IBackupHistoryService backups,
        ITransferHistoryRepository history,
        ScriptedSyncProvider scripted) : ISyncProviderFactory
    {
        public ISyncProvider CreateLocalFolderProvider(string remoteRoot) =>
            new EngineSyncProvider(
                "Local folder",
                remoteRoot,
                new LocalFolderRemoteFileSystem(remoteRoot, backups.GetBackupBasePath()),
                backups,
                history);

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) =>
            throw new InvalidOperationException("An unattended run must never build an SFTP provider.");

        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) => scripted;

        public ISyncProvider CreateOneDriveProvider(Guid remoteProfileId) => scripted;

        public ISyncProvider CreateWebDavProvider(Guid remoteProfileId) => scripted;

        public void ForgetSftpHostKey(string host, int port)
        {
        }
    }

    private sealed class ScriptedSyncProvider : ISyncProvider
    {
        private static readonly SyncItem Item = new(
            "run-a", SyncItemAction.UploadToRemote, true, false,
            @"C:\backups\run-a", "remote/run-a", "Test Game", 1, 10, "Copy to remote");

        public string ProviderName => "Scripted";

        public string RemoteRoot => "scripted://remote";

        public ProviderHealthReport Health { get; set; } = new(ProviderHealthState.Healthy, "Ready.");

        public SyncPlan Plan { get; set; } = new(
            "Scripted", "scripted://remote", [Item], [], CanExecute: true, 1, 0, 0, 0, 10, 0);

        public SyncResult Result { get; set; } = new(
            new SyncPlan("Scripted", "scripted://remote", [Item], [], true, 1, 0, 0, 0, 10, 0),
            false, 1, 0, 0, 10, [new SyncItemResult(Item, 10, SyncItemStatus.Uploaded, null)], []);

        public Exception? PreviewFailure { get; set; }

        public Exception? ExecuteFailure { get; set; }

        public List<SyncOptions> PreviewOptions { get; } = new();

        public List<SyncOptions> ExecuteOptions { get; } = new();

        public Task<ProviderHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

        public Task<SyncPlan> CreatePreviewAsync(SyncOptions options, CancellationToken cancellationToken = default)
        {
            PreviewOptions.Add(options);
            return PreviewFailure is null ? Task.FromResult(Plan) : Task.FromException<SyncPlan>(PreviewFailure);
        }

        public Task<SyncResult> ExecuteAsync(SyncPlan plan, SyncOptions options, CancellationToken cancellationToken = default)
        {
            ExecuteOptions.Add(options);
            return ExecuteFailure is null ? Task.FromResult(Result) : Task.FromException<SyncResult>(ExecuteFailure);
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>([]);

        public void Dispose()
        {
        }
    }
}
