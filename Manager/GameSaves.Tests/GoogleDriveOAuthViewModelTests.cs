using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class GoogleDriveOAuthViewModelTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-22T11:00:00Z");

    [Fact]
    public void NewGoogleProfile_IsSavedWithScopeAndWithoutAccountOrRootMetadata()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var oauth = AvailableOAuth();
        SyncViewModel viewModel = CreateViewModel(repository, oauth);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;
        viewModel.RemoteProfileDisplayName = "Personal Google Drive";

        viewModel.SaveRemoteProfileCommand.Execute(null);

        SyncRemoteProfile profile = Assert.Single(repository.GetAll());
        var settings = Assert.IsType<GoogleDriveSyncRemoteSettings>(profile.ProviderSettings);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile, settings.RequestedScope);
        Assert.Null(profile.AccountDisplayName);
        Assert.Null(settings.AccountEmail);
        Assert.Null(profile.RemoteFolderId);
        Assert.Null(profile.RemoteRootDisplayName);
        Assert.False(viewModel.CanPreviewSync);
        Assert.True(viewModel.CanConnectGoogleDrive);
    }

    [Fact]
    public async Task ConnectWithoutSavedProfile_IsBlockedBeforeOAuth()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var oauth = AvailableOAuth();
        SyncViewModel viewModel = CreateViewModel(repository, oauth);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        Assert.Equal(0, oauth.ConnectCalls);
        Assert.Equal(GoogleDriveConnectionStatus.NotConfigured, viewModel.GoogleDriveConnectionStatus);
        Assert.Contains("Save the Google Drive profile", viewModel.GoogleDriveConnectionMessage);
    }

    [Fact]
    public async Task SelectingGoogleProfile_RestoresSilentlyAndNeverStartsInteractiveLogin()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(GoogleProfile(
            Guid.Parse("11111111-aaaa-bbbb-cccc-111111111111"),
            "Drive profile",
            "Saved Account",
            "saved@example.invalid"));
        var oauth = AvailableOAuth();
        SyncViewModel viewModel = CreateViewModel(repository, oauth, profile.Id);

        await viewModel.GoogleAuthenticationInitializationTask;

        Assert.True(viewModel.IsGoogleDriveSelected);
        Assert.Equal("Saved Account", viewModel.GoogleDriveAccountDisplayName);
        Assert.Equal("saved@example.invalid", viewModel.GoogleDriveAccountEmail);
        Assert.Equal(1, oauth.RestoreCalls);
        Assert.Equal(0, oauth.ConnectCalls);
        Assert.False(viewModel.CanExecuteSync);
        Assert.False(viewModel.CanPreviewSync);
    }

    [Fact]
    public async Task ConnectSuccess_ShowsConnectedStateWhileSyncStaysDisabled()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        Guid id = Guid.Parse("22222222-aaaa-bbbb-cccc-222222222222");
        repository.Create(GoogleProfile(id, "Drive profile", null, null));
        var oauth = AvailableOAuth();
        oauth.ConnectResult = new GoogleDriveAuthenticationResult(
            GoogleDriveAuthenticationStatus.Connected,
            new GoogleDriveConnectionSettings(
                id,
                "Example User",
                "user@example.invalid",
                null,
                null,
                GoogleDriveAuthorizationScopes.DriveFile,
                GoogleDriveConnectionStatus.Connected,
                hasStoredToken: true),
            Message: "Google Drive account connected. Backup synchronization is not available yet.");
        SyncViewModel viewModel = CreateViewModel(repository, oauth, id);
        await viewModel.GoogleAuthenticationInitializationTask;

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        Assert.Equal(1, oauth.ConnectCalls);
        Assert.Equal(GoogleDriveConnectionStatus.Connected, viewModel.GoogleDriveConnectionStatus);
        Assert.True(viewModel.HasStoredAuthentication);
        Assert.False(viewModel.CanPreviewSync);
        Assert.False(viewModel.CanExecuteSync);
    }

    [Fact]
    public async Task GooglePreview_IsBlockedWithoutFactoryFallback()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            repository,
            AvailableOAuth(),
            factory: factory);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(0, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
        Assert.False(viewModel.CanExecuteSync);
        Assert.Contains("later milestones", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ProfileSwitch_CancelsRestoreAndIgnoresStaleResult()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        Guid firstId = Guid.Parse("33333333-aaaa-bbbb-cccc-333333333333");
        Guid secondId = Guid.Parse("44444444-aaaa-bbbb-cccc-444444444444");
        SyncRemoteProfile first = repository.Create(GoogleProfile(
            firstId,
            "First Drive",
            "First Saved Account",
            "first@example.invalid"));
        SyncRemoteProfile second = repository.Create(GoogleProfile(
            secondId,
            "Second Drive",
            "Second Saved Account",
            "second@example.invalid"));
        var firstRestore = new TaskCompletionSource<GoogleDriveAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = AvailableOAuth();
        oauth.RestoreHandler = (id, _) => id == firstId
            ? firstRestore.Task
            : Task.FromResult(oauth.RestoreResult);
        SyncViewModel viewModel = CreateViewModel(repository, oauth, first.Id);
        Task staleInitialization = viewModel.GoogleAuthenticationInitializationTask;

        viewModel.SelectedRemoteProfile = second;
        await viewModel.GoogleAuthenticationInitializationTask;
        firstRestore.SetResult(new GoogleDriveAuthenticationResult(
            GoogleDriveAuthenticationStatus.Connected,
            new GoogleDriveConnectionSettings(
                firstId,
                "Stale Account",
                "stale@example.invalid",
                null,
                null,
                GoogleDriveAuthorizationScopes.DriveFile,
                GoogleDriveConnectionStatus.Connected,
                hasStoredToken: true)));
        await staleInitialization;

        Assert.Equal(secondId, viewModel.SelectedRemoteProfile!.Id);
        Assert.Equal("Second Saved Account", viewModel.GoogleDriveAccountDisplayName);
        Assert.Equal("second@example.invalid", viewModel.GoogleDriveAccountEmail);
        Assert.NotEqual(GoogleDriveConnectionStatus.Connected, viewModel.GoogleDriveConnectionStatus);
    }

    [Fact]
    public async Task CancelConnect_ReturnsToIdleWithoutClaimingConnected()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        Guid id = Guid.Parse("55555555-aaaa-bbbb-cccc-555555555555");
        repository.Create(GoogleProfile(id, "Drive profile", null, null));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = AvailableOAuth();
        oauth.ConnectHandler = async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return oauth.ConnectResult;
        };
        SyncViewModel viewModel = CreateViewModel(repository, oauth, id);
        await viewModel.GoogleAuthenticationInitializationTask;

        Task connect = viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(viewModel.IsGoogleDriveConnecting);
        viewModel.CancelGoogleDriveConnectionCommand.Execute(null);
        await connect;

        Assert.False(viewModel.IsGoogleDriveConnecting);
        Assert.Equal(GoogleDriveConnectionStatus.Disconnected, viewModel.GoogleDriveConnectionStatus);
        Assert.Contains("cancelled", viewModel.GoogleDriveConnectionMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.CanExecuteSync);
    }

    [Fact]
    public void InvalidSavedGoogleSettings_DisableConnect()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        Guid id = Guid.Parse("66666666-aaaa-bbbb-cccc-666666666666");
        repository.Create(GoogleProfile(id, "Unavailable Drive", null, null) with
        {
            ProviderSettings = null,
            SettingsError = "The Google Drive profile settings are corrupted."
        });

        SyncViewModel viewModel = CreateViewModel(repository, AvailableOAuth(), id);

        Assert.False(viewModel.CanConnectGoogleDrive);
        Assert.Equal("Not connected", viewModel.GoogleDriveAccountDisplayText);
        Assert.False(viewModel.CanPreviewSync);
    }

    private static StubGoogleDriveOAuthService AvailableOAuth() => new()
    {
        ConfigurationState = new GoogleDriveOAuthClientConfigurationState(
            GoogleDriveOAuthClientConfigurationStatus.Available),
        RestoreResult = new GoogleDriveAuthenticationResult(
            GoogleDriveAuthenticationStatus.NoStoredAuthentication,
            Message: "No stored Google Drive authentication is available.")
    };

    private static SyncViewModel CreateViewModel(
        InMemorySyncRemoteProfileRepository repository,
        StubGoogleDriveOAuthService oauth,
        Guid? selectedProfileId = null,
        SyncProviderSelectionTests.RecordingSyncProviderFactory? factory = null)
    {
        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedRemoteProfileId = selectedProfileId,
            SelectedProviderKind = selectedProfileId is null
                ? SyncProviderKind.LocalFolder
                : SyncProviderKind.GoogleDrive
        };

        return new SyncViewModel(
            factory ?? new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(Now),
            oauth);
    }

    private static SyncRemoteProfile GoogleProfile(
        Guid id,
        string displayName,
        string? accountDisplayName,
        string? email) =>
        new(
            id,
            displayName,
            SyncProviderKind.GoogleDrive,
            accountDisplayName,
            null,
            new GoogleDriveSyncRemoteSettings(
                email,
                GoogleDriveAuthorizationScopes.DriveFile),
            Now,
            Now,
            null,
            null,
            null);
}
