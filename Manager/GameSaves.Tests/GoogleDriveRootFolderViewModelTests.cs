using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class GoogleDriveRootFolderViewModelTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-24T12:00:00Z");

    [Fact]
    public async Task ConnectedProfile_InspectsStoredFolderAndMayNowSync()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            "profile-a",
            rootId: "root-id",
            rootName: GoogleDriveApplicationRoot.DisplayName));
        StubGoogleDriveOAuthService oauth = ConnectedOAuth(profile);
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                profile.Id,
                "root-id",
                GoogleDriveApplicationRoot.DisplayName,
                WasValidatedById: true,
                Message: "The Google Drive backup folder is ready.")
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            oauth,
            roots,
            profile.Id);

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        Assert.Equal(GoogleDriveConnectionStatus.Connected,
            viewModel.GoogleDriveConnectionStatus);
        Assert.Equal(GoogleDriveRootFolderStatus.Ready,
            viewModel.GoogleDriveRootFolderStatus);
        Assert.Equal(GoogleDriveApplicationRoot.DisplayName,
            viewModel.GoogleDriveRootFolderDisplayText);
        Assert.Equal(1, roots.InspectCalls);
        Assert.True(viewModel.CanCheckGoogleDriveRootFolder);
        Assert.False(viewModel.CanSetUpGoogleDriveRootFolder);
        Assert.True(viewModel.CanUseGoogleDriveForSync);
        Assert.True(viewModel.CanPreviewSync);
        Assert.False(viewModel.CanExecuteSync);
    }

    [Fact]
    public async Task ConnectedProfileWithoutRoot_ShowsExplicitSetup()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile("profile-a"));
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Unconfigured,
                profile.Id,
                Message: "No accessible application backup folder was found.")
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            ConnectedOAuth(profile),
            roots,
            profile.Id);

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        Assert.Equal("Not configured", viewModel.GoogleDriveRootFolderDisplayText);
        Assert.Equal("Setup required",
            viewModel.GoogleDriveRootFolderStatusDisplayText);
        Assert.True(viewModel.CanSetUpGoogleDriveRootFolder);
        Assert.False(viewModel.CanCheckGoogleDriveRootFolder);
        Assert.False(viewModel.CanUseGoogleDriveForSync);
    }

    [Fact]
    public async Task SetupCommand_PersistsAndDisplaysAuthoritativeFolderIdentity()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile("profile-a"));
        var roots = new StubGoogleDriveRootFolderService();
        roots.InspectResult = new GoogleDriveRootFolderResult(
            GoogleDriveRootFolderStatus.Unconfigured,
            profile.Id);
        roots.EnsureHandler = (id, _) =>
        {
            SyncRemoteProfile current = repository.GetById(id)!;
            repository.Update(current with
            {
                RemoteFolderId = "created-id",
                RemoteRootDisplayName = GoogleDriveApplicationRoot.DisplayName,
                UpdatedUtc = Now.AddMinutes(1)
            });
            return Task.FromResult(new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                id,
                "created-id",
                GoogleDriveApplicationRoot.DisplayName,
                WasCreated: true,
                Message: "The visible Google Drive backup folder was created."));
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            ConnectedOAuth(profile),
            roots,
            profile.Id);
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        await viewModel.SetUpGoogleDriveRootFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, roots.EnsureCalls);
        Assert.Equal(GoogleDriveRootFolderStatus.Ready,
            viewModel.GoogleDriveRootFolderStatus);
        Assert.Equal(GoogleDriveApplicationRoot.DisplayName,
            viewModel.GoogleDriveRootFolderDisplayText);
        Assert.Equal("created-id",
            repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("created-id",
            viewModel.SelectedRemoteProfile!.RemoteFolderId);
        Assert.Equal(GoogleDriveApplicationRoot.DisplayName,
            viewModel.SelectedRemoteProfile.RemoteRootDisplayName);
        Assert.True(viewModel.CanUseGoogleDriveForSync);
    }

    [Fact]
    public async Task MovedFolder_IsDisplayedAsLinkedById()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            "profile-a",
            rootId: "moved-id",
            rootName: "Renamed folder"));
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Moved,
                profile.Id,
                "moved-id",
                "Renamed folder",
                WasValidatedById: true,
                WasMoved: true)
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            ConnectedOAuth(profile),
            roots,
            profile.Id);

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        Assert.Equal("Renamed folder",
            viewModel.GoogleDriveRootFolderDisplayText);
        Assert.Contains("still linked by ID",
            viewModel.GoogleDriveRootFolderStatusDisplayText,
            StringComparison.Ordinal);
        Assert.True(viewModel.CanUseGoogleDriveForSync);
    }

    [Fact]
    public async Task Recreate_RequiresConfirmationAndResetsItAfterSuccess()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            "profile-a",
            rootId: "stale-id",
            rootName: "Old folder"));
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Missing,
                profile.Id,
                "stale-id",
                "Old folder",
                RequiresRecreationConfirmation: true)
        };
        roots.RecreateHandler = (id, confirmation, _) =>
        {
            Assert.Equal(
                GoogleDriveRootFolderRecreationConfirmation.Confirmed,
                confirmation);
            SyncRemoteProfile current = repository.GetById(id)!;
            repository.Update(current with
            {
                RemoteFolderId = "replacement-id",
                RemoteRootDisplayName = GoogleDriveApplicationRoot.DisplayName
            });
            return Task.FromResult(new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                id,
                "replacement-id",
                GoogleDriveApplicationRoot.DisplayName,
                WasCreated: true));
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            ConnectedOAuth(profile),
            roots,
            profile.Id);
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        await viewModel.RecreateGoogleDriveRootFolderCommand.ExecuteAsync(null);
        Assert.Equal(0, roots.RecreateCalls);

        viewModel.ConfirmRecreateGoogleDriveRootFolder = true;
        await viewModel.RecreateGoogleDriveRootFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, roots.RecreateCalls);
        Assert.False(viewModel.ConfirmRecreateGoogleDriveRootFolder);
        Assert.Equal("replacement-id",
            repository.GetById(profile.Id)!.RemoteFolderId);
    }

    [Fact]
    public async Task ProfileChange_CancelsRootWorkAndIgnoresLateResult()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile first = repository.Create(Profile(
            "profile-a",
            rootId: "first-root",
            rootName: "First folder"));
        SyncRemoteProfile second = repository.Create(Profile(
            "profile-b",
            rootId: "second-root",
            rootName: "Second folder"));
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectHandler = async (id, _) =>
            {
                if (id == first.Id)
                {
                    entered.SetResult();
                    await release.Task;
                    return new GoogleDriveRootFolderResult(
                        GoogleDriveRootFolderStatus.Ready,
                        first.Id,
                        "first-root",
                        "Late first result");
                }

                return new GoogleDriveRootFolderResult(
                    GoogleDriveRootFolderStatus.Ready,
                    second.Id,
                    "second-root",
                    "Second folder");
            }
        };
        StubGoogleDriveOAuthService oauth = ConnectedOAuth(first);
        oauth.RestoreHandler = (id, _) =>
        {
            SyncRemoteProfile selected = repository.GetById(id)!;
            return Task.FromResult(ConnectedResult(selected));
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            oauth,
            roots,
            first.Id);
        await viewModel.GoogleAuthenticationInitializationTask;
        await entered.Task;

        viewModel.ConfirmRecreateGoogleDriveRootFolder = true;
        viewModel.SelectedRemoteProfile = second;
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;
        release.SetResult();
        await Task.Yield();

        Assert.Equal(second.Id, viewModel.SelectedRemoteProfile!.Id);
        Assert.Equal("Second folder",
            viewModel.GoogleDriveRootFolderDisplayText);
        Assert.False(viewModel.ConfirmRecreateGoogleDriveRootFolder);
    }

    [Fact]
    public async Task CancelFolderOperation_ResetsBusyState()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            "profile-a",
            rootId: "root-id",
            rootName: GoogleDriveApplicationRoot.DisplayName));
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                profile.Id,
                "root-id",
                GoogleDriveApplicationRoot.DisplayName)
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            ConnectedOAuth(profile),
            roots,
            profile.Id);
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        roots.InspectHandler = async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        };

        Task operation = viewModel.CheckGoogleDriveRootFolderCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.True(viewModel.IsGoogleDriveRootFolderBusy);
        Assert.True(viewModel.CanCancelGoogleDriveRootFolderOperation);

        viewModel.CancelGoogleDriveRootFolderCommand.Execute(null);
        await operation;

        Assert.False(viewModel.IsGoogleDriveRootFolderBusy);
        Assert.False(viewModel.CanCancelGoogleDriveRootFolderOperation);
        Assert.Contains("cancelled",
            viewModel.GoogleDriveRootFolderMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disconnect_PreservesRootMetadataAndDisablesRootActions()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(Profile(
            "profile-a",
            rootId: "preserved-root",
            rootName: "Preserved folder"));
        StubGoogleDriveOAuthService oauth = ConnectedOAuth(profile);
        oauth.DisconnectResult = new GoogleDriveDisconnectionResult(
            GoogleDriveDisconnectionStatus.Disconnected,
            LocalAuthenticationRemoved: true,
            ProfilePreserved: true,
            AccountMetadataCleared: true);
        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                profile.Id,
                "preserved-root",
                "Preserved folder")
        };
        SyncViewModel viewModel = CreateViewModel(
            repository,
            oauth,
            roots,
            profile.Id);
        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        viewModel.ConfirmDisconnectGoogleDrive = true;
        await viewModel.DisconnectGoogleDriveCommand.ExecuteAsync(null);

        Assert.Equal("preserved-root",
            repository.GetById(profile.Id)!.RemoteFolderId);
        Assert.Equal("Preserved folder",
            repository.GetById(profile.Id)!.RemoteRootDisplayName);
        Assert.False(viewModel.CanCheckGoogleDriveRootFolder);
        Assert.False(viewModel.CanSetUpGoogleDriveRootFolder);
        Assert.False(viewModel.CanUseGoogleDriveForSync);
    }

    [Fact]
    public void SyncView_DefinesRootActionsWithoutDisplayingRawFolderId()
    {
        string view = File.ReadAllText(FindSyncView());

        Assert.Contains("Set Up Drive Folder", view, StringComparison.Ordinal);
        Assert.Contains("Check Drive Folder", view, StringComparison.Ordinal);
        Assert.Contains("Recreate Drive Folder", view, StringComparison.Ordinal);
        Assert.Contains(
            "Confirm creating or selecting a replacement Google Drive root folder",
            view,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteFolderId}", view, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleDriveRootFolderId", view, StringComparison.Ordinal);
    }

    private static SyncViewModel CreateViewModel(
        InMemorySyncRemoteProfileRepository repository,
        StubGoogleDriveOAuthService oauth,
        StubGoogleDriveRootFolderService roots,
        Guid selectedProfileId)
    {
        var settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.GoogleDrive,
            SelectedRemoteProfileId = selectedProfileId
        };
        return new SyncViewModel(
            new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(Now),
            oauth,
            SyncProviderSelectionTests.NewWorkspaceLayout(),
            roots);
    }

    private static StubGoogleDriveOAuthService ConnectedOAuth(
        SyncRemoteProfile profile) =>
        new()
        {
            ConfigurationState = new GoogleDriveOAuthClientConfigurationState(
                GoogleDriveOAuthClientConfigurationStatus.Available),
            RestoreResult = ConnectedResult(profile),
            ConnectResult = ConnectedResult(profile),
            ReconnectResult = ConnectedResult(profile)
        };

    private static GoogleDriveAuthenticationResult ConnectedResult(
        SyncRemoteProfile profile) =>
        new(
            GoogleDriveAuthenticationStatus.Connected,
            new GoogleDriveConnectionSettings(
                profile.Id,
                profile.AccountDisplayName,
                (profile.ProviderSettings as GoogleDriveSyncRemoteSettings)?.AccountEmail,
                profile.RemoteFolderId,
                profile.RemoteRootDisplayName,
                GoogleDriveAuthorizationScopes.DriveFile,
                GoogleDriveConnectionStatus.Connected,
                hasStoredToken: true),
            Message: "Google Drive account connected.");

    private static SyncRemoteProfile Profile(
        string displayName,
        string? rootId = null,
        string? rootName = null) =>
        new(
            Guid.NewGuid(),
            displayName,
            SyncProviderKind.GoogleDrive,
            "Example User",
            rootName,
            new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            Now,
            Now,
            null,
            Now,
            rootId);

    private static string FindSyncView()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string solution = Path.Combine(directory.FullName, "Manager.sln");

            if (File.Exists(solution))
            {
                return Path.Combine(
                    directory.FullName,
                    "GameSaves.App",
                    "Views",
                    "SyncView.axaml");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
