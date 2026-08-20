using System.Reflection;
using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone V Task 4. Activation is only safe if nothing else moved: the two
/// providers that already worked must behave exactly as before, Google Drive
/// must reach preview, execution, history, and the sync log through the same
/// shared code those two use rather than through a branch of its own, and none
/// of the state activation newly exposes may carry an account address, a
/// folder identifier, or a token.
/// </summary>
public sealed class SyncUiProviderParityTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-20T12:00:00Z");

    /// <summary>
    /// Synthetic values that exist only inside this test. They are planted in
    /// the saved profile so the privacy sweep below is looking for something
    /// that is genuinely present in the object graph the UI binds against.
    /// </summary>
    private const string AccountEmailMarker = "parity-sweep@example.invalid";

    private const string RootFolderIdMarker = "parity-sweep-root-folder-id";

    private const string LocalFolderPath = @"D:\MountedBackups";

    private const string SftpHost = "backup.example.test";

    // ---- 4a: the two existing providers are unchanged ----

    [Fact]
    public async Task UsableDriveProfile_LeavesLocalFolderAndSftpSelectionUnchanged()
    {
        (SyncViewModel viewModel,
         SyncProviderSelectionTests.RecordingSyncProviderFactory factory) =
            await CreateConnectedDriveViewModelAsync();

        // Non-vacuity: Google Drive is genuinely usable in this view model, so
        // what follows isolates a live provider rather than a disabled one.
        Assert.True(viewModel.CanUseGoogleDriveForSync);

        viewModel.SelectedProviderKind = SyncProviderKind.LocalFolder;
        viewModel.RemoteRootPath = LocalFolderPath;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.LocalFolderCreateCount);
        Assert.Equal(LocalFolderPath, factory.LastLocalFolderPath);
        Assert.Equal(0, factory.SftpCreateCount);
        Assert.Equal(0, factory.GoogleDriveCreateCount);
        Assert.True(viewModel.CanExecuteSync);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        ConfigureSftp(viewModel);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.SftpCreateCount);
        Assert.Equal(SftpHost, factory.LastSftpSettings!.Host);
        Assert.Equal("/srv/game-saves", factory.LastSftpSettings.RemotePath);
        Assert.Equal(1, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.GoogleDriveCreateCount);
        Assert.True(viewModel.CanExecuteSync);
    }

    [Fact]
    public async Task DriveActivation_LeavesTheRefusalMessagesOfTheOtherProvidersIntact()
    {
        (SyncViewModel viewModel,
         SyncProviderSelectionTests.RecordingSyncProviderFactory factory) =
            await CreateConnectedDriveViewModelAsync();

        viewModel.SelectedProviderKind = SyncProviderKind.LocalFolder;
        viewModel.RemoteRootPath = "   ";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(
            "Choose a local or mounted sync folder first.",
            viewModel.StatusMessage);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        ConfigureSftp(viewModel);
        viewModel.SftpPassword = "";

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(
            "Enter the SFTP password (it remains session-only).",
            viewModel.StatusMessage);

        Assert.Equal(0, factory.LocalFolderCreateCount);
        Assert.Equal(0, factory.SftpCreateCount);
        Assert.Equal(0, factory.GoogleDriveCreateCount);
    }

    // ---- 4b: Drive uses the same shared path as the other two ----

    [Fact]
    public async Task AllThreeProviders_ReachPreviewAndExecutionThroughTheSameSharedState()
    {
        SharedRunState local = await RunSharedPathAsync(
            CreateLocalFolderViewModel());
        SharedRunState sftp = await RunSharedPathAsync(
            CreateSftpViewModel());

        (SyncViewModel driveViewModel,
         SyncProviderSelectionTests.RecordingSyncProviderFactory driveFactory) =
            await CreateConnectedDriveViewModelAsync();
        SharedRunState drive = await RunSharedPathAsync(driveViewModel);

        // The provider was built through the Core factory seam, keyed by the
        // saved profile, which is the only Drive-specific step in the run.
        Assert.Equal(1, driveFactory.GoogleDriveCreateCount);
        Assert.Equal(
            driveViewModel.SelectedRemoteProfile!.Id,
            driveFactory.LastGoogleDriveProfileId);

        // Non-vacuity: the shared state is populated rather than uniformly
        // empty, so equality below is a real match and not a match of nothing.
        Assert.Equal(1, local.ItemCount);
        Assert.Equal("run-one", local.FirstRunName);
        Assert.True(local.CanExecuteSync);
        Assert.Contains("Upload: 1 run(s)", local.SummaryDisplay);

        Assert.Equal(local, sftp);
        Assert.Equal(local, drive);
    }

    [Fact]
    public void SharedSyncPath_ContainsNoProviderSpecificBranch()
    {
        string source = ReadSyncViewModelSource();

        int start = source.IndexOf(
            "private async Task PreviewSyncAsync()",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            "private static string FormatBytes(",
            StringComparison.Ordinal);

        Assert.True(start >= 0, "The preview method was not found.");
        Assert.True(end > start, "The shared sync region was not found.");

        // Preview, execution, history, and the sync log are contiguous in the
        // view model, so one slice covers all four.
        string sharedPath = source[start..end];

        // Non-vacuity: this really is that region, not an empty or mis-sliced
        // string that would satisfy every absence assertion by accident.
        Assert.Contains("CreateConfiguredProvider()", sharedPath, StringComparison.Ordinal);
        Assert.Contains("ValidateProviderSelection()", sharedPath, StringComparison.Ordinal);
        Assert.Contains("CreatePreviewAsync(", sharedPath, StringComparison.Ordinal);
        Assert.Contains("_lastProvider.ExecuteAsync(", sharedPath, StringComparison.Ordinal);
        Assert.Contains("provider.GetSyncLogAsync()", sharedPath, StringComparison.Ordinal);
        Assert.Contains("TryUpdateLastSuccessfulConnection()", sharedPath, StringComparison.Ordinal);

        // Every provider difference is resolved before this region runs, by
        // ValidateProviderSelection and CreateConfiguredProvider.
        Assert.DoesNotContain("SyncProviderKind.", sharedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleDrive", sharedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Sftp", sharedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalFolder", sharedPath, StringComparison.Ordinal);
    }

    // ---- 4c: newly visible state carries no identifier and no token ----

    [Fact]
    public async Task ActivatedDriveUi_NeverExposesTheRootFolderIdentifier()
    {
        (SyncViewModel viewModel, _) = await CreateConnectedDriveViewModelAsync();
        await RunSharedPathAsync(viewModel);

        // Non-vacuity: the identifier really is held by the state the UI binds
        // against, so a leak would have somewhere to leak from.
        Assert.Equal(
            RootFolderIdMarker,
            viewModel.SelectedRemoteProfile!.RemoteFolderId);

        Assert.Empty(FindMarker(viewModel, RootFolderIdMarker));
    }

    [Fact]
    public async Task ActivatedDriveUi_ConfinesTheAccountAddressToThePreExistingAccountFields()
    {
        (SyncViewModel viewModel, _) = await CreateConnectedDriveViewModelAsync();
        await RunSharedPathAsync(viewModel);

        Assert.Equal(
            AccountEmailMarker,
            Assert.IsType<GoogleDriveSyncRemoteSettings>(
                viewModel.SelectedRemoteProfile!.ProviderSettings).AccountEmail);

        // The account address is deliberately shown so a user can tell which
        // account is connected. Both fields that carry it predate this
        // milestone: Milestone P added the observable source property and the
        // one label the view binds, and V changed neither. Nothing else may
        // carry it, and in particular none of the sync state that activation
        // newly exposed.
        Assert.Equal(
            new[]
            {
                nameof(SyncViewModel.GoogleDriveAccountEmail),
                nameof(SyncViewModel.GoogleDriveEmailDisplayText)
            },
            FindMarker(viewModel, AccountEmailMarker).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void StoredAuthentication_ReachesTheViewModelAsAFlagRatherThanAToken()
    {
        // There is no token value to redact because none is ever handed to the
        // presentation layer: the authentication result reports only whether a
        // token is stored.
        PropertyInfo stored = Assert.Single(
            typeof(GoogleDriveConnectionSettings).GetProperties(),
            property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(nameof(GoogleDriveConnectionSettings.HasStoredToken), stored.Name);
        Assert.Equal(typeof(bool), stored.PropertyType);

        Assert.DoesNotContain(
            typeof(SyncViewModel).GetProperties(),
            property => property.PropertyType == typeof(string) &&
                        property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    // ---- helpers ----

    /// <summary>
    /// The bound state a sync run produces, with nothing provider-specific in
    /// it. Two providers that agree on all of this reached it through the same
    /// code.
    /// </summary>
    private sealed record SharedRunState(
        int ItemCount,
        string? FirstRunName,
        string SummaryDisplay,
        bool CanExecuteSync,
        int WarningCount,
        int SyncLogCount,
        int ExecutionResultCount,
        string ExecutionStatusMessage,
        string ProgressText);

    private static async Task<SharedRunState> RunSharedPathAsync(SyncViewModel viewModel)
    {
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        viewModel.ConfirmSync = true;

        await viewModel.ExecuteSyncCommand.ExecuteAsync(null);

        return new SharedRunState(
            viewModel.Items.Count,
            viewModel.Items.FirstOrDefault()?.RunName,
            viewModel.SummaryDisplay,
            viewModel.CanExecuteSync,
            viewModel.Warnings.Count,
            viewModel.SyncLog.Count,
            viewModel.ExecutionResults.Count,
            viewModel.ExecutionStatusMessage,
            viewModel.ProgressText);
    }

    /// <summary>
    /// Names every readable string property of the view model, and of every row
    /// in its bound collections, whose value contains <paramref name="marker"/>.
    /// </summary>
    private static IReadOnlyList<string> FindMarker(SyncViewModel viewModel, string marker)
    {
        var hits = new List<string>();

        CollectMarker(viewModel, marker, hits);

        foreach (object row in viewModel.Items
                     .Cast<object>()
                     .Concat(viewModel.Warnings)
                     .Concat(viewModel.ExecutionResults)
                     .Concat(viewModel.SyncLog))
        {
            CollectMarker(row, marker, hits);
        }

        return hits;
    }

    private static void CollectMarker(object instance, string marker, List<string> hits)
    {
        foreach (PropertyInfo property in instance.GetType().GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(string) ||
                !property.CanRead ||
                property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.GetValue(instance) is string value &&
                value.Contains(marker, StringComparison.Ordinal))
            {
                hits.Add(property.Name);
            }
        }
    }

    private static void ConfigureSftp(SyncViewModel viewModel)
    {
        viewModel.SftpHost = SftpHost;
        viewModel.SftpPort = "2222";
        viewModel.SftpUsername = "alice";
        viewModel.SftpPassword = "session-only";
        viewModel.SftpRemotePath = "/srv/game-saves";
    }

    private static SyncViewModel CreateLocalFolderViewModel()
    {
        SyncViewModel viewModel = CreateViewModel(
            new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new InMemorySyncRemoteProfileRepository(),
            new StubGoogleDriveOAuthService(),
            new StubGoogleDriveRootFolderService(),
            SyncUiSettings.Default);

        viewModel.RemoteRootPath = LocalFolderPath;
        return viewModel;
    }

    private static SyncViewModel CreateSftpViewModel()
    {
        SyncViewModel viewModel = CreateViewModel(
            new SyncProviderSelectionTests.RecordingSyncProviderFactory(),
            new InMemorySyncRemoteProfileRepository(),
            new StubGoogleDriveOAuthService(),
            new StubGoogleDriveRootFolderService(),
            SyncUiSettings.Default);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        ConfigureSftp(viewModel);
        return viewModel;
    }

    private static async Task<(SyncViewModel ViewModel,
        SyncProviderSelectionTests.RecordingSyncProviderFactory Factory)>
        CreateConnectedDriveViewModelAsync()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = repository.Create(new SyncRemoteProfile(
            Guid.NewGuid(),
            "parity-profile",
            SyncProviderKind.GoogleDrive,
            "Example User",
            GoogleDriveApplicationRoot.DisplayName,
            new GoogleDriveSyncRemoteSettings(
                AccountEmailMarker,
                GoogleDriveAuthorizationScopes.DriveFile),
            Now,
            Now,
            null,
            Now,
            RootFolderIdMarker));

        var oauth = new StubGoogleDriveOAuthService
        {
            ConfigurationState = new GoogleDriveOAuthClientConfigurationState(
                GoogleDriveOAuthClientConfigurationStatus.Available),
            RestoreResult = Connected(profile),
            ConnectResult = Connected(profile),
            ReconnectResult = Connected(profile)
        };

        var roots = new StubGoogleDriveRootFolderService
        {
            InspectResult = new GoogleDriveRootFolderResult(
                GoogleDriveRootFolderStatus.Ready,
                profile.Id,
                RootFolderIdMarker,
                GoogleDriveApplicationRoot.DisplayName,
                WasValidatedById: true,
                Message: "The Google Drive backup folder is ready.")
        };

        SyncUiSettings settings = SyncUiSettings.Default with
        {
            SelectedProviderKind = SyncProviderKind.GoogleDrive,
            SelectedRemoteProfileId = profile.Id
        };

        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(
            factory, repository, oauth, roots, settings);

        await viewModel.GoogleAuthenticationInitializationTask;
        await viewModel.GoogleRootFolderInitializationTask;

        return (viewModel, factory);
    }

    private static GoogleDriveAuthenticationResult Connected(SyncRemoteProfile profile) =>
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

    private static SyncViewModel CreateViewModel(
        SyncProviderSelectionTests.RecordingSyncProviderFactory factory,
        InMemorySyncRemoteProfileRepository repository,
        StubGoogleDriveOAuthService oauth,
        StubGoogleDriveRootFolderService roots,
        SyncUiSettings settings) =>
        new(
            factory,
            new SyncProviderCatalog(),
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(settings),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(settings),
            new FixedUtcClock(Now),
            oauth,
            roots);

    private static string ReadSyncViewModelSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                return File.ReadAllText(Path.Combine(
                    directory.FullName,
                    "GameSaves.App",
                    "ViewModels",
                    "SyncViewModel.cs"));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
