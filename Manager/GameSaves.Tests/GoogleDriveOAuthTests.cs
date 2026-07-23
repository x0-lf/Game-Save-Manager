using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaves.Tests;

public sealed class GoogleDriveOAuthTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-22T10:30:00Z");

    [Fact]
    public void EnvironmentConfiguration_RejectsMissingBlankAndPlaceholderValues()
    {
        Assert.Equal(
            GoogleDriveOAuthClientConfigurationStatus.Missing,
            CreateEnvironmentConfiguration(null).Read().State.Status);
        Assert.Equal(
            GoogleDriveOAuthClientConfigurationStatus.Missing,
            CreateEnvironmentConfiguration("  ").Read().State.Status);
        Assert.Equal(
            GoogleDriveOAuthClientConfigurationStatus.Invalid,
            CreateEnvironmentConfiguration(
                "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com").Read().State.Status);
    }

    [Fact]
    public void EnvironmentConfiguration_AcceptsDesktopIdWithoutExposingIt()
    {
        const string clientId = "1234567890-example.apps.googleusercontent.com";
        GoogleOAuthClientConfigurationReadResult result =
            CreateEnvironmentConfiguration(clientId).Read();

        Assert.True(result.State.IsAvailable);
        Assert.Equal(clientId, result.Configuration!.ClientId);
        Assert.DoesNotContain(clientId, result.State.ToString());
    }

    [Fact]
    public void EnvironmentConfiguration_UsesPersistentWindowsUserValueWhenProcessValueIsMissing()
    {
        const string clientId = "1234567890-user.apps.googleusercontent.com";
        var provider = new EnvironmentGoogleOAuthClientConfigurationProvider(
            _ => null,
            name => name == EnvironmentGoogleOAuthClientConfigurationProvider.ClientIdVariable
                ? clientId
                : null);

        GoogleOAuthClientConfigurationReadResult result = provider.Read();

        Assert.True(result.State.IsAvailable);
        Assert.Equal(clientId, result.Configuration!.ClientId);
    }

    [Fact]
    public void EnvironmentConfiguration_ProcessValueOverridesPersistentWindowsUserValue()
    {
        const string processClientId = "1234567890-process.apps.googleusercontent.com";
        const string userClientId = "1234567890-user.apps.googleusercontent.com";
        var provider = new EnvironmentGoogleOAuthClientConfigurationProvider(
            _ => processClientId,
            _ => userClientId);

        GoogleOAuthClientConfigurationReadResult result = provider.Read();

        Assert.True(result.State.IsAvailable);
        Assert.Equal(processClientId, result.Configuration!.ClientId);
    }

    [Fact]
    public void EnvironmentConfiguration_ReadsOptionalSecretWithoutExposingIt()
    {
        const string clientId = "1234567890-example.apps.googleusercontent.com";
        const string clientSecret = "test-client-secret-marker";
        var provider = new EnvironmentGoogleOAuthClientConfigurationProvider(
            name => name switch
            {
                EnvironmentGoogleOAuthClientConfigurationProvider.ClientIdVariable => clientId,
                EnvironmentGoogleOAuthClientConfigurationProvider.ClientSecretVariable => clientSecret,
                _ => null
            },
            _ => null);

        GoogleOAuthClientConfigurationReadResult result = provider.Read();

        Assert.True(result.State.IsAvailable);
        Assert.Equal(clientSecret, result.Configuration!.ClientSecret);
        Assert.DoesNotContain(clientSecret, result.Configuration.ToString());
        Assert.DoesNotContain(clientSecret, result.ToString());
    }

    [Fact]
    public void EnvironmentConfiguration_UsesPersistentWindowsUserSecretWhenProcessValueIsMissing()
    {
        const string clientId = "1234567890-example.apps.googleusercontent.com";
        const string clientSecret = "persistent-user-secret-marker";
        var provider = new EnvironmentGoogleOAuthClientConfigurationProvider(
            name => name == EnvironmentGoogleOAuthClientConfigurationProvider.ClientIdVariable
                ? clientId
                : null,
            name => name == EnvironmentGoogleOAuthClientConfigurationProvider.ClientSecretVariable
                ? clientSecret
                : null);

        GoogleOAuthClientConfigurationReadResult result = provider.Read();

        Assert.True(result.State.IsAvailable);
        Assert.Equal(clientSecret, result.Configuration!.ClientSecret);
    }

    [Fact]
    public void EnvironmentConfiguration_RejectsPlaceholderSecret()
    {
        var provider = new EnvironmentGoogleOAuthClientConfigurationProvider(
            name => name switch
            {
                EnvironmentGoogleOAuthClientConfigurationProvider.ClientIdVariable =>
                    "1234567890-example.apps.googleusercontent.com",
                EnvironmentGoogleOAuthClientConfigurationProvider.ClientSecretVariable =>
                    "YOUR_DESKTOP_CLIENT_SECRET",
                _ => null
            });

        Assert.Equal(
            GoogleDriveOAuthClientConfigurationStatus.Invalid,
            provider.Read().State.Status);
    }

    [Fact]
    public void RequestedScope_IsExactlyDriveFileOnly()
    {
        Assert.Equal(
            new[] { GoogleDriveAuthorizationScopes.DriveFile },
            GoogleDriveOAuthService.RequestedScopes);
        Assert.DoesNotContain("https://www.googleapis.com/auth/drive", GoogleDriveOAuthService.RequestedScopes);
        Assert.DoesNotContain("https://www.googleapis.com/auth/drive.readonly", GoogleDriveOAuthService.RequestedScopes);
        Assert.DoesNotContain("openid", GoogleDriveOAuthService.RequestedScopes);
        Assert.DoesNotContain("email", GoogleDriveOAuthService.RequestedScopes);
        Assert.DoesNotContain("profile", GoogleDriveOAuthService.RequestedScopes);
    }

    [Fact]
    public async Task Connect_StoresTokenReadsAccountAndUpdatesProfileTimestamps()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var authorizer = new FakeAuthorizer { StoreTokenOnConnect = true };
        var accountReader = new FakeAccountReader(
            new GoogleDriveAccountInfo("Example User", "user@example.invalid"));
        GoogleDriveOAuthService service = CreateService(
            repository, secrets, authorizer, accountReader);

        GoogleDriveAuthenticationResult result =
            await service.ConnectAsync(ProfileId);

        Assert.True(result.IsConnected);
        Assert.Equal(GoogleDriveConnectionStatus.Connected, result.ConnectionSettings!.ConnectionStatus);
        Assert.Equal("Example User", result.ConnectionSettings.AccountDisplayName);
        Assert.Equal("user@example.invalid", result.ConnectionSettings.AccountEmail);
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
        SyncRemoteProfile updated = repository.GetById(ProfileId)!;
        Assert.Equal(Now, updated.LastUsedUtc);
        Assert.Equal(Now, updated.LastSuccessfulConnectionUtc);
        Assert.Null(updated.RemoteFolderId);
        Assert.Null(updated.RemoteRootDisplayName);
    }

    [Fact]
    public async Task Connect_PersistsOperationalTimestampsThroughSqliteRepository()
    {
        using var temp = new TemporaryDirectory();
        var repository = new SqliteSyncRemoteProfileRepository(
            temp.GetPath("data", "gamesave.db"),
            new SyncRemoteProfileSettingsSerializer());
        repository.Create(CreateGoogleProfile());
        GoogleDriveOAuthService service = CreateService(
            repository,
            new InMemorySecretStore(),
            new FakeAuthorizer { StoreTokenOnConnect = true },
            new FakeAccountReader(
                new GoogleDriveAccountInfo("Example User", "user@example.invalid")));

        GoogleDriveAuthenticationResult result = await service.ConnectAsync(ProfileId);

        Assert.True(result.IsConnected);
        SyncRemoteProfile persisted = repository.GetById(ProfileId)!;
        Assert.Equal(Now, persisted.LastUsedUtc);
        Assert.Equal(Now, persisted.LastSuccessfulConnectionUtc);
        Assert.Equal("Example User", persisted.AccountDisplayName);
    }

    [Fact]
    public async Task Restore_WithNoToken_IsSilentAndReturnsNoStoredAuthentication()
    {
        var repository = CreateRepository();
        var authorizer = new FakeAuthorizer();
        GoogleDriveOAuthService service = CreateService(
            repository,
            new InMemorySecretStore(),
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.NoStoredAuthentication, result.Status);
        Assert.Equal(0, authorizer.ConnectCalls);
        Assert.Equal(1, authorizer.RestoreCalls);
    }

    [Fact]
    public async Task Restore_WithStoredToken_RewritesRefreshAndConnectsWithoutBrowserFlow()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        await store.StoreAsync(ProfileId.ToString("D"), CreateToken("stale-access"));
        var authorizer = new FakeAuthorizer { RefreshTokenOnRestore = true };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);
        TokenResponse restored =
            (await store.GetAsync<TokenResponse>(ProfileId.ToString("D")))!;

        Assert.True(result.IsConnected);
        Assert.Equal("refreshed-access", restored.AccessToken);
        Assert.Equal(0, authorizer.ConnectCalls);
    }

    [Theory]
    [InlineData((int)GoogleAuthorizationFailure.Cancelled, GoogleDriveAuthenticationStatus.Cancelled, GoogleDriveOAuthErrorCodes.Cancelled)]
    [InlineData((int)GoogleAuthorizationFailure.Denied, GoogleDriveAuthenticationStatus.AuthorizationDenied, GoogleDriveOAuthErrorCodes.Denied)]
    [InlineData((int)GoogleAuthorizationFailure.PolicyDenied, GoogleDriveAuthenticationStatus.AuthorizationDenied, GoogleDriveOAuthErrorCodes.PolicyDenied)]
    [InlineData((int)GoogleAuthorizationFailure.BrowserFailed, GoogleDriveAuthenticationStatus.BrowserLaunchFailed, GoogleDriveOAuthErrorCodes.BrowserFailed)]
    [InlineData((int)GoogleAuthorizationFailure.CallbackFailed, GoogleDriveAuthenticationStatus.CallbackFailed, GoogleDriveOAuthErrorCodes.CallbackFailed)]
    [InlineData((int)GoogleAuthorizationFailure.InvalidClient, GoogleDriveAuthenticationStatus.Failed, GoogleDriveOAuthErrorCodes.InvalidClient)]
    [InlineData((int)GoogleAuthorizationFailure.RedirectMismatch, GoogleDriveAuthenticationStatus.CallbackFailed, GoogleDriveOAuthErrorCodes.RedirectMismatch)]
    [InlineData((int)GoogleAuthorizationFailure.NetworkFailed, GoogleDriveAuthenticationStatus.Failed, GoogleDriveOAuthErrorCodes.NetworkFailed)]
    [InlineData((int)GoogleAuthorizationFailure.TokenExchangeFailed, GoogleDriveAuthenticationStatus.Failed, GoogleDriveOAuthErrorCodes.TokenExchangeFailed)]
    public async Task InteractiveFailures_MapToFriendlySafeResults(
        int failureValue,
        GoogleDriveAuthenticationStatus expected,
        string expectedErrorCode)
    {
        GoogleAuthorizationFailure failure = (GoogleAuthorizationFailure)failureValue;
        var authorizer = new FakeAuthorizer { Failure = failure };
        GoogleDriveOAuthService service = CreateService(
            CreateRepository(),
            new InMemorySecretStore(),
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        GoogleDriveAuthenticationResult result = await service.ConnectAsync(ProfileId);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain("authorization_code", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-token-marker", result.ToString());
    }

    [Fact]
    public async Task RefreshFailure_RequiresReauthenticationAndPreservesProfile()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        await new GoogleSecretDataStore(ProfileId, secrets).StoreAsync(
            ProfileId.ToString("D"),
            CreateToken("stale-access"));
        var authorizer = new FakeAuthorizer
        {
            Failure = GoogleAuthorizationFailure.RefreshFailed
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.ReauthenticationRequired, result.Status);
        Assert.NotNull(repository.GetById(ProfileId));
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
    }

    [Fact]
    public async Task AccountLookupFailure_PreservesValidProtectedToken()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var accountReader = new FakeAccountReader(null) { Throw = true };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new FakeAuthorizer { StoreTokenOnConnect = true },
            accountReader);

        GoogleDriveAuthenticationResult result = await service.ConnectAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.AccountLookupFailed, result.Status);
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
        Assert.Null(repository.GetById(ProfileId)!.AccountDisplayName);
    }

    [Fact]
    public async Task DriveUnavailable_MapsSeparatelyAndPreservesValidProtectedToken()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var accountReader = new FakeAccountReader(null)
        {
            Failure = GoogleDriveAccountReadFailure.Unavailable
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new FakeAuthorizer { StoreTokenOnConnect = true },
            accountReader);

        GoogleDriveAuthenticationResult result = await service.ConnectAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.AccountLookupFailed, result.Status);
        Assert.Equal(GoogleDriveOAuthErrorCodes.DriveUnavailable, result.ErrorCode);
        Assert.True(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
    }

    [Fact]
    public async Task MissingConfiguration_StopsBeforeAuthorization()
    {
        var repository = CreateRepository();
        var authorizer = new FakeAuthorizer();
        var service = new GoogleDriveOAuthService(
            repository,
            new InMemorySecretStore(),
            new FakeConfigurationProvider(null),
            new GoogleSecretDataStoreFactory(new InMemorySecretStore()),
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)),
            new FixedUtcClock(Now));

        GoogleDriveAuthenticationResult result = await service.ConnectAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.ClientConfigurationMissing, result.Status);
        Assert.Equal(0, authorizer.ConnectCalls);
    }

    [Theory]
    [InlineData(true, GoogleDriveAuthenticationStatus.TokenCorrupted)]
    [InlineData(false, GoogleDriveAuthenticationStatus.SecretStoreUnavailable)]
    public async Task Restore_MapsUnreadableProtectedTokenWithoutExposingIt(
        bool corrupted,
        GoogleDriveAuthenticationStatus expected)
    {
        var secrets = new InMemorySecretStore();
        SecretKey key = new(ProfileId, SecretNames.OAuthTokenData);

        if (corrupted)
            secrets.MarkCorrupted(key);
        else
            secrets.SimulateUnavailable = true;

        GoogleDriveOAuthService service = CreateService(
            CreateRepository(),
            secrets,
            new FakeAuthorizer(),
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);

        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("access", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentConnect_ForSameProfileIsRejectedWithoutSecondBrowserFlow()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizer = new FakeAuthorizer
        {
            ConnectStarted = started,
            ConnectRelease = release
        };
        GoogleDriveOAuthService service = CreateService(
            CreateRepository(),
            new InMemorySecretStore(),
            authorizer,
            new FakeAccountReader(new GoogleDriveAccountInfo("Example User", null)));

        Task<GoogleDriveAuthenticationResult> first = service.ConnectAsync(ProfileId);
        await started.Task;
        GoogleDriveAuthenticationResult second = await service.ConnectAsync(ProfileId);
        release.SetResult();
        await first;

        Assert.Equal(GoogleDriveAuthenticationStatus.Failed, second.Status);
        Assert.Equal(GoogleDriveOAuthErrorCodes.OperationInProgress, second.ErrorCode);
        Assert.Equal(1, authorizer.ConnectCalls);
    }

    [Fact]
    public void AccountLookup_IsLimitedToDriveUserIdentityFields()
    {
        Assert.Equal("user(displayName,emailAddress)", GoogleDriveAccountReader.RequestedFields);
        Assert.DoesNotContain("quota", GoogleDriveAccountReader.RequestedFields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", GoogleDriveAccountReader.RequestedFields, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfrastructureRegistration_ProvidesOAuthServiceWithoutStartingAuthorization()
    {
        var services = new ServiceCollection();

        services.AddGameSavesInfrastructure();

        ServiceDescriptor oauth = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IGoogleDriveOAuthService));
        Assert.Equal(ServiceLifetime.Singleton, oauth.Lifetime);
        Assert.NotNull(oauth.ImplementationFactory);
    }

    private static EnvironmentGoogleOAuthClientConfigurationProvider CreateEnvironmentConfiguration(
        string? clientId) =>
        new(name => name == EnvironmentGoogleOAuthClientConfigurationProvider.ClientIdVariable
            ? clientId
            : null);

    private static InMemorySyncRemoteProfileRepository CreateRepository()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(CreateGoogleProfile());
        return repository;
    }

    private static SyncRemoteProfile CreateGoogleProfile() =>
        new(
            ProfileId,
            "Personal Google Drive",
            SyncProviderKind.GoogleDrive,
            null,
            null,
            new GoogleDriveSyncRemoteSettings(
                null,
                GoogleDriveAuthorizationScopes.DriveFile),
            Now.AddDays(-1),
            Now.AddDays(-1),
            null,
            null,
            null);

    private static GoogleDriveOAuthService CreateService(
        ISyncRemoteProfileRepository repository,
        InMemorySecretStore secrets,
        IGoogleInstalledAppAuthorizer authorizer,
        IGoogleDriveAccountReader accountReader) =>
        new(
            repository,
            secrets,
            new FakeConfigurationProvider(
                new GoogleOAuthClientConfiguration(
                    "1234567890-example.apps.googleusercontent.com")),
            new GoogleSecretDataStoreFactory(secrets),
            authorizer,
            accountReader,
            new FixedUtcClock(Now));

    private static TokenResponse CreateToken(string accessToken) => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh-token-marker",
        TokenType = "Bearer",
        Scope = GoogleDriveAuthorizationScopes.DriveFile,
        ExpiresInSeconds = 3600,
        IssuedUtc = DateTime.UtcNow
    };

    private sealed class FakeConfigurationProvider : IGoogleOAuthClientConfigurationProvider
    {
        private readonly GoogleOAuthClientConfiguration? _configuration;

        public FakeConfigurationProvider(GoogleOAuthClientConfiguration? configuration) =>
            _configuration = configuration;

        public GoogleOAuthClientConfigurationReadResult Read() =>
            _configuration is null
                ? new(
                    new GoogleDriveOAuthClientConfigurationState(
                        GoogleDriveOAuthClientConfigurationStatus.Missing,
                        GoogleDriveOAuthErrorCodes.ClientIdMissing,
                        "Client configuration is missing."),
                    null)
                : new(
                    new GoogleDriveOAuthClientConfigurationState(
                        GoogleDriveOAuthClientConfigurationStatus.Available),
                    _configuration);
    }

    private sealed class FakeAuthorizer : IGoogleInstalledAppAuthorizer
    {
        public bool StoreTokenOnConnect { get; set; }
        public bool RefreshTokenOnRestore { get; set; }
        public GoogleAuthorizationFailure? Failure { get; set; }
        public TaskCompletionSource? ConnectStarted { get; set; }
        public TaskCompletionSource? ConnectRelease { get; set; }
        public int ConnectCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public async Task<GoogleAuthorizedCredential> ConnectAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            ConnectCalls++;
            ConnectStarted?.SetResult();

            if (ConnectRelease is not null)
                await ConnectRelease.Task.WaitAsync(cancellationToken);

            ThrowIfNeeded();
            TokenResponse token = CreateToken("access-token-marker");

            if (StoreTokenOnConnect)
                await dataStore.StoreAsync(profileId.ToString("D"), token);

            return CreateCredential(profileId, token);
        }

        public async Task<GoogleAuthorizedCredential?> RestoreAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            ThrowIfNeeded();
            TokenResponse? token = await dataStore.GetAsync<TokenResponse>(profileId.ToString("D"));

            if (token is null)
                return null;

            if (RefreshTokenOnRestore)
            {
                token.AccessToken = "refreshed-access";
                await dataStore.StoreAsync(profileId.ToString("D"), token);
            }

            return CreateCredential(profileId, token);
        }

        private void ThrowIfNeeded()
        {
            if (Failure is { } failure)
                throw new GoogleAuthorizationException(failure);
        }

        private static GoogleAuthorizedCredential CreateCredential(
            Guid profileId,
            TokenResponse token)
        {
            var flow = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = "1234567890-example.apps.googleusercontent.com"
                    }
                });
            return new GoogleAuthorizedCredential(
                new UserCredential(flow, profileId.ToString("D"), token));
        }
    }

    private sealed class FakeAccountReader : IGoogleDriveAccountReader
    {
        private readonly GoogleDriveAccountInfo? _account;

        public FakeAccountReader(GoogleDriveAccountInfo? account) => _account = account;

        public bool Throw { get; set; }
        public GoogleDriveAccountReadFailure? Failure { get; set; }

        public Task<GoogleDriveAccountInfo> ReadAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            if (Throw)
                throw new InvalidOperationException("Simulated safe account lookup failure.");

            if (Failure is { } failure)
                throw new GoogleDriveAccountReadException(failure);

            return Task.FromResult(_account!);
        }
    }
}
