using GameSaves.App.Models;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class SyncRemoteProfileViewModelTests
{
    [Fact]
    public async Task SaveRenameSaveAsAndDelete_ManageOnlyProfileConfiguration()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var clock = new FixedUtcClock(start);
        var repository = new InMemorySyncRemoteProfileRepository();
        var factory = new ProfileTestProviderFactory();
        SyncViewModel viewModel = CreateViewModel(repository, factory, clock);
        viewModel.RemoteProfileDisplayName = "USB Backup";
        viewModel.RemoteRootPath = @"D:\Backups";

        viewModel.SaveRemoteProfileCommand.Execute(null);

        SyncRemoteProfile original = Assert.Single(repository.GetAll());
        Assert.NotEqual(Guid.Empty, original.Id);
        Assert.Equal(start, original.CreatedUtc);
        Assert.Equal(start, original.UpdatedUtc);
        Assert.Equal("Saved", viewModel.RemoteProfileState);
        Assert.Equal(0, factory.CreateCount);

        clock.UtcNow = start.AddHours(1);
        viewModel.RemoteRootPath = @"E:\Backups";
        viewModel.SaveRemoteProfileCommand.Execute(null);
        SyncRemoteProfile updated = repository.GetById(original.Id)!;
        Assert.Equal(start, updated.CreatedUtc);
        Assert.Equal(clock.UtcNow, updated.UpdatedUtc);

        viewModel.RemoteProfileDisplayName = "NAS Backup";
        viewModel.RenameRemoteProfileCommand.Execute(null);
        SyncRemoteProfile renamed = repository.GetById(original.Id)!;
        Assert.Equal("NAS Backup", renamed.DisplayName);
        Assert.Equal(@"E:\Backups",
            Assert.IsType<LocalFolderSyncRemoteSettings>(renamed.ProviderSettings).LocalFolderPath);

        viewModel.RemoteProfileDisplayName = "NAS Backup Copy";
        viewModel.SftpPassword = "session-password";
        viewModel.SftpKeyPassphrase = "session-passphrase";
        viewModel.SaveRemoteProfileAsCommand.Execute(null);
        Assert.Equal(2, repository.GetAll().Count);
        Assert.NotEqual(original.Id, viewModel.SelectedRemoteProfile!.Id);
        Assert.Equal("", viewModel.SftpPassword);
        Assert.Equal("", viewModel.SftpKeyPassphrase);

        Guid copiedId = viewModel.SelectedRemoteProfile.Id;
        viewModel.ConfirmDeleteRemoteProfile = true;
        await viewModel.DeleteRemoteProfileCommand.ExecuteAsync(null);

        Assert.Null(repository.GetById(copiedId));
        Assert.NotNull(repository.GetById(original.Id));
        Assert.DoesNotContain(viewModel.RemoteProfiles, profile => profile.Id == copiedId);
        Assert.DoesNotContain(
            viewModel.RemoteProfileOptions,
            option => option.Profile?.Id == copiedId);
        Assert.Null(viewModel.SelectedRemoteProfile);
        Assert.Equal("Unsaved changes", viewModel.RemoteProfileState);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, factory.ExecuteCount);
    }

    [Fact]
    public async Task NoSavedProfileOption_PreservesFormAndAllowsPreviewWithoutProfile()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var clock = new FixedUtcClock(now);
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile sftp = repository.Create(SftpProfile(
            "Home SFTP", "home.example.test", now));
        var factory = new ProfileTestProviderFactory();
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedRemoteProfileId = sftp.Id,
            LegacyProfileMigrationCompleted = true
        };
        var store = new RecordingSettingsStore(settings);
        var viewModel = new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new NullProfileFolderPicker(),
            store,
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            clock);
        viewModel.SftpPassword = "first-session-password";
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        ProfileTestProvider firstProvider =
            Assert.IsType<ProfileTestProvider>(factory.LastProvider);
        SyncRemoteProfileOption noProfile = viewModel.RemoteProfileOptions[0];

        Assert.Null(noProfile.Profile);
        Assert.Equal("No saved profile (use current settings)", noProfile.DisplayName);

        viewModel.SelectedRemoteProfileOption = noProfile;

        Assert.True(firstProvider.IsDisposed);
        Assert.Null(viewModel.SelectedRemoteProfile);
        Assert.Equal(noProfile, viewModel.SelectedRemoteProfileOption);
        Assert.Equal(SyncProviderKind.Sftp, viewModel.SelectedProviderKind);
        Assert.Equal("home.example.test", viewModel.SftpHost);
        Assert.Equal("", viewModel.SftpPassword);
        Assert.Equal("", viewModel.SftpKeyPassphrase);
        Assert.False(viewModel.SftpTrustNewHostKey);
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Equal("Unsaved settings (no profile)", viewModel.RemoteProfileState);
        Assert.Null(store.Saved!.SelectedRemoteProfileId);
        Assert.Equal("home.example.test", store.Saved.SftpHost);

        viewModel.SftpPassword = "second-session-password";
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(viewModel.CanExecuteSync);
        Assert.Null(viewModel.SelectedRemoteProfile);
    }

    [Fact]
    public async Task SwitchingProfiles_ClearsSecretsInvalidatesPlanDisposesProviderAndDoesNotConnect()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var clock = new FixedUtcClock(now);
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile local = repository.Create(LocalProfile(
            "USB Backup", @"D:\Backups", now));
        SyncRemoteProfile sftp = repository.Create(SftpProfile(
            "Home SFTP", "home.example.test", now));
        var factory = new ProfileTestProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            repository, factory, clock, selectedProfileId: local.Id);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        ProfileTestProvider provider = Assert.IsType<ProfileTestProvider>(factory.LastProvider);
        Assert.True(viewModel.CanExecuteSync);
        Assert.NotEmpty(viewModel.Items);

        viewModel.SftpPassword = "server-one-password";
        viewModel.SftpKeyPassphrase = "server-one-passphrase";
        viewModel.SftpTrustNewHostKey = true;
        viewModel.SelectedRemoteProfile = viewModel.RemoteProfiles
            .Single(profile => profile.Id == sftp.Id);

        Assert.True(provider.IsDisposed);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Empty(viewModel.Items);
        Assert.Equal("", viewModel.SftpPassword);
        Assert.Equal("", viewModel.SftpKeyPassphrase);
        Assert.False(viewModel.SftpTrustNewHostKey);
        Assert.Equal("home.example.test", viewModel.SftpHost);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.ExecuteCount);
        Assert.Null(repository.GetById(sftp.Id)!.LastUsedUtc);
    }

    [Fact]
    public async Task ActualPreviewUpdatesUsageAndOnlySuccessfulValidationUpdatesConnection()
    {
        DateTimeOffset first = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var clock = new FixedUtcClock(first);
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(LocalProfile(
            "USB Backup", @"D:\Backups", first.AddDays(-1)));
        var factory = new ProfileTestProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            repository, factory, clock, selectedProfileId: profile.Id);

        Assert.Null(repository.GetById(profile.Id)!.LastUsedUtc);
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(first, repository.GetById(profile.Id)!.LastUsedUtc);
        Assert.Equal(first, repository.GetById(profile.Id)!.LastSuccessfulConnectionUtc);

        clock.UtcNow = first.AddHours(1);
        factory.ReturnValidationError = true;
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(clock.UtcNow, repository.GetById(profile.Id)!.LastUsedUtc);
        Assert.Equal(first, repository.GetById(profile.Id)!.LastSuccessfulConnectionUtc);
    }

    [Fact]
    public async Task UnsupportedSavedProfile_RemainsVisibleAndBlockedWithoutFallback()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var repository = new InMemorySyncRemoteProfileRepository();
        var future = new SyncRemoteProfile(
            Guid.NewGuid(), "Personal Google Drive", SyncProviderKind.GoogleDrive,
            "person@example.test", "GameSave Manager Backups", null,
            now, now, null, null, "future-folder-id",
            "Google Drive sync is not implemented yet.");
        repository.Create(future);
        var factory = new ProfileTestProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            repository,
            factory,
            new FixedUtcClock(now),
            selectedProfileId: future.Id);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal("Personal Google Drive", viewModel.SelectedRemoteProfile!.DisplayName);
        Assert.Equal("Profile unavailable", viewModel.RemoteProfileState);
        Assert.Contains("not implemented", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, factory.ExecuteCount);
    }

    [Fact]
    public async Task DeleteCommand_DisposesProviderClearsSessionSecretsAndDeletesOwnedSecrets()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(LocalProfile(
            "USB Backup", @"D:\Backups", now));
        var secretStore = new InMemorySecretStore();
        var secretKey = new GameSaves.Core.Secrets.SecretKey(
            profile.Id,
            GameSaves.Core.Secrets.SecretNames.OAuthTokenData);
        await secretStore.StoreAsync(secretKey, new byte[] { 42 });
        var factory = new ProfileTestProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            repository,
            factory,
            new FixedUtcClock(now),
            selectedProfileId: profile.Id,
            secretStore: secretStore);
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        ProfileTestProvider provider =
            Assert.IsType<ProfileTestProvider>(factory.LastProvider);
        viewModel.SftpPassword = "session-password";
        viewModel.SftpKeyPassphrase = "session-passphrase";
        viewModel.ConfirmDeleteRemoteProfile = true;

        await viewModel.DeleteRemoteProfileCommand.ExecuteAsync(null);

        Assert.True(provider.IsDisposed);
        Assert.Equal("", viewModel.SftpPassword);
        Assert.Equal("", viewModel.SftpKeyPassphrase);
        Assert.False(await secretStore.ExistsAsync(secretKey));
        Assert.Null(repository.GetById(profile.Id));
    }

    [Fact]
    public async Task DisconnectCommand_RemovesSecretsButPreservesProfileAndConfiguration()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(SftpProfile(
            "Home SFTP", "home.example.test", now));
        var secretStore = new InMemorySecretStore();
        var secretKey = new GameSaves.Core.Secrets.SecretKey(
            profile.Id,
            GameSaves.Core.Secrets.SecretNames.OAuthTokenData);
        await secretStore.StoreAsync(secretKey, new byte[] { 42 });
        SyncViewModel viewModel = CreateViewModel(
            repository,
            new ProfileTestProviderFactory(),
            new FixedUtcClock(now),
            selectedProfileId: profile.Id,
            secretStore: secretStore);
        viewModel.SftpPassword = "session-password";
        viewModel.SftpKeyPassphrase = "session-passphrase";

        await viewModel.DisconnectAuthenticationCommand.ExecuteAsync(null);

        Assert.False(await secretStore.ExistsAsync(secretKey));
        Assert.NotNull(repository.GetById(profile.Id));
        Assert.Equal("home.example.test",
            Assert.IsType<SftpSyncRemoteSettings>(
                repository.GetById(profile.Id)!.ProviderSettings).Host);
        Assert.Equal("", viewModel.SftpPassword);
        Assert.Equal("", viewModel.SftpKeyPassphrase);
    }

    private static SyncViewModel CreateViewModel(
        InMemorySyncRemoteProfileRepository repository,
        ProfileTestProviderFactory factory,
        FixedUtcClock clock,
        Guid? selectedProfileId = null,
        InMemorySecretStore? secretStore = null)
    {
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedRemoteProfileId = selectedProfileId,
            LegacyProfileMigrationCompleted = true
        };
        var store = new RecordingSettingsStore(settings);
        secretStore ??= new InMemorySecretStore();
        return new SyncViewModel(
            factory,
            new SyncProviderCatalog(),
            new NullProfileFolderPicker(),
            store,
            repository,
            new SyncRemoteProfileService(repository, secretStore),
            new StubSyncRemoteProfileMigrationService(settings),
            clock);
    }

    private static SyncRemoteProfile LocalProfile(
        string name,
        string path,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(), name, SyncProviderKind.LocalFolder,
            null, path, new LocalFolderSyncRemoteSettings(path),
            now, now, null, null, null);

    private static SyncRemoteProfile SftpProfile(
        string name,
        string host,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(), name, SyncProviderKind.Sftp,
            $"alice@{host}", $"sftp://alice@{host}:22/gamesave-sync",
            new SftpSyncRemoteSettings(
                host, 22, "alice", SftpAuthMethod.Password, null, "/gamesave-sync"),
            now, now, null, null, null);

    private sealed class RecordingSettingsStore : ISyncSettingsStore
    {
        private readonly SyncUiSettings _settings;

        public RecordingSettingsStore(SyncUiSettings settings)
        {
            _settings = settings;
        }

        public SyncUiSettings? Saved { get; private set; }

        public SyncUiSettings Load() => _settings;

        public void Save(SyncUiSettings settings) => Saved = settings;
    }

    private sealed class NullProfileFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startLocation = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(
            string title,
            string filterName,
            string[] patterns) => Task.FromResult<string?>(null);
    }

    private sealed class ProfileTestProviderFactory : ISyncProviderFactory
    {
        public int CreateCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public bool ReturnValidationError { get; set; }
        public ISyncProvider? LastProvider { get; private set; }

        public ISyncProvider CreateLocalFolderProvider(string remoteRoot) =>
            Create("Local folder", remoteRoot);

        public ISyncProvider CreateSftpProvider(SftpConnectionSettings settings) =>
            Create("SFTP", settings.DisplayRoot);

        public void ForgetSftpHostKey(string host, int port)
        {
        }

        private ISyncProvider Create(string name, string root)
        {
            CreateCount++;
            return LastProvider = new ProfileTestProvider(
                name,
                root,
                () => ReturnValidationError,
                () => ExecuteCount++);
        }
    }

    private sealed class ProfileTestProvider : ISyncProvider
    {
        private readonly Func<bool> _returnValidationError;
        private readonly Action _onExecute;

        public ProfileTestProvider(
            string providerName,
            string remoteRoot,
            Func<bool> returnValidationError,
            Action onExecute)
        {
            ProviderName = providerName;
            RemoteRoot = remoteRoot;
            _returnValidationError = returnValidationError;
            _onExecute = onExecute;
        }

        public string ProviderName { get; }
        public string RemoteRoot { get; }
        public bool IsDisposed { get; private set; }

        public Task<SyncPlan> CreatePreviewAsync(
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            if (_returnValidationError())
            {
                return Task.FromResult(new SyncPlan(
                    ProviderName, RemoteRoot, Array.Empty<SyncItem>(),
                    new[]
                    {
                        new TransferPreviewWarning(
                            "ValidationFailed", "Connection failed.",
                            TransferWarningSeverity.Error)
                    },
                    false, 0, 0, 0, 0, 0, 0)
                {
                    ProviderValidationSucceeded = false
                });
            }

            var item = new SyncItem(
                "run-one", SyncItemAction.UploadToRemote, true, false,
                "local/run-one", $"{RemoteRoot}/run-one", "Test Game",
                1, 10, "Copy to remote");
            return Task.FromResult(new SyncPlan(
                ProviderName, RemoteRoot, new[] { item },
                Array.Empty<TransferPreviewWarning>(), true,
                1, 0, 0, 0, 10, 0));
        }

        public Task<SyncResult> ExecuteAsync(
            SyncPlan plan,
            SyncOptions options,
            CancellationToken cancellationToken = default)
        {
            _onExecute();
            return Task.FromResult(new SyncResult(
                plan, false, 0, 0, 0, 0,
                Array.Empty<SyncItemResult>(),
                Array.Empty<TransferPreviewWarning>()));
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetSyncLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncLogEntry>>(Array.Empty<SyncLogEntry>());

        public void Dispose() => IsDisposed = true;
    }
}
