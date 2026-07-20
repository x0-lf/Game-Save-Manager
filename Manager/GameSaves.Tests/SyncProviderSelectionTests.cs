using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;

namespace GameSaves.Tests;

public sealed class SyncProviderSelectionTests
{
    [Fact]
    public void Selector_ExposesOnlyImplementedProvidersAndDefaultsToLocalFolder()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);

        Assert.Equal(SyncProviderKind.LocalFolder, viewModel.SelectedProviderKind);
        Assert.True(viewModel.IsLocalFolderSelected);
        Assert.False(viewModel.IsSftpSelected);
        Assert.Collection(
            viewModel.ProviderOptions,
            option =>
            {
                Assert.Equal(SyncProviderKind.LocalFolder, option.Kind);
                Assert.Equal("Local or mounted folder", option.DisplayName);
            },
            option =>
            {
                Assert.Equal(SyncProviderKind.Sftp, option.Kind);
                Assert.Equal("SFTP server (SSH)", option.DisplayName);
            });
    }

    [Fact]
    public async Task LocalFolderSelection_CreatesOnlyTheLocalProviderPath()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.RemoteRootPath = @"D:\MountedBackups";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
        Assert.Equal(@"D:\MountedBackups", factory.LastLocalFolderPath);
    }

    [Fact]
    public async Task SftpSelection_CreatesOnlyTheSftpProviderPath()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        viewModel.SftpHost = "backup.example.test";
        viewModel.SftpPort = "2222";
        viewModel.SftpUsername = "alice";
        viewModel.SftpPassword = "session-only";
        viewModel.SftpRemotePath = "/srv/game-saves";

        Assert.False(viewModel.IsLocalFolderSelected);
        Assert.True(viewModel.IsSftpSelected);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(0, factory.LocalFolderCreateCount);
        Assert.Equal(1, factory.SftpCreateCount);
        Assert.NotNull(factory.LastSftpSettings);
        Assert.Equal("backup.example.test", factory.LastSftpSettings!.Host);
        Assert.Equal(2222, factory.LastSftpSettings.Port);
        Assert.Equal("alice", factory.LastSftpSettings.Username);
        Assert.Equal("/srv/game-saves", factory.LastSftpSettings.RemotePath);
        Assert.Equal("session-only", factory.LastSftpSettings.Password);
    }

    [Theory]
    [InlineData(SyncProviderKind.GoogleDrive, "Google Drive sync is not implemented yet.")]
    [InlineData(SyncProviderKind.WebDav, "WebDAV sync is not implemented yet.")]
    [InlineData(SyncProviderKind.OneDrive, "OneDrive sync is not implemented yet.")]
    public async Task UnimplementedProvider_BlocksBeforeFactoryCreation(
        SyncProviderKind kind,
        string expectedMessage)
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.SelectedProviderKind = kind;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(expectedMessage, viewModel.StatusMessage);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Equal(0, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
    }

    [Fact]
    public async Task UnknownPersistedProvider_DoesNotSilentlySelectAnotherRemote()
    {
        var factory = new RecordingSyncProviderFactory();
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = (SyncProviderKind)99,
            LocalFolderPath = @"D:\MustNotBeUsed",
            SftpHost = "must-not-be-used"
        };
        var viewModel = CreateViewModel(factory, settings);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(99, (int)viewModel.SelectedProviderKind);
        Assert.False(viewModel.IsLocalFolderSelected);
        Assert.False(viewModel.IsSftpSelected);
        Assert.Contains("not supported by this version", viewModel.StatusMessage);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Equal(0, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
    }

    [Fact]
    public async Task ChangingProvider_InvalidatesPlanDisposesProviderAndDoesNotReconnect()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.RemoteRootPath = @"D:\MountedBackups";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        FakeSyncProvider provider = Assert.IsType<FakeSyncProvider>(factory.LastProvider);
        Assert.True(viewModel.CanExecuteSync);
        Assert.NotEmpty(viewModel.Items);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.True(provider.IsDisposed);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Empty(viewModel.Items);
        Assert.Equal(1, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
    }

    [Fact]
    public async Task ChangingConnectionSetting_InvalidatesPlanAndDisposesProvider()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.RemoteRootPath = @"D:\MountedBackups";
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        FakeSyncProvider provider = Assert.IsType<FakeSyncProvider>(factory.LastProvider);

        viewModel.RemoteRootPath = @"E:\DifferentBackups";

        Assert.True(provider.IsDisposed);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Empty(viewModel.Items);
    }

    [Fact]
    public async Task SftpValidation_BlocksInvalidPortAndMissingSessionPassword()
    {
        var factory = new RecordingSyncProviderFactory();
        var viewModel = CreateViewModel(factory, SyncUiSettings.Default);
        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        viewModel.SftpHost = "backup.example.test";
        viewModel.SftpUsername = "alice";
        viewModel.SftpPort = "70000";
        viewModel.SftpPassword = "session-only";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Contains("between 1 and 65535", viewModel.StatusMessage);
        Assert.Equal(0, factory.SftpCreateCount);

        viewModel.SftpPort = "22";
        viewModel.SftpPassword = "";
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Contains("remains session-only", viewModel.StatusMessage);
        Assert.Equal(0, factory.SftpCreateCount);
    }

    [Fact]
    public void SftpDisplayRoot_ContainsNoPasswordOrPassphrase()
    {
        var settings = new SftpConnectionSettings(
            Host: "backup.example.test",
            Port: 22,
            Username: "alice",
            AuthMethod: SftpAuthMethod.PrivateKey,
            Password: "password-secret",
            PrivateKeyPath: @"C:\Keys\id_ed25519",
            PrivateKeyPassphrase: "passphrase-secret",
            RemotePath: "/game-saves",
            TrustNewHostKey: true);

        Assert.Equal(
            "sftp://alice@backup.example.test:22/game-saves",
            settings.DisplayRoot);
        Assert.DoesNotContain("password-secret", settings.DisplayRoot);
        Assert.DoesNotContain("passphrase-secret", settings.DisplayRoot);
    }

    [Fact]
    public async Task PreviewPersistsOnlyNonSecretSettings()
    {
        var factory = new RecordingSyncProviderFactory();
        var store = new InMemorySyncSettingsStore(SyncUiSettings.Default);
        var viewModel = new SyncViewModel(
            factory,
            new NullFolderPickerService(),
            store,
            new InMemorySyncRemoteProfileRepository(),
            new StubSyncRemoteProfileMigrationService(SyncUiSettings.Default),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")))
        {
            SelectedProviderKind = SyncProviderKind.Sftp,
            SftpHost = "backup.example.test",
            SftpUsername = "alice",
            SftpPassword = "password-secret",
            SftpKeyPassphrase = "passphrase-secret"
        };

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.NotNull(store.Saved);
        Assert.Equal(SyncProviderKind.Sftp, store.Saved!.SelectedProviderKind);
        Assert.DoesNotContain(
            store.Saved.GetType().GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase));
    }

    private static SyncViewModel CreateViewModel(
        RecordingSyncProviderFactory factory,
        SyncUiSettings settings)
    {
        return new SyncViewModel(
            factory,
            new NullFolderPickerService(),
            new InMemorySyncSettingsStore(settings),
            new InMemorySyncRemoteProfileRepository(),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")));
    }

    private sealed class InMemorySyncSettingsStore : ISyncSettingsStore
    {
        private readonly SyncUiSettings _loaded;

        public InMemorySyncSettingsStore(SyncUiSettings loaded)
        {
            _loaded = loaded;
        }

        public SyncUiSettings? Saved { get; private set; }

        public SyncUiSettings Load() => _loaded;

        public void Save(SyncUiSettings settings)
        {
            Saved = settings;
        }
    }

    private sealed class NullFolderPickerService : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startLocation = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(
            string title,
            string filterName,
            string[] patterns) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingSyncProviderFactory : ISyncProviderFactory
    {
        public int LocalFolderCreateCount { get; private set; }
        public int SftpCreateCount { get; private set; }
        public string? LastLocalFolderPath { get; private set; }
        public SftpConnectionSettings? LastSftpSettings { get; private set; }
        public ISyncProvider? LastProvider { get; private set; }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot)
        {
            LocalFolderCreateCount++;
            LastLocalFolderPath = remoteRoot;
            return LastProvider = new FakeSyncProvider("Local folder", remoteRoot);
        }

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings)
        {
            SftpCreateCount++;
            LastSftpSettings = settings;
            return LastProvider = new FakeSyncProvider("SFTP", settings.DisplayRoot);
        }

        public void ForgetSftpHostKey(string host, int port)
        {
        }
    }

    private sealed class FakeSyncProvider : ISyncProvider
    {
        public FakeSyncProvider(string providerName, string remoteRoot)
        {
            ProviderName = providerName;
            RemoteRoot = remoteRoot;
        }

        public string ProviderName { get; }
        public string RemoteRoot { get; }
        public bool IsDisposed { get; private set; }

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            var item = new SyncItem(
                RunName: "run-one",
                Action: SyncItemAction.UploadToRemote,
                ExistsLocally: true,
                ExistsRemotely: false,
                LocalPath: "local/run-one",
                RemotePath: $"{RemoteRoot}/run-one",
                GameName: "Test Game",
                FileCount: 1,
                TotalBytes: 10,
                StatusText: "Copy to remote");

            return Task.FromResult(new SyncPlan(
                ProviderName,
                RemoteRoot,
                new[] { item },
                Array.Empty<TransferPreviewWarning>(),
                CanExecute: true,
                UploadCount: 1,
                DownloadCount: 0,
                InSyncCount: 0,
                ConflictCount: 0,
                BytesToUpload: 10,
                BytesToDownload: 0));
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncResult(
                plan,
                options.DryRun,
                Uploaded: 0,
                Downloaded: 0,
                Skipped: 0,
                BytesCopied: 0,
                Items: Array.Empty<SyncItemResult>(),
                Warnings: Array.Empty<TransferPreviewWarning>()));
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncLogEntry>>(
                Array.Empty<SyncLogEntry>());
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
