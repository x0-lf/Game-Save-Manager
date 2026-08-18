using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveSyncProviderFactoryTests
{
    private const string RootId = "private-root-id-marker";
    private const string AccountEmail = "user@example.invalid";
    private const string ProfileName = "Private profile name marker";
    private const string RootDisplayName = "Private root display marker";

    private static readonly Guid ProfileId =
        Guid.Parse("0d6f5f5e-6a2a-4a6f-9f0c-8f2f3c0f6b41");

    [Fact]
    public void Create_RefusesAnEmptyProfileIdBeforeAnyLookup()
    {
        var repository = new CountingProfileRepository();
        var factory = Factory(repository);

        Assert.Throws<ArgumentException>(() => factory.Create(Guid.Empty));
        Assert.Equal(0, repository.LookupCalls);
    }

    [Fact]
    public void Create_RefusesAnUnknownProfile()
    {
        var factory = Factory(new InMemorySyncRemoteProfileRepository());

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => factory.Create(ProfileId));

        Assert.Equal(
            GoogleDriveRemoteValidationStatus.ProfileNotFound,
            failure.Result.Status);
        Assert.Equal(
            GoogleDriveRemoteValidationErrorCodes.ProfileNotFound,
            failure.Result.ErrorCode);
    }

    [Theory]
    [InlineData((int)GoogleDriveRemoteValidationStatus.WrongProviderKind)]
    [InlineData((int)GoogleDriveRemoteValidationStatus.UnsupportedScope)]
    [InlineData((int)GoogleDriveRemoteValidationStatus.RootNotConfigured)]
    public void Create_RefusesAProfileThatCannotBeUsed(int expectedStatus)
    {
        var status = (GoogleDriveRemoteValidationStatus)expectedStatus;
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(UnusableProfile(status));
        var factory = Factory(repository);

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => factory.Create(ProfileId));

        Assert.Equal(status, failure.Result.Status);
        Assert.NotNull(failure.Result.ErrorCode);
    }

    [Fact]
    public void Create_ReusesTheSharedProfileValidatorRatherThanItsOwnRules()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        SyncRemoteProfile profile = UnusableProfile(
            GoogleDriveRemoteValidationStatus.UnsupportedScope);
        repository.Create(profile);
        var factory = Factory(repository);

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => factory.Create(ProfileId));

        GoogleDriveRemoteValidationResult? shared =
            GoogleDriveRemoteProfileValidator.Validate(profile);

        Assert.NotNull(shared);
        Assert.Equal(shared!.Status, failure.Result.Status);
        Assert.Equal(shared.ErrorCode, failure.Result.ErrorCode);
    }

    [Fact]
    public void Create_BuildsAProviderForAUsableProfile()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(UsableProfile());
        var fileSystems = new RecordingRemoteFileSystemFactory();
        var factory = new GoogleDriveSyncProviderFactory(
            repository,
            fileSystems,
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository());

        using ISyncProvider provider = factory.Create(ProfileId);

        Assert.Equal("Google Drive", provider.ProviderName);
        Assert.Equal(ProfileId, Assert.Single(fileSystems.RequestedProfileIds));
    }

    [Fact]
    public void Create_BuildsOneProviderPerCallWithoutRemoteWork()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(UsableProfile());
        var fileSystems = new RecordingRemoteFileSystemFactory();
        var factory = new GoogleDriveSyncProviderFactory(
            repository,
            fileSystems,
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository());

        using ISyncProvider first = factory.Create(ProfileId);
        using ISyncProvider second = factory.Create(ProfileId);

        Assert.NotSame(first, second);
        Assert.Equal(2, fileSystems.RequestedProfileIds.Count);
        Assert.Empty(fileSystems.FileSystem.Calls);
    }

    [Fact]
    public void RefusedProfiles_ExposeNoPrivateValue()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(UnusableProfile(
            GoogleDriveRemoteValidationStatus.RootNotConfigured));
        var factory = Factory(repository);

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => factory.Create(ProfileId));

        string[] surfaces =
        [
            failure.Message,
            failure.ToString(),
            failure.Result.UserMessage,
            failure.Result.ToSafeDiagnosticString()
        ];

        foreach (string surface in surfaces)
        {
            Assert.DoesNotContain(RootId, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(
                AccountEmail, surface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileName, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(
                RootDisplayName, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(
                ProfileId.ToString(), surface, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void DependencyInjection_ResolvesTheFactoryWithoutRemoteWork()
    {
        var repository = new CountingProfileRepository();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.AddSingleton<ISyncRemoteProfileRepository>(repository);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IGoogleDriveSyncProviderFactory>();

        Assert.IsType<GoogleDriveSyncProviderFactory>(factory);
        Assert.Same(
            factory,
            provider.GetRequiredService<IGoogleDriveSyncProviderFactory>());
        Assert.Equal(0, repository.LookupCalls);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
    }

    [Fact]
    public void CoreFactory_DelegatesGoogleDriveConstructionToTheInternalFactory()
    {
        var repository = new CountingProfileRepository();
        var services = new ServiceCollection();
        services.AddGameSavesInfrastructure();
        services.RemoveAll<ISyncRemoteProfileRepository>();
        services.AddSingleton<ISyncRemoteProfileRepository>(repository);

        using ServiceProvider provider = services.BuildServiceProvider();
        var coreFactory = provider.GetRequiredService<ISyncProviderFactory>();

        // The empty-ID rejection lives only in the internal factory, so seeing
        // it here proves the Core seam reached it rather than answering itself.
        Assert.Throws<ArgumentException>(
            () => coreFactory.CreateGoogleDriveProvider(Guid.Empty));

        // Delegation, not reimplementation: no profile lookup happened, and the
        // Core factory still resolves as the concrete infrastructure type.
        Assert.Equal(0, repository.LookupCalls);
        Assert.IsType<SyncProviderFactory>(coreFactory);
        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
    }

    [Fact]
    public void CoreFactory_HoldsNoServiceProviderAndKeepsItsDependencyExplicit()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(SyncProviderFactory).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.False(constructor.IsPublic);
        Assert.Contains(
            constructor.GetParameters(),
            parameter =>
                parameter.ParameterType == typeof(IGoogleDriveSyncProviderFactory));
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IServiceProvider));
    }

    // Milestone U replaced the Milestone T guard that asserted the Core factory
    // had no Google Drive method at all. That became false by design, so the
    // guard now pins the exact shape of the one case it is allowed to gain,
    // and that gaining it still activates nothing.
    [Fact]
    public void CoreProviderFactory_ExposesExactlyOneGoogleDriveCaseOfTheAgreedShape()
    {
        MethodInfo driveMethod = Assert.Single(
            typeof(ISyncProviderFactory).GetMethods(),
            method => method.Name.Contains(
                "GoogleDrive", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("CreateGoogleDriveProvider", driveMethod.Name);
        Assert.Equal(typeof(ISyncProvider), driveMethod.ReturnType);

        ParameterInfo parameter = Assert.Single(driveMethod.GetParameters());
        Assert.Equal(typeof(Guid), parameter.ParameterType);

        // A saved profile ID is the only thing that may cross. No connection
        // settings record and no provider-specific type may follow it.
        Assert.Equal("remoteProfileId", parameter.Name);
    }

    [Fact]
    public void CoreProviderFactory_StillExposesNoGoogleTypeAfterActivation()
    {
        IEnumerable<Type> surfaceTypes = typeof(ISyncProviderFactory)
            .GetMethods()
            .SelectMany(method => method
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType));

        Assert.DoesNotContain(
            surfaceTypes,
            type => type.FullName?.Contains("Google", StringComparison.Ordinal) == true);

        Assert.True(new SyncProviderCatalog()
            .GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
    }

    // ---- Milestone U Task 4: the Core seam adds nothing and hides nothing ----

    [Theory]
    [InlineData((int)GoogleDriveRemoteValidationStatus.WrongProviderKind)]
    [InlineData((int)GoogleDriveRemoteValidationStatus.UnsupportedScope)]
    [InlineData((int)GoogleDriveRemoteValidationStatus.RootNotConfigured)]
    public void CoreSeam_RefusesAnUnusableProfileIdenticallyToTheInternalFactory(
        int expectedStatus)
    {
        var status = (GoogleDriveRemoteValidationStatus)expectedStatus;

        GoogleDriveRemoteOperationException viaInternal =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => Factory(RepositoryWith(UnusableProfile(status)))
                    .Create(ProfileId));

        GoogleDriveRemoteOperationException viaCore =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => CoreFactory(RepositoryWith(UnusableProfile(status)))
                    .CreateGoogleDriveProvider(ProfileId));

        AssertIdenticalRefusal(viaInternal, viaCore);
    }

    [Fact]
    public void CoreSeam_RefusesAnUnknownProfileIdenticallyToTheInternalFactory()
    {
        GoogleDriveRemoteOperationException viaInternal =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => Factory(new InMemorySyncRemoteProfileRepository())
                    .Create(ProfileId));

        GoogleDriveRemoteOperationException viaCore =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => CoreFactory(new InMemorySyncRemoteProfileRepository())
                    .CreateGoogleDriveProvider(ProfileId));

        AssertIdenticalRefusal(viaInternal, viaCore);
    }

    [Fact]
    public void CoreSeam_RefusesAnEmptyProfileIdBeforeAnyLookup()
    {
        var repository = new CountingProfileRepository();

        Assert.Throws<ArgumentException>(
            () => CoreFactory(repository).CreateGoogleDriveProvider(Guid.Empty));

        Assert.Equal(0, repository.LookupCalls);
    }

    [Fact]
    public void CoreSeam_BuildsTheSameProviderTheInternalFactoryWouldBuild()
    {
        var fileSystems = new RecordingRemoteFileSystemFactory();
        ISyncProviderFactory core = new SyncProviderFactory(
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository(),
            new TestDatabasePathProvider(
                Path.Combine(Path.GetTempPath(), "gamesaves-u4.db")),
            new GoogleDriveSyncProviderFactory(
                RepositoryWith(UsableProfile()),
                fileSystems,
                new EmptyBackupHistoryService(),
                new RecordingHistoryRepository()));

        using ISyncProvider provider = core.CreateGoogleDriveProvider(ProfileId);

        Assert.Equal("Google Drive", provider.ProviderName);
        Assert.Equal(ProfileId, Assert.Single(fileSystems.RequestedProfileIds));
        Assert.Empty(fileSystems.FileSystem.Calls);
    }

    [Fact]
    public void CoreSeamRefusals_ExposeNoPrivateValue()
    {
        // WrongProviderKind is the status that still carries every private
        // value, so the non-vacuity check below is honest. RootNotConfigured
        // would have to fake the root marker back in, which would assert that
        // the sweep guards something the profile no longer holds.
        SyncRemoteProfile profile = UnusableProfile(
            GoogleDriveRemoteValidationStatus.WrongProviderKind);

        string[] markers =
            [RootId, AccountEmail, ProfileName, RootDisplayName, ProfileId.ToString()];

        // Non-vacuity: the sweep below only means something if these markers are
        // genuinely carried by the profile being refused. If a future refactor
        // stopped putting them there, the sweep would pass while proving nothing.
        string profileState = string.Join(
            "|",
            profile.Id.ToString(),
            profile.DisplayName,
            profile.AccountDisplayName,
            profile.RemoteRootDisplayName,
            profile.RemoteFolderId);

        foreach (string marker in markers)
        {
            Assert.False(string.IsNullOrWhiteSpace(marker));
            Assert.Contains(marker, profileState, StringComparison.OrdinalIgnoreCase);
        }

        GoogleDriveRemoteOperationException failure =
            Assert.Throws<GoogleDriveRemoteOperationException>(
                () => CoreFactory(RepositoryWith(profile))
                    .CreateGoogleDriveProvider(ProfileId));

        string[] surfaces =
        [
            failure.Message,
            failure.ToString(),
            failure.Result.UserMessage,
            failure.Result.ToSafeDiagnosticString()
        ];

        foreach (string surface in surfaces)
        {
            foreach (string marker in markers)
            {
                Assert.DoesNotContain(
                    marker, surface, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Null(failure.InnerException);
    }

    private static void AssertIdenticalRefusal(
        GoogleDriveRemoteOperationException viaInternal,
        GoogleDriveRemoteOperationException viaCore)
    {
        Assert.Equal(viaInternal.Result.Status, viaCore.Result.Status);
        Assert.Equal(viaInternal.Result.ErrorCode, viaCore.Result.ErrorCode);
        Assert.Equal(viaInternal.Result.UserMessage, viaCore.Result.UserMessage);
        Assert.Equal(viaInternal.Message, viaCore.Message);
        Assert.Null(viaCore.InnerException);
    }

    private static InMemorySyncRemoteProfileRepository RepositoryWith(
        SyncRemoteProfile profile)
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(profile);
        return repository;
    }

    private static ISyncProviderFactory CoreFactory(
        ISyncRemoteProfileRepository repository) =>
        new SyncProviderFactory(
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository(),
            new TestDatabasePathProvider(
                Path.Combine(Path.GetTempPath(), "gamesaves-u4.db")),
            Factory(repository));

    private static GoogleDriveSyncProviderFactory Factory(
        ISyncRemoteProfileRepository repository) =>
        new(
            repository,
            new RecordingRemoteFileSystemFactory(),
            new EmptyBackupHistoryService(),
            new RecordingHistoryRepository());

    private static SyncRemoteProfile UsableProfile() =>
        new(
            ProfileId,
            ProfileName,
            SyncProviderKind.GoogleDrive,
            AccountDisplayName: AccountEmail,
            RemoteRootDisplayName: RootDisplayName,
            ProviderSettings: new GoogleDriveSyncRemoteSettings(
                AccountEmail,
                GoogleDriveAuthorizationScopes.DriveFile),
            CreatedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            UpdatedUtc: DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: RootId);

    private static SyncRemoteProfile UnusableProfile(
        GoogleDriveRemoteValidationStatus status) =>
        status switch
        {
            GoogleDriveRemoteValidationStatus.WrongProviderKind =>
                UsableProfile() with { ProviderKind = SyncProviderKind.Sftp },
            // The settings record itself rejects an unsupported scope, so an
            // unusable persisted profile reaches this state through its
            // recorded settings error instead.
            GoogleDriveRemoteValidationStatus.UnsupportedScope =>
                UsableProfile() with { SettingsError = "Unreadable settings." },
            GoogleDriveRemoteValidationStatus.RootNotConfigured =>
                UsableProfile() with { RemoteFolderId = null },
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private sealed class CountingProfileRepository : ISyncRemoteProfileRepository
    {
        public int LookupCalls { get; private set; }

        public IReadOnlyList<SyncRemoteProfile> GetAll() => [];

        public SyncRemoteProfile? GetById(Guid id)
        {
            LookupCalls++;
            return null;
        }

        public SyncRemoteProfile Create(SyncRemoteProfile profile) =>
            throw new NotSupportedException();

        public SyncRemoteProfile Update(SyncRemoteProfile profile) =>
            throw new NotSupportedException();

        public SyncRemoteProfile Rename(
            Guid id,
            string displayName,
            DateTimeOffset updatedUtc) =>
            throw new NotSupportedException();

        public void Delete(Guid id) => throw new NotSupportedException();

        public SyncRemoteProfile UpdateLastUsed(
            Guid id,
            DateTimeOffset lastUsedUtc) =>
            throw new NotSupportedException();

        public SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id,
            DateTimeOffset lastSuccessfulConnectionUtc) =>
            throw new NotSupportedException();
    }
}
