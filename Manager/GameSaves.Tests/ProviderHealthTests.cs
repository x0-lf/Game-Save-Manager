using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// SYNC-004. One read-only health check per provider: validation decides
/// whether the remote is usable, capacity is reported only when the backend
/// read it, throttling and a full remote are told apart from a failure, and
/// the Sync page checks every saved profile on request, one at a time.
/// WebDAV and OneDrive capacity are covered with their providers' own tests.
/// </summary>
public sealed class ProviderHealthTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    // ---- the engine provider's check ----

    [Fact]
    public async Task Check_PassesTheReportedCapacityThrough_AndKeepsANonBlockingWarning()
    {
        var remote = new HealthRemote
        {
            Warning = new TransferPreviewWarning("RootMoved", "The folder was moved.", TransferWarningSeverity.Warning),
            Capacity = new RemoteCapacity(FreeBytes: 40, TotalBytes: 100, UsedBytes: 60)
        };

        ProviderHealthReport report = await Provider(remote).CheckHealthAsync();

        Assert.Equal(ProviderHealthState.Healthy, report.State);
        Assert.Equal("The folder was moved.", report.Reason);
        Assert.Equal(new RemoteCapacity(40, 100, 60), report.Capacity);
    }

    [Fact]
    public async Task Check_ABlockingValidationAnswer_IsUnavailable_AndReadsNoCapacity()
    {
        var remote = new HealthRemote
        {
            Warning = new TransferPreviewWarning("WebDavAuthenticationFailed", "Wrong password.", TransferWarningSeverity.Error),
            Capacity = new RemoteCapacity(40)
        };

        ProviderHealthReport report = await Provider(remote).CheckHealthAsync();

        Assert.Equal(new ProviderHealthReport(ProviderHealthState.Unavailable, "Wrong password."), report);
        Assert.Equal(0, remote.CapacityReads);
    }

    [Fact]
    public async Task Check_AThrottledValidationAnswer_IsRateLimited()
    {
        var remote = new HealthRemote
        {
            Warning = new TransferPreviewWarning(
                GoogleDriveRemoteValidationErrorCodes.RateLimited,
                "Try again later.",
                TransferWarningSeverity.Error)
        };

        ProviderHealthReport report = await Provider(remote).CheckHealthAsync();

        Assert.Equal(ProviderHealthState.RateLimited, report.State);
        Assert.Null(report.Capacity);
    }

    [Fact]
    public async Task Check_NoFreeSpace_IsQuotaExhausted_WithTheCapacityThatSaysSo()
    {
        var remote = new HealthRemote { Capacity = new RemoteCapacity(0, 100, 100) };

        ProviderHealthReport report = await Provider(remote).CheckHealthAsync();

        Assert.Equal(ProviderHealthState.QuotaExhausted, report.State);
        Assert.Equal(new RemoteCapacity(0, 100, 100), report.Capacity);
    }

    [Fact]
    public async Task Check_AFailureIsClassifiedByTheBackend_AndOnlyCancellationEscapes()
    {
        var throttled = new HealthRemote { CapacityFailure = new TimeoutException("slow down") };
        var broken = new HealthRemote { CapacityFailure = new InvalidDataException("bad answer") };

        ProviderHealthReport rateLimited = await Provider(Retrying(throttled)).CheckHealthAsync();
        ProviderHealthReport unavailable = await Provider(Retrying(broken)).CheckHealthAsync();

        Assert.Equal(new ProviderHealthReport(ProviderHealthState.RateLimited, "slow down"), rateLimited);
        Assert.Equal(new ProviderHealthReport(ProviderHealthState.Unavailable, "bad answer"), unavailable);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider(new HealthRemote()).CheckHealthAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Check_WithoutACapacityRead_ReportsNone()
    {
        // The default member: a backend that cannot read space says nothing.
        IRemoteFileSystem plain = new RecordingProviderRemoteFileSystem();

        ProviderHealthReport report = await Provider(plain).CheckHealthAsync();

        Assert.Equal(ProviderHealthState.Healthy, report.State);
        Assert.Null(report.Capacity);
    }

    // ---- local folder ----

    [Fact]
    public async Task LocalFolder_ReportsTheVolume_OfTheFolderOrItsNearestExistingParent()
    {
        using var temp = new TemporaryDirectory();
        var existing = new LocalFolderRemoteFileSystem(temp.Path, temp.GetPath("local-base"));
        var notYetCreated = new LocalFolderRemoteFileSystem(temp.GetPath("sync", "not", "created"), temp.GetPath("local-base"));

        RemoteCapacity? capacity = await existing.GetCapacityAsync();
        RemoteCapacity? parents = await notYetCreated.GetCapacityAsync();

        Assert.NotNull(capacity);
        Assert.NotNull(parents);
        Assert.True(capacity.FreeBytes > 0);
        Assert.True(capacity.TotalBytes >= capacity.FreeBytes);
        Assert.Equal(capacity.TotalBytes, parents.TotalBytes);
        Assert.False(Directory.Exists(temp.GetPath("sync")), "reading space must not create the folder");
    }

    [Fact]
    public async Task LocalFolder_OnADriveThatIsNotThere_IsUnavailable()
    {
        char? missing = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => (char)letter)
            .Cast<char?>()
            .FirstOrDefault(letter => !Directory.Exists($"{letter}:\\"));
        Assert.SkipWhen(!OperatingSystem.IsWindows() || missing is null, "needs a free Windows drive letter");

        using var temp = new TemporaryDirectory();
        IRemoteFileSystem remote = new LocalFolderRemoteFileSystem($@"{missing}:\Backups", temp.GetPath("local-base"));

        ProviderHealthReport report = await Provider(remote).CheckHealthAsync();

        Assert.Equal(ProviderHealthState.Unavailable, report.State);
        Assert.Contains("not available", report.Reason);
    }

    // ---- the Sync page ----

    [Fact]
    public async Task SyncPage_ChecksEverySavedProfileButSftp_InWords_AndIsolatesAFailure()
    {
        var factory = new HealthProviderFactory();
        factory.Reports[SyncProviderKind.LocalFolder] = () =>
            new ProviderHealthReport(ProviderHealthState.Healthy, "Ready.", new RemoteCapacity(2048, 4096));
        factory.Reports[SyncProviderKind.WebDav] = () =>
            new ProviderHealthReport(ProviderHealthState.Healthy, "Ready.");
        factory.Reports[SyncProviderKind.OneDrive] = () =>
            throw new InvalidOperationException("OneDrive profile is gone.");
        factory.Reports[SyncProviderKind.GoogleDrive] = () =>
            new ProviderHealthReport(ProviderHealthState.RateLimited, "Try again later.");
        SyncViewModel viewModel = CreateViewModel(factory);

        Assert.Equal(
            new[] { "Alpha", "Beta", "Delta", "Epsilon" },
            viewModel.MultiTargetDestinations.Select(row => row.DisplayName));
        Assert.All(viewModel.MultiTargetDestinations, row => Assert.Equal("Not checked yet.", row.HealthText));
        Assert.Empty(factory.Created);

        await viewModel.CheckProviderHealthCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { SyncProviderKind.LocalFolder, SyncProviderKind.WebDav, SyncProviderKind.OneDrive, SyncProviderKind.GoogleDrive },
            factory.Created);
        Assert.True(factory.AllDisposed);

        var rows = viewModel.MultiTargetDestinations.ToDictionary(row => row.DisplayName);
        Assert.Equal("✓", rows["Alpha"].HealthGlyph);
        Assert.Equal("Healthy. Ready.", rows["Alpha"].HealthText);
        Assert.Equal("Storage: 2 KB free of 4 KB.", rows["Alpha"].CapacityText);
        Assert.Equal("Storage: not reported by the server on this check.", rows["Beta"].CapacityText);
        Assert.Equal("✕", rows["Delta"].HealthGlyph);
        Assert.Equal("Unavailable. OneDrive profile is gone.", rows["Delta"].HealthText);
        Assert.Equal("", rows["Delta"].CapacityText);
        Assert.Equal("Rate limited. Try again later.", rows["Epsilon"].HealthText);
        Assert.Contains("Alpha", rows["Alpha"].HealthAccessibleName);
        Assert.Contains("Healthy. Ready. Storage: 2 KB free of 4 KB.", rows["Alpha"].HealthAccessibleName);
        Assert.StartsWith("Checked 4 profile(s) at ", viewModel.ProviderHealthStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncPage_SaysWhenAProviderNeverReportsSpace()
    {
        var factory = new HealthProviderFactory();
        factory.Reports[SyncProviderKind.GoogleDrive] = () =>
            new ProviderHealthReport(ProviderHealthState.Healthy, "Ready.");
        SyncViewModel viewModel = CreateViewModel(factory);

        await viewModel.CheckProviderHealthCommand.ExecuteAsync(null);

        Assert.Equal(
            "Storage: this provider does not report free space.",
            viewModel.MultiTargetDestinations.Single(row => row.DisplayName == "Epsilon").CapacityText);
    }

    [Fact]
    public async Task SyncPage_CancellingTheCheck_LeavesTheRestUnchecked()
    {
        var factory = new HealthProviderFactory { Gate = SyncProviderKind.WebDav };
        SyncViewModel viewModel = CreateViewModel(factory);

        Task running = viewModel.CheckProviderHealthCommand.ExecuteAsync(null);
        await factory.GateReached.Task;
        Assert.True(viewModel.CheckProviderHealthCommand.IsRunning);
        viewModel.CheckProviderHealthCancelCommand.Execute(null);
        await running;

        var rows = viewModel.MultiTargetDestinations.ToDictionary(row => row.DisplayName);
        Assert.StartsWith("Healthy.", rows["Alpha"].HealthText, StringComparison.Ordinal);
        Assert.All(
            new[] { "Beta", "Delta", "Epsilon" },
            name => Assert.Equal("Not checked: the check was cancelled.", rows[name].HealthText));
        Assert.Equal(new[] { SyncProviderKind.LocalFolder, SyncProviderKind.WebDav }, factory.Created);
        Assert.Equal("Check cancelled after 1 of 4 profile(s).", viewModel.ProviderHealthStatusMessage);
    }

    [Fact]
    public void SyncView_HasTheHealthPanel_WithNamedRowsAndAStopButton()
    {
        string xaml = CancelSyncTests.ReadSyncView();

        Assert.Contains("PanelKey=\"sync.health\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding HealthAccessibleName}\"", xaml);
        Assert.Contains("Text=\"{Binding HealthText}\"", xaml);
        Assert.Contains("Text=\"{Binding CapacityText}\"", xaml);
        Assert.Contains("Command=\"{Binding CheckProviderHealthCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding CheckProviderHealthCancelCommand}\"", xaml);
    }

    // ---- helpers ----

    private static ISyncProvider Provider(IRemoteFileSystem remote) =>
        new EngineSyncProvider(
            "Test",
            remote.DisplayRoot,
            remote,
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository());

    private static IRemoteFileSystem Retrying(IRemoteFileSystem remote) =>
        new RetryingRemoteFileSystem(
            remote,
            new RecordingDelayProvider(),
            isRetryable: _ => false,
            isRateLimited: exception => exception is TimeoutException);

    private static SyncViewModel CreateViewModel(HealthProviderFactory factory)
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile("Beta", SyncProviderKind.WebDav,
            new WebDavSyncRemoteSettings("https://dav.example.test/", "alice", "Backups")));
        repository.Create(Profile("Alpha", SyncProviderKind.LocalFolder,
            new LocalFolderSyncRemoteSettings(@"D:\SyncA")));
        repository.Create(Profile("Gamma", SyncProviderKind.Sftp,
            new SftpSyncRemoteSettings("host", 22, "alice", SftpAuthMethod.Password, null, "/srv")));
        repository.Create(Profile("Delta", SyncProviderKind.OneDrive,
            new OneDriveSyncRemoteSettings(null, OneDriveAuthorizationScopes.AppFolder)));
        repository.Create(Profile("Epsilon", SyncProviderKind.GoogleDrive,
            new GoogleDriveSyncRemoteSettings(null, GoogleDriveAuthorizationScopes.DriveFile)));

        SyncUiSettings settings = SyncUiSettings.Default;

        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(T0),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());
    }

    private static SyncRemoteProfile Profile(
        string name,
        SyncProviderKind kind,
        SyncRemoteProfileSettings settings) =>
        new(Guid.NewGuid(), name, kind, null, $"{name} root", settings, T0, T0, null, null, null);

    /// <summary>Scripted validation and capacity; every sync member is off limits to a check.</summary>
    private sealed class HealthRemote : IRemoteFileSystem
    {
        public TransferPreviewWarning? Warning { get; init; }

        public RemoteCapacity? Capacity { get; init; }

        public Exception? CapacityFailure { get; init; }

        public int CapacityReads { get; private set; }

        public string DisplayRoot => "health-test";

        public string GetDisplayPath(string relativePath) => relativePath;

        public Task<TransferPreviewWarning?> ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Warning);
        }

        public Task<RemoteCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default)
        {
            CapacityReads++;
            return CapacityFailure is null
                ? Task.FromResult(Capacity)
                : Task.FromException<RemoteCapacity?>(CapacityFailure);
        }

        public Task<bool> RootExistsAsync(CancellationToken cancellationToken = default) => Refuse<bool>();
        public Task<IReadOnlyList<string>> ListRunFolderNamesAsync(CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<string>>();
        public Task<bool> FolderExistsAsync(string relativeFolder, CancellationToken cancellationToken = default) => Refuse<bool>();
        public Task<string?> ReadTextFileAsync(string relativePath, CancellationToken cancellationToken = default) => Refuse<string?>();
        public Task CreateTextFileIfMissingAsync(string relativePath, string content, CancellationToken cancellationToken = default) => Refuse<bool>();
        public Task<string?> ReadProviderMetadataAsync(string relativePath, CancellationToken cancellationToken = default) => Refuse<string?>();
        public Task ReplaceProviderMetadataAsync(string relativePath, string content, CancellationToken cancellationToken = default) => Refuse<bool>();
        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeFolder, CancellationToken cancellationToken = default) => Refuse<IReadOnlyList<string>>();
        public Task<long> UploadFileAsync(string localFilePath, string relativeRemotePath, CancellationToken cancellationToken = default) => Refuse<long>();
        public Task<long> DownloadFileAsync(string relativeRemotePath, string localFilePath, CancellationToken cancellationToken = default) => Refuse<long>();

        private static Task<T> Refuse<T>() =>
            throw new InvalidOperationException("A health check must not touch sync content.");
    }

    private sealed class HealthProviderFactory : ISyncProviderFactory
    {
        private readonly List<HealthProvider> _providers = [];

        public Dictionary<SyncProviderKind, Func<ProviderHealthReport>> Reports { get; } = [];

        public List<SyncProviderKind> Created { get; } = [];

        public SyncProviderKind? Gate { get; init; }

        public TaskCompletionSource GateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool AllDisposed => _providers.All(provider => provider.IsDisposed);

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot) => Create(SyncProviderKind.LocalFolder);

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) => Create(SyncProviderKind.Sftp);

        public ISyncProvider CreateGoogleDriveProvider(Guid remoteProfileId) => Create(SyncProviderKind.GoogleDrive);

        public ISyncProvider CreateOneDriveProvider(Guid remoteProfileId) => Create(SyncProviderKind.OneDrive);

        public ISyncProvider CreateWebDavProvider(Guid remoteProfileId) => Create(SyncProviderKind.WebDav);

        public void ForgetSftpHostKey(string host, int port)
        {
        }

        private HealthProvider Create(SyncProviderKind kind)
        {
            Created.Add(kind);
            var provider = new HealthProvider(this, kind);
            _providers.Add(provider);
            return provider;
        }

        private sealed class HealthProvider(HealthProviderFactory factory, SyncProviderKind kind) : ISyncProvider
        {
            public bool IsDisposed { get; private set; }

            public string ProviderName => kind.ToString();

            public string RemoteRoot => kind.ToString();

            public async Task<ProviderHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default)
            {
                if (factory.Gate == kind)
                {
                    factory.GateReached.SetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return factory.Reports.TryGetValue(kind, out Func<ProviderHealthReport>? report)
                    ? report()
                    : new ProviderHealthReport(ProviderHealthState.Healthy, "Ready.");
            }

            public Task<SyncPlan> CreatePreviewAsync(SyncOptions options, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("A health check must not preview.");

            public Task<SyncResult> ExecuteAsync(SyncPlan plan, SyncOptions options, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("A health check must not sync.");

            public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("A health check must not read the sync log.");

            public void Dispose() => IsDisposed = true;
        }
    }
}
