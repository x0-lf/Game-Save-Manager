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
    public void OnlyLocalFolderAndSftp_AreImplemented_GoogleIsConfigurationVisible()
    {
        Assert.Equal(
            new[] { SyncProviderKind.LocalFolder, SyncProviderKind.Sftp },
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
    [InlineData(SyncProviderKind.GoogleDrive)]
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
    public async Task UnavailableCapabilities_DoNotEnableActionsOrExecution()
    {
        SyncViewModel viewModel = CreateViewModel();
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        Assert.True(viewModel.RequiresInteractiveLogin);
        Assert.True(viewModel.SupportsPersistentAuthentication);
        Assert.True(viewModel.SupportsConnectionTesting);
        Assert.False(viewModel.CanCheckConnection);
        Assert.False(viewModel.CanLogout);
        Assert.False(viewModel.CanOpenRemoteLocation);
        Assert.False(viewModel.CanShowQuota);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.False(viewModel.CanExecuteSync);
        Assert.Contains("later milestones", viewModel.StatusMessage);
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
    public async Task GoogleDriveStaysUnreachableWhileTheCatalogCallsItUnimplemented()
    {
        // Milestone V Task 2 adds the selection case but must not activate the
        // provider; Task 3 flips the catalog. Until then no path may construct
        // a Drive provider, so an interrupted sequence cannot ship a reachable
        // failure.
        var factory = new SyncProviderSelectionTests.RecordingSyncProviderFactory();
        SyncViewModel viewModel = CreateViewModel(factory);
        viewModel.SelectedProviderKind = SyncProviderKind.GoogleDrive;

        Assert.False(
            _catalog.GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        Assert.False(viewModel.CanPreviewSync);

        await viewModel.PreviewSyncCommand.ExecuteAsync(null);

        Assert.Equal(0, factory.GoogleDriveCreateCount);
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
            new StubGoogleDriveOAuthService());
    }
}
