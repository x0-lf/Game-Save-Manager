using GameSaves.App.Services;
using GameSaves.App.ViewModels;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

public sealed class SyncProviderCapabilityTests
{
    private readonly SyncProviderCatalog _catalog = new();

    [Fact]
    public void EveryStableProviderKind_HasExactlyOneDescriptor()
    {
        SyncProviderKind[] kinds = Enum.GetValues<SyncProviderKind>();
        IReadOnlyList<SyncProviderDescriptor> descriptors = _catalog.GetAll();

        Assert.Equal(kinds.Length, descriptors.Count);
        Assert.All(kinds, kind =>
            Assert.Single(descriptors, descriptor => descriptor.Kind == kind));
    }

    [Fact]
    public void UnknownNumericKind_ReturnsSafeUnknownDescriptor()
    {
        SyncProviderDescriptor descriptor =
            _catalog.GetDescriptor((SyncProviderKind)9876);

        Assert.Equal(SyncProviderKind.Unknown, descriptor.Kind);
        Assert.False(descriptor.IsImplemented);
        Assert.Equal(SyncProviderCapabilities.None, descriptor.Capabilities);
        Assert.NotNull(descriptor.UnavailableMessage);
    }

    [Fact]
    public void LocalFolderSftpAndGoogleDrive_AreImplemented()
    {
        Assert.Equal(
            new[]
            {
                SyncProviderKind.LocalFolder,
                SyncProviderKind.Sftp,
                SyncProviderKind.GoogleDrive
            },
            _catalog.GetAll()
                .Where(descriptor => descriptor.IsImplemented)
                .Select(descriptor => descriptor.Kind));

        SyncViewModel viewModel = CreateViewModel();
        Assert.Equal(
            new[]
            {
                SyncProviderKind.LocalFolder,
                SyncProviderKind.Sftp,
                SyncProviderKind.GoogleDrive
            },
            viewModel.ProviderOptions.Select(option => option.Kind));

        Assert.True(_catalog.GetDescriptor(SyncProviderKind.GoogleDrive).IsConfigurationAvailable);
        Assert.False(_catalog.GetDescriptor(SyncProviderKind.WebDav).IsConfigurationAvailable);
        Assert.False(_catalog.GetDescriptor(SyncProviderKind.OneDrive).IsConfigurationAvailable);
    }

    [Fact]
    public void LocalFolderCapabilities_AreConservativeAndExact()
    {
        SyncProviderDescriptor descriptor =
            _catalog.GetDescriptor(SyncProviderKind.LocalFolder);

        Assert.True(descriptor.IsImplemented);
        Assert.Equal(
            new SyncProviderCapabilities(
                RequiresInteractiveLogin: false,
                RequiresServerCredentials: false,
                SupportsResumableUpload: false,
                SupportsRemoteQuota: false,
                SupportsRemoteFolderSelection: true,
                SupportsPersistentAuthentication: false,
                SupportsConnectionTesting: true,
                SupportsLogout: false,
                SupportsOpenRemoteLocation: true),
            descriptor.Capabilities);
    }

    [Fact]
    public void SftpCapabilities_AreConservativeAndExact()
    {
        SyncProviderDescriptor descriptor =
            _catalog.GetDescriptor(SyncProviderKind.Sftp);

        Assert.True(descriptor.IsImplemented);
        Assert.Equal(
            new SyncProviderCapabilities(
                RequiresInteractiveLogin: false,
                RequiresServerCredentials: true,
                SupportsResumableUpload: false,
                SupportsRemoteQuota: false,
                SupportsRemoteFolderSelection: false,
                SupportsPersistentAuthentication: false,
                SupportsConnectionTesting: true,
                SupportsLogout: false,
                SupportsOpenRemoteLocation: false),
            descriptor.Capabilities);
    }

    [Theory]
    [InlineData(SyncProviderKind.OneDrive)]
    public void PlannedCloudCapabilities_AreDeclaredButUnavailable(
        SyncProviderKind kind)
    {
        SyncProviderDescriptor descriptor = _catalog.GetDescriptor(kind);

        Assert.False(descriptor.IsImplemented);
        Assert.Equal(
            new SyncProviderCapabilities(
                RequiresInteractiveLogin: true,
                RequiresServerCredentials: false,
                SupportsResumableUpload: true,
                SupportsRemoteQuota: true,
                SupportsRemoteFolderSelection: true,
                SupportsPersistentAuthentication: true,
                SupportsConnectionTesting: true,
                SupportsLogout: true,
                SupportsOpenRemoteLocation: true),
            descriptor.Capabilities);
    }

    [Fact]
    public void PlannedWebDavCapabilities_AreConservativeButUnavailable()
    {
        SyncProviderDescriptor descriptor =
            _catalog.GetDescriptor(SyncProviderKind.WebDav);

        Assert.False(descriptor.IsImplemented);
        Assert.Equal(
            new SyncProviderCapabilities(
                RequiresInteractiveLogin: false,
                RequiresServerCredentials: true,
                SupportsResumableUpload: false,
                SupportsRemoteQuota: false,
                SupportsRemoteFolderSelection: false,
                SupportsPersistentAuthentication: true,
                SupportsConnectionTesting: true,
                SupportsLogout: true,
                SupportsOpenRemoteLocation: true),
            descriptor.Capabilities);
    }

    [Fact]
    public async Task ImplementedCapabilities_ReachTheUiWithoutGrantingExecution()
    {
        SyncViewModel viewModel = CreateViewModel();
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        // Milestone V activated the provider, so the declared capabilities now
        // reach the UI instead of being suppressed by IsImplemented.
        Assert.True(viewModel.RequiresInteractiveLogin);
        Assert.True(viewModel.SupportsPersistentAuthentication);
        Assert.True(viewModel.SupportsConnectionTesting);
        Assert.True(viewModel.CanCheckConnection);
        Assert.True(viewModel.CanLogout);
        Assert.True(viewModel.CanOpenRemoteLocation);
        Assert.True(viewModel.CanShowQuota);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // Capability is not permission: with no saved profile the run is still
        // refused and nothing is executable.
        Assert.False(viewModel.CanExecuteSync);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public void GoogleDriveCapabilities_AreUnchangedByActivation()
    {
        // Milestone V flipped IsImplemented and dropped the unavailable
        // message. Nothing else about the descriptor was allowed to move, and
        // until this test existed nothing pinned that: activation removed
        // Google Drive from the planned-cloud theory that had been comparing
        // the full record, and no replacement comparison took its place.
        SyncProviderDescriptor descriptor =
            _catalog.GetDescriptor(SyncProviderKind.GoogleDrive);

        Assert.True(descriptor.IsImplemented);
        Assert.Null(descriptor.UnavailableMessage);
        Assert.True(descriptor.IsConfigurationAvailable);
        Assert.Equal(
            SyncProviderConfigurationSurface.InteractiveOAuth,
            descriptor.ConfigurationSurface);
        Assert.Equal("Google Drive", descriptor.DisplayName);
        Assert.Equal(
            new SyncProviderCapabilities(
                RequiresInteractiveLogin: true,
                RequiresServerCredentials: false,
                SupportsResumableUpload: true,
                SupportsRemoteQuota: true,
                SupportsRemoteFolderSelection: true,
                SupportsPersistentAuthentication: true,
                SupportsConnectionTesting: true,
                SupportsLogout: true,
                SupportsOpenRemoteLocation: true),
            descriptor.Capabilities);

        // The same record OneDrive still declares, which is what "unchanged by
        // activation" means: the flags describe the provider, not its state.
        Assert.Equal(
            _catalog.GetDescriptor(SyncProviderKind.OneDrive).Capabilities,
            descriptor.Capabilities);
    }

    [Fact]
    public void ActivatedDriveCapabilities_OfferNoControlTheCodeCannotHonour()
    {
        // Two declared capabilities describe features Milestone V does not
        // implement: remote quota and opening the remote location. Activation
        // makes both properties true, so the guarantee that matters is that
        // neither reaches a control a user can press.
        SyncViewModel viewModel = CreateViewModel();
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        Assert.True(viewModel.CanShowQuota);
        Assert.True(viewModel.CanOpenRemoteLocation);

        // No quota control is bound at all, and the Open Folder button lives
        // inside the Local folder panel, so Google Drive never shows it.
        string view = ReadSyncView();
        Assert.DoesNotContain("CanShowQuota", view, StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding CanOpenRemoteLocation}\"",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsVisible=\"{Binding IsLocalFolderSelected}\"",
            view,
            StringComparison.Ordinal);

        // And the command refuses anyway, with a sanitized message, so the
        // guarantee does not rest on layout alone.
        viewModel.OpenRemoteLocationCommand.Execute(null);

        Assert.Equal(
            "Opening the selected provider location is unavailable.",
            viewModel.StatusMessage);
    }

    [Fact]
    public void GenericViewModelProperties_FollowCapabilities()
    {
        SyncViewModel viewModel = CreateViewModel();

        Assert.True(viewModel.CanSelectRemoteFolder);
        Assert.True(viewModel.CanCheckConnection);
        Assert.True(viewModel.CanOpenRemoteLocation);
        Assert.False(viewModel.RequiresServerCredentials);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;

        Assert.False(viewModel.CanSelectRemoteFolder);
        Assert.True(viewModel.CanCheckConnection);
        Assert.False(viewModel.CanOpenRemoteLocation);
        Assert.True(viewModel.RequiresServerCredentials);
    }

    // ---- Milestone V Task 2: Google Drive selection and validation ----

    [Fact]
    public async Task GoogleDriveWithNoSavedProfile_IsRefusedAndBuildsNoProvider()
    {
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        // The refusal happens before construction, so the factory is untouched
        // and no profile ID was dereferenced.
        Assert.Equal(0, factory.GoogleDriveCreateCount);
        Assert.Null(factory.LastGoogleDriveProfileId);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public async Task ActivatedGoogleDrive_StillBuildsNothingWithoutASavedProfile()
    {
        // Milestone V Task 3 activated the provider. Since the UI revamp,
        // preview is not even offered without a usable saved profile - and
        // the saved-profile guard beneath still stops construction if the
        // command is somehow invoked anyway.
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        Assert.True(
            _catalog.GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        Assert.False(viewModel.CanPreviewSync);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(0, factory.GoogleDriveCreateCount);
        Assert.False(viewModel.CanExecuteSync);
    }

    [Fact]
    public async Task LocalFolderAndSftpSelectionAreUnchangedByTheDriveCase()
    {
        // Regression: adding a Drive case must not alter the two providers that
        // already worked.
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory);

        viewModel.SelectedProviderKind = SyncProviderKind.LocalFolder;
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        Assert.Equal(0, factory.GoogleDriveCreateCount);

        viewModel.SelectedProviderKind = SyncProviderKind.Sftp;
        await viewModel.PreviewSyncCommand.ExecuteAsync(null);
        Assert.Equal(0, factory.GoogleDriveCreateCount);

        Assert.True(_catalog.GetDescriptor(SyncProviderKind.LocalFolder).IsImplemented);
        Assert.True(_catalog.GetDescriptor(SyncProviderKind.Sftp).IsImplemented);
    }

    private static string ReadSyncView()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                return File.ReadAllText(Path.Combine(
                    directory.FullName,
                    "GameSaves.App",
                    "Views",
                    "SyncView.axaml"));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }

    private SyncViewModel CreateViewModel() =>
        CreateViewModel(new SyncProviderSelectionTests.RecordingSyncProviderFactory());

    private SyncViewModel CreateViewModel(
        SyncProviderSelectionTests.RecordingSyncProviderFactory factory)
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        return new SyncViewModel(
            factory,
            _catalog,
            new SyncProviderSelectionTests.NullFolderPickerService(),
            new SyncProviderSelectionTests.InMemorySyncSettingsStore(SyncUiSettings.Default),
            repository,
            new SyncRemoteProfileService(repository, new InMemorySecretStore()),
            new StubSyncRemoteProfileMigrationService(SyncUiSettings.Default),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")),
            new StubGoogleDriveOAuthService(),
            SyncProviderSelectionTests.NewWorkspaceLayout());
    }
}
