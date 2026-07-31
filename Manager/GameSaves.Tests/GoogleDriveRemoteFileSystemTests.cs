using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRemoteFileSystemTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("24b45199-8c22-45b8-a2f2-f80b1d63385c");

    public static IEnumerable<object[]> InvalidValidationStatuses() =>
        Enum.GetValues<GoogleDriveRemoteValidationStatus>()
            .Where(status => status != GoogleDriveRemoteValidationStatus.Valid)
            .Select(status => new object[] { (int)status });

    [Fact]
    public void Factory_CreatesDistinctProfileScopedFileSystems()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService());

        IRemoteFileSystem first = factory.Create(ProfileId);
        IRemoteFileSystem second = factory.Create(ProfileId);

        Assert.IsType<GoogleDriveRemoteFileSystem>(first);
        Assert.IsType<GoogleDriveRemoteFileSystem>(second);
        Assert.NotSame(first, second);
        Assert.Equal("GameSave Manager Backups", first.DisplayRoot);
        Assert.Equal("GameSave Manager Backups/nested/run", first.GetDisplayPath("nested/run"));
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public async Task Factory_PreservesTheSelectedProfileIdentity()
    {
        Guid secondProfileId =
            Guid.Parse("b9a241af-e03c-4e80-a9c1-078642fd54c6");
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        repository.Create(Profile() with
        {
            Id = secondProfileId,
            DisplayName = "Second Google Drive profile"
        });
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService());

        await factory.Create(ProfileId).ValidateAsync();
        await factory.Create(secondProfileId).ValidateAsync();

        Assert.Equal(new[] { ProfileId, secondProfileId }, validation.ProfileIds);
    }

    [Fact]
    public void Factory_FallsBackWhenDisplayMetadataCouldExposeAnIdOrEmail()
    {
        const string rootId = "private-root-id-marker";
        const string accountEmail = "user@example.invalid";
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile() with
        {
            RemoteFolderId = rootId,
            RemoteRootDisplayName =
                $"Backups {rootId} for {accountEmail}",
            ProviderSettings = new GoogleDriveSyncRemoteSettings(
                accountEmail,
                GoogleDriveAuthorizationScopes.DriveFile)
        });
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            new RecordingValidationService(),
            new RecordingRootExistenceService());

        IRemoteFileSystem remote = factory.Create(ProfileId);

        Assert.Equal("Google Drive", remote.DisplayRoot);
        Assert.DoesNotContain(rootId, remote.DisplayRoot, StringComparison.Ordinal);
        Assert.DoesNotContain(accountEmail, remote.DisplayRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_UsesGenericDisplayForMissingOrNonGoogleProfiles()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        var validation = new RecordingValidationService();
        var factory = new GoogleDriveRemoteFileSystemFactory(
            repository,
            validation,
            new RecordingRootExistenceService());

        IRemoteFileSystem missing = factory.Create(ProfileId);
        repository.Create(Profile() with
        {
            ProviderKind = SyncProviderKind.LocalFolder,
            RemoteRootDisplayName = @"C:\private\backups",
            ProviderSettings = new LocalFolderSyncRemoteSettings(
                @"C:\private\backups")
        });
        IRemoteFileSystem wrongProvider = factory.Create(ProfileId);

        Assert.Equal("Google Drive", missing.DisplayRoot);
        Assert.Equal("Google Drive", wrongProvider.DisplayRoot);
        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public void DependencyInjection_ResolvesFactoryWithoutRemoteWork()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(Profile());
        var validation = new RecordingValidationService();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.RemoveAll<IGoogleDriveRemoteValidationService>();
        services.AddSingleton<ISyncRemoteProfileRepository>(repository);
        services.AddSingleton<IGoogleDriveRemoteValidationService>(validation);

        using ServiceProvider provider = services.BuildServiceProvider();
        IGoogleDriveRemoteFileSystemFactory factory =
            provider.GetRequiredService<IGoogleDriveRemoteFileSystemFactory>();
        Assert.IsType<GoogleDriveRootExistenceService>(
            provider.GetRequiredService<IGoogleDriveRootExistenceService>());
        IRemoteFileSystem remote = factory.Create(ProfileId);

        Assert.IsType<GoogleDriveRemoteFileSystemFactory>(factory);
        Assert.IsType<GoogleDriveRemoteFileSystem>(remote);
        Assert.Equal(0, validation.Calls);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType ==
                          typeof(GoogleDriveRemoteFileSystem));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNullOnlyForValidResult()
    {
        var validation = new RecordingValidationService
        {
            Result = GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid)
        };
        IRemoteFileSystem remote = Remote(validation);

        TransferPreviewWarning? warning = await remote.ValidateAsync();

        Assert.Null(warning);
        Assert.Equal(1, validation.Calls);
        Assert.Equal(ProfileId, validation.ProfileIds.Single());
    }

    [Theory]
    [MemberData(nameof(InvalidValidationStatuses))]
    public async Task ValidateAsync_ReturnsCentralSafeWarningForEveryInvalidState(
        int statusValue)
    {
        var status = (GoogleDriveRemoteValidationStatus)statusValue;
        var validation = new RecordingValidationService
        {
            Result = GoogleDriveRemoteValidationMapper.FromStatus(
                status,
                "private-root-id-marker")
        };
        IRemoteFileSystem remote = Remote(validation);

        TransferPreviewWarning warning = Assert.IsType<TransferPreviewWarning>(
            await remote.ValidateAsync());
        TransferPreviewWarning expected = Assert.IsType<TransferPreviewWarning>(
            GoogleDriveRemoteValidationMapper.ToTransferPreviewWarning(
                validation.Result));

        Assert.Equal(expected, warning);
        Assert.Equal(1, validation.Calls);
        Assert.DoesNotContain("private-root-id-marker", warning.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", warning.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_ForwardsCancellationWithoutDoingOtherWork()
    {
        var validation = new RecordingValidationService
        {
            Handler = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    GoogleDriveRemoteValidationMapper.FromStatus(
                        GoogleDriveRemoteValidationStatus.Valid));
            }
        };
        IRemoteFileSystem remote = Remote(validation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => remote.ValidateAsync(cancellation.Token));

        Assert.Equal(1, validation.Calls);
        Assert.Equal(cancellation.Token, validation.CancellationTokens.Single());
    }

    [Fact]
    public async Task RootExistsAsync_DelegatesTheSelectedProfileAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var existence = new RecordingRootExistenceService { Result = true };
        IRemoteFileSystem remote = Remote(
            new RecordingValidationService(),
            existence);

        bool exists = await remote.RootExistsAsync(cancellation.Token);

        Assert.True(exists);
        Assert.Equal(new[] { ProfileId }, existence.ProfileIds);
        Assert.Equal(cancellation.Token, existence.CancellationTokens.Single());
    }

    [Fact]
    public async Task EveryLaterOperation_FailsExplicitlyWithoutValidationOrDriveWork()
    {
        var validation = new RecordingValidationService();
        IRemoteFileSystem remote = Remote(validation);
        var operations = new (string Name, Func<Task> Invoke)[]
        {
            (nameof(IRemoteFileSystem.ListRunFolderNamesAsync),
                async () => await remote.ListRunFolderNamesAsync()),
            (nameof(IRemoteFileSystem.FolderExistsAsync),
                async () => await remote.FolderExistsAsync("run")),
            (nameof(IRemoteFileSystem.ReadTextFileAsync),
                async () => await remote.ReadTextFileAsync("run/manifest.json")),
            (nameof(IRemoteFileSystem.CreateTextFileIfMissingAsync),
                () => remote.CreateTextFileIfMissingAsync(
                    "run/manifest.json",
                    "{}")),
            (nameof(IRemoteFileSystem.ReadProviderMetadataAsync),
                async () => await remote.ReadProviderMetadataAsync(
                    ".gamesave-sync/sync-log.json")),
            (nameof(IRemoteFileSystem.ReplaceProviderMetadataAsync),
                () => remote.ReplaceProviderMetadataAsync(
                    ".gamesave-sync/sync-log.json",
                    "[]")),
            (nameof(IRemoteFileSystem.ListFilesAsync),
                async () => await remote.ListFilesAsync("run")),
            (nameof(IRemoteFileSystem.UploadFileAsync),
                async () => await remote.UploadFileAsync(
                    "local.sav",
                    "run/save.sav")),
            (nameof(IRemoteFileSystem.DownloadFileAsync),
                async () => await remote.DownloadFileAsync(
                    "run/save.sav",
                    "local.sav"))
        };

        foreach ((string name, Func<Task> invoke) in operations)
        {
            NotSupportedException exception =
                await Assert.ThrowsAsync<NotSupportedException>(invoke);
            Assert.Equal(
                GoogleDriveRemoteFileSystem.OperationsUnavailableMessage,
                exception.Message);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        Assert.Equal(0, validation.Calls);
    }

    [Fact]
    public void FileSystem_HasOnlyNarrowValidationAndRootExistenceDependencies()
    {
        Type[] fieldTypes = typeof(GoogleDriveRemoteFileSystem)
            .GetFields(BindingFlags.Instance |
                       BindingFlags.NonPublic |
                       BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Contains(typeof(IGoogleDriveRemoteValidationService), fieldTypes);
        Assert.Contains(typeof(IGoogleDriveRootExistenceService), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveObjectPathResolver), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveObjectApi), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveRootFolderApi), fieldTypes);
        Assert.DoesNotContain(typeof(IGoogleDriveRootValidationApi), fieldTypes);
        Assert.DoesNotContain(
            fieldTypes,
            type => type.Namespace?.StartsWith("Google.", StringComparison.Ordinal)
                    == true);
    }

    [Fact]
    public void ProviderActivation_RemainsUnavailable()
    {
        var catalog = new SyncProviderCatalog();
        SyncProviderDescriptor google =
            catalog.GetDescriptor(SyncProviderKind.GoogleDrive);
        Type[] googleTypes = typeof(GoogleDriveRemoteFileSystem).Assembly
            .GetTypes()
            .Where(type => string.Equals(
                type.Namespace,
                "GameSaves.Infrastructure.GoogleDrive",
                StringComparison.Ordinal))
            .ToArray();

        Assert.False(google.IsImplemented);
        Assert.DoesNotContain(
            typeof(SyncProviderFactory).GetMethods(),
            method => method.Name.Contains("Google", StringComparison.Ordinal));
        Assert.DoesNotContain(
            googleTypes,
            type => type.Name == "GoogleDriveSyncProvider");
    }

    private static IRemoteFileSystem Remote(
        RecordingValidationService validation,
        RecordingRootExistenceService? rootExistence = null) =>
        new GoogleDriveRemoteFileSystem(
            ProfileId,
            "GameSave Manager Backups",
            validation,
            rootExistence ?? new RecordingRootExistenceService());

    private static SyncRemoteProfile Profile() =>
        new(
            ProfileId,
            "Google Drive profile",
            SyncProviderKind.GoogleDrive,
            AccountDisplayName: "Example User",
            RemoteRootDisplayName: "GameSave Manager Backups",
            ProviderSettings: new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            CreatedUtc: DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-07-31T10:00:00Z"),
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: "private-root-id-marker");

    private sealed class RecordingValidationService
        : IGoogleDriveRemoteValidationService
    {
        public GoogleDriveRemoteValidationResult Result { get; set; } =
            GoogleDriveRemoteValidationMapper.FromStatus(
                GoogleDriveRemoteValidationStatus.Valid);

        public Func<
            Guid,
            CancellationToken,
            Task<GoogleDriveRemoteValidationResult>>? Handler { get; set; }

        public int Calls { get; private set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<GoogleDriveRemoteValidationResult> ValidateAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ProfileIds.Add(remoteProfileId);
            CancellationTokens.Add(cancellationToken);
            return Handler is null
                ? Task.FromResult(Result)
                : Handler(remoteProfileId, cancellationToken);
        }
    }

    private sealed class RecordingRootExistenceService
        : IGoogleDriveRootExistenceService
    {
        public bool Result { get; set; }

        public List<Guid> ProfileIds { get; } = new();

        public List<CancellationToken> CancellationTokens { get; } = new();

        public Task<bool> ExistsAsync(
            Guid remoteProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfileIds.Add(remoteProfileId);
            CancellationTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
}
