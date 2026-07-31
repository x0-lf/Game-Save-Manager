using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveRemoteOperationContextTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EmptyOrMissingProfile_FailsBeforeAuthentication()
    {
        Context context = CreateContext(addProfile: false);

        GoogleDriveRemoteOperationContextException empty =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(Guid.Empty));
        GoogleDriveRemoteOperationContextException missing =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(Guid.NewGuid()));

        Assert.Equal(GoogleDriveRemoteValidationStatus.ProfileNotFound,
            empty.Result.Status);
        Assert.Equal(GoogleDriveRemoteValidationStatus.ProfileNotFound,
            missing.Result.Status);
        Assert.Equal(1, context.Repository.GetCalls);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
        Assert.Equal(0, context.ResolverFactory.CreateCalls);
    }

    [Fact]
    public async Task WrongProviderKind_FailsBeforeAuthentication()
    {
        SyncRemoteProfile profile = Profile() with
        {
            ProviderKind = SyncProviderKind.LocalFolder,
            ProviderSettings = new LocalFolderSyncRemoteSettings("C:\\Backups")
        };
        Context context = CreateContext(profile);

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(profile.Id));

        Assert.Equal(GoogleDriveRemoteValidationStatus.WrongProviderKind,
            exception.Result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MissingCorruptedOrUnsupportedSettings_AreRejected(
        int settingsCase)
    {
        SyncRemoteProfile profile = Profile() with
        {
            ProviderSettings = settingsCase switch
            {
                0 => null,
                1 => new UnsupportedGoogleDriveSettings(2),
                2 => new UnsupportedGoogleDriveSettings(1),
                _ => throw new ArgumentOutOfRangeException(nameof(settingsCase))
            },
            SettingsError = settingsCase == 2
                ? "The saved settings are unreadable."
                : null
        };
        Context context = CreateContext(profile);

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(profile.Id));

        Assert.Equal(GoogleDriveRemoteValidationStatus.UnsupportedScope,
            exception.Result.Status);
        Assert.Equal(GoogleDriveRemoteValidationErrorCodes.UnsupportedScope,
            exception.Result.ErrorCode);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Fact]
    public async Task MissingRootId_FailsBeforeAuthentication()
    {
        SyncRemoteProfile profile = Profile() with { RemoteFolderId = " " };
        Context context = CreateContext(profile);

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(profile.Id));

        Assert.Equal(GoogleDriveRemoteValidationStatus.RootNotConfigured,
            exception.Result.Status);
        Assert.Equal(0, context.SessionFactory.RestoreCalls);
    }

    [Fact]
    public async Task ValidProfile_RestoresSilentlyAndCreatesCredentialScopedResolver()
    {
        Context context = CreateContext();
        context.SessionFactory.WasAuthenticationRefreshed = true;

        using GoogleDriveRemoteOperationContext operation =
            await context.Factory.CreateAsync(context.Profile.Id);

        Assert.Equal(context.Profile.Id, operation.RemoteProfileId);
        Assert.Equal(context.Profile.RemoteFolderId, operation.RootFolderId);
        Assert.Same(context.SessionFactory.LastCredential, operation.Credential);
        Assert.True(operation.Credential.WasAuthenticationRefreshed);
        Assert.Same(context.ResolverFactory.Resolver, operation.Resolver);
        Assert.Same(context.Profile, context.SessionFactory.LastProfile);
        Assert.Equal(context.Profile.Id, context.ResolverFactory.ProfileIds.Single());
        Assert.Same(operation.Credential,
            context.ResolverFactory.Credentials.Single());
        Assert.Equal(0, context.ResolverFactory.Resolver.OperationCalls);
    }

    public static TheoryData<int, int> SessionFailures => new()
    {
        { (int)GoogleDriveAuthorizedSessionFailure.ClientConfigurationMissing,
            (int)GoogleDriveRemoteValidationStatus.AuthenticationUnavailable },
        { (int)GoogleDriveAuthorizedSessionFailure.NoStoredAuthentication,
            (int)GoogleDriveRemoteValidationStatus.NotConnected },
        { (int)GoogleDriveAuthorizedSessionFailure.SecretStoreUnavailable,
            (int)GoogleDriveRemoteValidationStatus.AuthenticationUnavailable },
        { (int)GoogleDriveAuthorizedSessionFailure.TokenCorrupted,
            (int)GoogleDriveRemoteValidationStatus.AuthenticationCorrupted },
        { (int)GoogleDriveAuthorizedSessionFailure.ReauthenticationRequired,
            (int)GoogleDriveRemoteValidationStatus.ReauthenticationRequired },
        { (int)GoogleDriveAuthorizedSessionFailure.AuthorizationRevoked,
            (int)GoogleDriveRemoteValidationStatus.AuthorizationRevoked },
        { (int)GoogleDriveAuthorizedSessionFailure.RevokedTokenCleanupFailed,
            (int)GoogleDriveRemoteValidationStatus.AuthorizationRevoked },
        { (int)GoogleDriveAuthorizedSessionFailure.Unavailable,
            (int)GoogleDriveRemoteValidationStatus.Unavailable },
        { (int)GoogleDriveAuthorizedSessionFailure.Failed,
            (int)GoogleDriveRemoteValidationStatus.Failed }
    };

    [Theory]
    [MemberData(nameof(SessionFailures))]
    public async Task AuthorizedSessionFailures_UseExistingSafeTaxonomy(
        int failureValue,
        int expectedStatusValue)
    {
        Context context = CreateContext();
        context.SessionFactory.Failure =
            (GoogleDriveAuthorizedSessionFailure)failureValue;

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(context.Profile.Id));

        Assert.Equal(
            (GoogleDriveRemoteValidationStatus)expectedStatusValue,
            exception.Result.Status);
        Assert.False(string.IsNullOrWhiteSpace(exception.Result.ErrorCode));
        Assert.Equal(0, context.ResolverFactory.CreateCalls);
    }

    [Fact]
    public async Task CancellationAfterRestore_DisposesCredentialAndSkipsResolver()
    {
        Context context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        context.SessionFactory.AfterCredentialCreated = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Factory.CreateAsync(
                context.Profile.Id,
                cancellation.Token));

        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
        Assert.Equal(0, context.ResolverFactory.CreateCalls);
    }

    [Fact]
    public async Task DisposingContext_DisposesCredentialAndBlocksResourceReuse()
    {
        Context context = CreateContext();
        GoogleDriveRemoteOperationContext operation =
            await context.Factory.CreateAsync(context.Profile.Id);

        operation.Dispose();
        operation.Dispose();

        Assert.True(operation.IsDisposed);
        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => operation.Credential);
        Assert.Throws<ObjectDisposedException>(() => operation.Resolver);
    }

    [Fact]
    public async Task ResolverCreationFailure_DisposesCredentialAndMapsSafely()
    {
        Context context = CreateContext();
        context.ResolverFactory.ThrowOnCreate = true;

        GoogleDriveRemoteOperationContextException exception =
            await Assert.ThrowsAsync<GoogleDriveRemoteOperationContextException>(() =>
                context.Factory.CreateAsync(context.Profile.Id));

        Assert.Equal(GoogleDriveRemoteValidationStatus.Failed,
            exception.Result.Status);
        Assert.True(context.SessionFactory.LastCredential!.IsDisposed);
        Assert.DoesNotContain(context.Profile.RemoteFolderId!, exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("fake-access-token", exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContextCreation_PerformsNoBrowserOrDriveObjectOperation()
    {
        Context context = CreateContext();

        using GoogleDriveRemoteOperationContext operation =
            await context.Factory.CreateAsync(context.Profile.Id);

        Assert.Equal(new[] { "RestoreAsync" },
            typeof(IGoogleDriveAuthorizedSessionFactory)
                .GetMethods()
                .Select(method => method.Name)
                .ToArray());
        Assert.Equal(1, context.SessionFactory.RestoreCalls);
        Assert.Equal(1, context.ResolverFactory.CreateCalls);
        Assert.Equal(0, context.ResolverFactory.Resolver.OperationCalls);
    }

    [Fact]
    public void DependencyRegistration_AddsFactoryWithoutStartingRemoteWork()
    {
        var services = new ServiceCollection();

        services.AddGameSavesInfrastructure();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            service => service.ServiceType ==
                typeof(IGoogleDriveRemoteOperationContextFactory));
        Assert.Equal(typeof(GoogleDriveRemoteOperationContextFactory),
            descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static Context CreateContext(
        SyncRemoteProfile? profile = null,
        bool addProfile = true)
    {
        profile ??= Profile();
        var repository = new RecordingProfileRepository(
            addProfile ? profile : null);
        var sessionFactory = new RecordingSessionFactory();
        var resolverFactory = new RecordingResolverFactory();
        var factory = new GoogleDriveRemoteOperationContextFactory(
            repository,
            sessionFactory,
            resolverFactory);
        return new Context(
            profile,
            repository,
            sessionFactory,
            resolverFactory,
            factory);
    }

    private static SyncRemoteProfile Profile() => new(
        Guid.Parse("a71b7d6c-56ee-4b50-b897-4a49e54cd143"),
        "Saved Google Drive",
        SyncProviderKind.GoogleDrive,
        "Example User",
        "Backup root",
        new GoogleDriveSyncRemoteSettings(
            "user@example.invalid",
            GoogleDriveAuthorizationScopes.DriveFile),
        Now.AddDays(-1),
        Now,
        null,
        null,
        "authoritative-root-id");

    private sealed record UnsupportedGoogleDriveSettings(int Version)
        : SyncRemoteProfileSettings(Version);

    private sealed record Context(
        SyncRemoteProfile Profile,
        RecordingProfileRepository Repository,
        RecordingSessionFactory SessionFactory,
        RecordingResolverFactory ResolverFactory,
        GoogleDriveRemoteOperationContextFactory Factory);

    private sealed class RecordingProfileRepository
        : ISyncRemoteProfileRepository
    {
        private readonly SyncRemoteProfile? _profile;

        public RecordingProfileRepository(SyncRemoteProfile? profile) =>
            _profile = profile;

        public int GetCalls { get; private set; }

        public IReadOnlyList<SyncRemoteProfile> GetAll() =>
            _profile is null ? Array.Empty<SyncRemoteProfile>() : new[] { _profile };

        public SyncRemoteProfile? GetById(Guid id)
        {
            GetCalls++;
            return _profile?.Id == id ? _profile : null;
        }

        public SyncRemoteProfile Create(SyncRemoteProfile profile) =>
            throw new NotSupportedException();
        public SyncRemoteProfile Update(SyncRemoteProfile profile) =>
            throw new NotSupportedException();
        public SyncRemoteProfile Rename(
            Guid id,
            string displayName,
            DateTimeOffset updatedUtc) => throw new NotSupportedException();
        public void Delete(Guid id) => throw new NotSupportedException();
        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc) =>
            throw new NotSupportedException();
        public SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id,
            DateTimeOffset lastSuccessfulConnectionUtc) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSessionFactory
        : IGoogleDriveAuthorizedSessionFactory
    {
        public GoogleDriveAuthorizedSessionFailure? Failure { get; set; }
        public bool WasAuthenticationRefreshed { get; set; }
        public Action? AfterCredentialCreated { get; set; }
        public int RestoreCalls { get; private set; }
        public SyncRemoteProfile? LastProfile { get; private set; }
        public GoogleAuthorizedCredential? LastCredential { get; private set; }

        public Task<GoogleDriveAuthorizedSession> RestoreAsync(
            SyncRemoteProfile profile,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            LastProfile = profile;
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is { } failure)
                throw new GoogleDriveAuthorizedSessionException(failure);

            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = "fake-client-id",
                        ClientSecret = "fake-client-secret"
                    }
                });
            var token = new TokenResponse
            {
                AccessToken = "fake-access-token",
                RefreshToken = "fake-refresh-token",
                ExpiresInSeconds = 3600,
                IssuedUtc = Now.UtcDateTime
            };
            LastCredential = new GoogleAuthorizedCredential(
                new UserCredential(flow, profile.Id.ToString("D"), token),
                wasAuthenticationRefreshed: WasAuthenticationRefreshed);
            AfterCredentialCreated?.Invoke();

            return Task.FromResult(new GoogleDriveAuthorizedSession(
                LastCredential,
                new GoogleDriveAccountInfo(
                    "Example User",
                    "user@example.invalid")));
        }
    }

    private sealed class RecordingResolverFactory
        : IGoogleDriveObjectPathResolverFactory
    {
        public RecordingResolver Resolver { get; } = new();
        public bool ThrowOnCreate { get; set; }
        public int CreateCalls { get; private set; }
        public List<Guid> ProfileIds { get; } = new();
        public List<GoogleAuthorizedCredential> Credentials { get; } = new();

        public IGoogleDriveObjectPathResolver Create(
            Guid remoteProfileId,
            GoogleAuthorizedCredential credential)
        {
            CreateCalls++;
            ProfileIds.Add(remoteProfileId);
            Credentials.Add(credential);
            if (ThrowOnCreate)
                throw new InvalidOperationException("Deterministic resolver failure.");
            return Resolver;
        }
    }

    private sealed class RecordingResolver : IGoogleDriveObjectPathResolver
    {
        public int OperationCalls { get; private set; }

        public Task<GoogleDriveObjectResolutionResult> FindChildAsync(
            string parentId,
            string exactName,
            GoogleDriveObjectKind expectedKind,
            CancellationToken cancellationToken = default) => Called();

        public Task<GoogleDriveObjectResolutionResult> ResolveAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativePath,
            GoogleDriveObjectKind? expectedFinalKind,
            CancellationToken cancellationToken = default) => Called();

        public Task<GoogleDriveObjectResolutionResult> EnsureFolderPathAsync(
            string rootFolderId,
            GoogleDriveRelativePath relativeFolderPath,
            CancellationToken cancellationToken = default) => Called();

        private Task<GoogleDriveObjectResolutionResult> Called()
        {
            OperationCalls++;
            throw new InvalidOperationException(
                "No resolver operation is expected while creating a context.");
        }
    }
}
