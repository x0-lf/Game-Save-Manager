using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.GoogleDrive;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Google.Apis.Requests;
using System.Net;
using System.Reflection;

namespace GameSaves.Tests;

public sealed class GoogleDriveAccountLifecycleTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherProfileId =
        Guid.Parse("11111111-2222-3333-4444-666666666666");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-22T15:00:00Z");

    [Fact]
    public void LifecycleEnums_HaveStableValuesAndSafeFormatting()
    {
        Assert.Equal(16, (int)GoogleDriveAuthenticationStatus.AuthorizationRevoked);
        Assert.Equal(0, (int)GoogleDriveDisconnectionStatus.Disconnected);
        Assert.Equal(1, (int)GoogleDriveDisconnectionStatus.AlreadyDisconnected);
        Assert.Equal(6, (int)GoogleDriveDisconnectionStatus.Failed);

        var result = new GoogleDriveDisconnectionResult(
            GoogleDriveDisconnectionStatus.CleanupFailed,
            false,
            true,
            false,
            GoogleDriveDisconnectionErrorCodes.CleanupFailed,
            "Authentication cleanup failed.");

        Assert.Equal(
            "CleanupFailed (GoogleDriveDisconnectCleanupFailed)",
            result.ToString());
        Assert.DoesNotContain("token", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconnect_ReplacesTokenOnlyAfterValidationAndUpdatesAccountMetadata()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        await store.StoreAsync(ProfileId.ToString("D"), Token("old-access"));
        var authorizer = new LifecycleAuthorizer("new-access");
        var accountReader = new LifecycleAccountReader(
            new GoogleDriveAccountInfo("New Account", "new@example.invalid"));
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            accountReader);

        GoogleDriveAuthenticationResult result = await service.ReconnectAsync(ProfileId);
        TokenResponse stored =
            (await store.GetAsync<TokenResponse>(ProfileId.ToString("D")))!;
        SyncRemoteProfile updated = repository.GetById(ProfileId)!;

        Assert.True(result.IsConnected);
        Assert.Equal(1, authorizer.ConnectCalls);
        Assert.Equal(new[] { GoogleDriveAuthorizationScopes.DriveFile }, authorizer.Scopes);
        Assert.Equal("new-access", stored.AccessToken);
        Assert.Equal("New Account", updated.AccountDisplayName);
        Assert.Equal(
            "new@example.invalid",
            Assert.IsType<GoogleDriveSyncRemoteSettings>(updated.ProviderSettings).AccountEmail);
        Assert.Equal("future-folder-id", updated.RemoteFolderId);
        Assert.Equal("Future folder", updated.RemoteRootDisplayName);
        Assert.Equal(ProfileId, updated.Id);
        Assert.Equal(Now.AddDays(-2), updated.CreatedUtc);
        Assert.Equal(Now, updated.LastUsedUtc);
        Assert.Equal(Now, updated.LastSuccessfulConnectionUtc);
        Assert.Contains("validated", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((int)GoogleAuthorizationFailure.Cancelled)]
    [InlineData((int)GoogleAuthorizationFailure.Denied)]
    [InlineData((int)GoogleAuthorizationFailure.Failed)]
    public async Task Reconnect_InteractiveFailurePreservesOldTokenAndMetadata(int failureValue)
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        await store.StoreAsync(ProfileId.ToString("D"), Token("old-access"));
        var authorizer = new LifecycleAuthorizer("replacement-access")
        {
            ConnectFailure = (GoogleAuthorizationFailure)failureValue
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(
                new GoogleDriveAccountInfo("Different Account", "different@example.invalid")));

        GoogleDriveAuthenticationResult result = await service.ReconnectAsync(ProfileId);
        TokenResponse stored =
            (await store.GetAsync<TokenResponse>(ProfileId.ToString("D")))!;
        SyncRemoteProfile profile = repository.GetById(ProfileId)!;

        Assert.NotEqual(GoogleDriveAuthenticationStatus.Connected, result.Status);
        Assert.Equal("old-access", stored.AccessToken);
        Assert.Equal("Previous Account", profile.AccountDisplayName);
        Assert.Equal(
            "previous@example.invalid",
            Assert.IsType<GoogleDriveSyncRemoteSettings>(profile.ProviderSettings).AccountEmail);
    }

    [Fact]
    public async Task Reconnect_AccountLookupFailureDoesNotCommitReplacementToken()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var store = new GoogleSecretDataStore(ProfileId, secrets);
        await store.StoreAsync(ProfileId.ToString("D"), Token("old-access"));
        var reader = new LifecycleAccountReader(null)
        {
            Failure = GoogleDriveAccountReadFailure.Failed
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new LifecycleAuthorizer("replacement-access"),
            reader);

        GoogleDriveAuthenticationResult result = await service.ReconnectAsync(ProfileId);
        TokenResponse stored =
            (await store.GetAsync<TokenResponse>(ProfileId.ToString("D")))!;

        Assert.Equal(GoogleDriveAuthenticationStatus.AccountLookupFailed, result.Status);
        Assert.Equal("old-access", stored.AccessToken);
        Assert.Equal("Previous Account", repository.GetById(ProfileId)!.AccountDisplayName);
    }

    [Fact]
    public async Task Disconnect_RemovesOnlyGoogleTokenAndPreservesProfileRootAndOtherSecrets()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        SecretKey googleKey = new(ProfileId, SecretNames.OAuthTokenData);
        SecretKey sftpKey = new(ProfileId, SecretNames.SftpPassword);
        SecretKey oneDriveKey = new(ProfileId, SecretNames.OneDriveTokenData);
        SecretKey webDavKey = new(ProfileId, SecretNames.WebDavPassword);
        SecretKey otherGoogleKey = new(OtherProfileId, SecretNames.OAuthTokenData);
        foreach (SecretKey key in new[] { googleKey, sftpKey, oneDriveKey, webDavKey, otherGoogleKey })
            await secrets.StoreAsync(key, new byte[] { 1, 2, 3 });
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new LifecycleAuthorizer("unused"),
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Unused", null)));

        GoogleDriveDisconnectionResult first = await service.DisconnectAsync(ProfileId);
        GoogleDriveDisconnectionResult second = await service.DisconnectAsync(ProfileId);
        SyncRemoteProfile profile = repository.GetById(ProfileId)!;

        Assert.Equal(GoogleDriveDisconnectionStatus.Disconnected, first.Status);
        Assert.True(first.LocalAuthenticationRemoved);
        Assert.Equal(GoogleDriveDisconnectionStatus.AlreadyDisconnected, second.Status);
        Assert.False(await secrets.ExistsAsync(googleKey));
        Assert.True(await secrets.ExistsAsync(sftpKey));
        Assert.True(await secrets.ExistsAsync(oneDriveKey));
        Assert.True(await secrets.ExistsAsync(webDavKey));
        Assert.True(await secrets.ExistsAsync(otherGoogleKey));
        Assert.Null(profile.AccountDisplayName);
        Assert.Null(Assert.IsType<GoogleDriveSyncRemoteSettings>(profile.ProviderSettings).AccountEmail);
        Assert.Equal("future-folder-id", profile.RemoteFolderId);
        Assert.Equal("Future folder", profile.RemoteRootDisplayName);
        Assert.Equal(Now.AddDays(-1), profile.LastUsedUtc);
        Assert.Equal(Now.AddDays(-1), profile.LastSuccessfulConnectionUtc);
    }

    [Fact]
    public async Task Disconnect_CanRemoveCorruptedAuthenticationWithoutReadingIt()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        SecretKey key = new(ProfileId, SecretNames.OAuthTokenData);
        await secrets.StoreAsync(key, new byte[] { 9, 8, 7 });
        secrets.MarkCorrupted(key);
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new LifecycleAuthorizer("unused"),
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Unused", null)));

        GoogleDriveDisconnectionResult result = await service.DisconnectAsync(ProfileId);

        Assert.Equal(GoogleDriveDisconnectionStatus.Disconnected, result.Status);
        Assert.False(await secrets.ExistsAsync(key));
        Assert.NotNull(repository.GetById(ProfileId));
    }

    [Fact]
    public async Task Disconnect_SecretStoreFailureDoesNotClaimSuccessOrClearMetadata()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore { SimulateUnavailable = true };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            new LifecycleAuthorizer("unused"),
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Unused", null)));

        GoogleDriveDisconnectionResult result = await service.DisconnectAsync(ProfileId);

        Assert.Equal(GoogleDriveDisconnectionStatus.SecretStoreUnavailable, result.Status);
        Assert.False(result.LocalAuthenticationRemoved);
        Assert.False(result.AccountMetadataCleared);
        Assert.Equal("Previous Account", repository.GetById(ProfileId)!.AccountDisplayName);
    }

    [Fact]
    public async Task ConfirmedExternalRevocation_RemovesExactTokenAndPreservesAccountContext()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        SecretKey googleKey = new(ProfileId, SecretNames.OAuthTokenData);
        SecretKey sftpKey = new(ProfileId, SecretNames.SftpPassword);
        await secrets.StoreAsync(googleKey, new byte[] { 4, 5, 6 });
        await secrets.StoreAsync(sftpKey, new byte[] { 7, 8, 9 });
        var authorizer = new LifecycleAuthorizer("unused")
        {
            RestoreFailure = GoogleAuthorizationFailure.AuthorizationRevoked
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Unused", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.AuthorizationRevoked, result.Status);
        Assert.Equal(GoogleDriveConnectionStatus.ReauthenticationRequired, result.ConnectionSettings!.ConnectionStatus);
        Assert.False(result.ConnectionSettings.HasStoredToken);
        Assert.False(await secrets.ExistsAsync(googleKey));
        Assert.True(await secrets.ExistsAsync(sftpKey));
        Assert.Equal("Previous Account", repository.GetById(ProfileId)!.AccountDisplayName);
        Assert.Equal(0, authorizer.ConnectCalls);
    }

    [Fact]
    public async Task UnauthorizedAccountValidation_RemovesTokenButGenericProviderFailureDoesNot()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        SecretKey key = new(ProfileId, SecretNames.OAuthTokenData);
        await new GoogleSecretDataStore(ProfileId, secrets).StoreAsync(
            ProfileId.ToString("D"),
            Token("stored-access"));
        var authorizer = new LifecycleAuthorizer("unused");
        GoogleDriveOAuthService revokedService = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(null)
            {
                Failure = GoogleDriveAccountReadFailure.AuthorizationRevoked
            });

        GoogleDriveAuthenticationResult revoked = await revokedService.RestoreAsync(ProfileId);
        Assert.Equal(GoogleDriveAuthenticationStatus.AuthorizationRevoked, revoked.Status);
        Assert.False(await secrets.ExistsAsync(key));

        await new GoogleSecretDataStore(ProfileId, secrets).StoreAsync(
            ProfileId.ToString("D"),
            Token("temporary-access"));
        GoogleDriveOAuthService temporaryService = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(null)
            {
                Failure = GoogleDriveAccountReadFailure.Failed
            });
        GoogleDriveAuthenticationResult temporary = await temporaryService.RestoreAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.AccountLookupFailed, temporary.Status);
        Assert.True(await secrets.ExistsAsync(key));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, true)]
    [InlineData(HttpStatusCode.Forbidden, "authError", true)]
    [InlineData(HttpStatusCode.Forbidden, "invalidCredentials", true)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", false)]
    [InlineData(HttpStatusCode.Forbidden, null, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, false)]
    public void AccountReader_OnlyTreatsConfirmedAuthenticationErrorsAsRevoked(
        HttpStatusCode statusCode,
        string? reason,
        bool expected)
    {
        var exception = new GoogleApiException("Drive", "Safe test failure")
        {
            HttpStatusCode = statusCode,
            Error = reason is null
                ? null
                : new RequestError
                {
                    Errors = new List<SingleError>
                    {
                        new() { Reason = reason }
                    }
                }
        };
        MethodInfo method = typeof(GoogleDriveAccountReader).GetMethod(
            "IsConfirmedAuthenticationFailure",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        bool actual = (bool)method.Invoke(null, new object[] { exception })!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RevokedTokenCleanupFailure_ReturnsWarningAndPreservesTokenFlag()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        await secrets.StoreAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData),
            new byte[] { 1 });
        var authorizer = new LifecycleAuthorizer("unused")
        {
            RestoreFailure = GoogleAuthorizationFailure.AuthorizationRevoked
        };
        secrets.SimulateUnavailable = true;
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Unused", null)));

        GoogleDriveAuthenticationResult result = await service.RestoreAsync(ProfileId);

        Assert.Equal(GoogleDriveAuthenticationStatus.AuthorizationRevoked, result.Status);
        Assert.Equal(GoogleDriveOAuthErrorCodes.RevokedTokenCleanupFailed, result.ErrorCode);
        Assert.True(result.ConnectionSettings!.HasStoredToken);
        Assert.Contains("could not be removed", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disconnect_CancelsActiveConnectAndLateResultCannotRecreateToken()
    {
        var repository = CreateRepository();
        var secrets = new InMemorySecretStore();
        var authorizer = new LifecycleAuthorizer("late-token")
        {
            WaitForCancellation = true
        };
        GoogleDriveOAuthService service = CreateService(
            repository,
            secrets,
            authorizer,
            new LifecycleAccountReader(new GoogleDriveAccountInfo("Late Account", null)));

        Task<GoogleDriveAuthenticationResult> connect = service.ConnectAsync(ProfileId);
        await authorizer.Started.Task;
        GoogleDriveDisconnectionResult disconnect = await service.DisconnectAsync(ProfileId);
        GoogleDriveAuthenticationResult connectResult = await connect;

        Assert.True(disconnect.Succeeded);
        Assert.Equal(GoogleDriveAuthenticationStatus.Cancelled, connectResult.Status);
        Assert.False(await secrets.ExistsAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData)));
        Assert.Null(repository.GetById(ProfileId)!.AccountDisplayName);
    }

    private static InMemorySyncRemoteProfileRepository CreateRepository()
    {
        var repository = new InMemorySyncRemoteProfileRepository();
        repository.Create(new SyncRemoteProfile(
            ProfileId,
            "Google profile",
            SyncProviderKind.GoogleDrive,
            "Previous Account",
            "Future folder",
            new GoogleDriveSyncRemoteSettings(
                "previous@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            Now.AddDays(-2),
            Now.AddDays(-2),
            Now.AddDays(-1),
            Now.AddDays(-1),
            "future-folder-id"));
        return repository;
    }

    private static GoogleDriveOAuthService CreateService(
        ISyncRemoteProfileRepository repository,
        InMemorySecretStore secrets,
        IGoogleInstalledAppAuthorizer authorizer,
        IGoogleDriveAccountReader accountReader) =>
        new(
            repository,
            secrets,
            new LifecycleConfigurationProvider(),
            new GoogleSecretDataStoreFactory(secrets),
            authorizer,
            accountReader,
            new FixedUtcClock(Now));

    private static TokenResponse Token(string accessToken) => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh-marker",
        TokenType = "Bearer",
        Scope = GoogleDriveAuthorizationScopes.DriveFile,
        ExpiresInSeconds = 3600,
        IssuedUtc = DateTime.UtcNow
    };

    private sealed class LifecycleConfigurationProvider : IGoogleOAuthClientConfigurationProvider
    {
        public GoogleOAuthClientConfigurationReadResult Read() =>
            new(
                new GoogleDriveOAuthClientConfigurationState(
                    GoogleDriveOAuthClientConfigurationStatus.Available),
                new GoogleOAuthClientConfiguration(
                    "1234567890-example.apps.googleusercontent.com"));
    }

    private sealed class LifecycleAuthorizer : IGoogleInstalledAppAuthorizer
    {
        private readonly string _replacementAccessToken;

        public LifecycleAuthorizer(string replacementAccessToken) =>
            _replacementAccessToken = replacementAccessToken;

        public GoogleAuthorizationFailure? ConnectFailure { get; set; }
        public GoogleAuthorizationFailure? RestoreFailure { get; set; }
        public bool WaitForCancellation { get; set; }
        public int ConnectCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public IReadOnlyList<string>? Scopes { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GoogleAuthorizedCredential> ConnectAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            ConnectCalls++;
            Scopes = scopes.ToArray();
            Started.TrySetResult();

            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new GoogleAuthorizationException(GoogleAuthorizationFailure.Cancelled);
                }
            }

            if (ConnectFailure is { } failure)
                throw new GoogleAuthorizationException(failure);

            TokenResponse token = Token(_replacementAccessToken);
            return CreateCredential(
                profileId,
                token,
                ct => dataStore.StoreAsync(profileId.ToString("D"), token));
        }

        public async Task<GoogleAuthorizedCredential?> RestoreAsync(
            GoogleOAuthClientConfiguration configuration,
            Guid profileId,
            IDataStore dataStore,
            IReadOnlyList<string> scopes,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            Scopes = scopes.ToArray();

            if (RestoreFailure is { } failure)
                throw new GoogleAuthorizationException(failure);

            TokenResponse? token = await dataStore.GetAsync<TokenResponse>(profileId.ToString("D"));
            return token is null ? null : CreateCredential(profileId, token);
        }

        private static GoogleAuthorizedCredential CreateCredential(
            Guid profileId,
            TokenResponse token,
            Func<CancellationToken, Task>? commit = null)
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
                new UserCredential(flow, profileId.ToString("D"), token),
                commit);
        }
    }

    private sealed class LifecycleAccountReader : IGoogleDriveAccountReader
    {
        private readonly GoogleDriveAccountInfo? _account;

        public LifecycleAccountReader(GoogleDriveAccountInfo? account) => _account = account;

        public GoogleDriveAccountReadFailure? Failure { get; set; }

        public Task<GoogleDriveAccountInfo> ReadAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
                throw new GoogleDriveAccountReadException(failure);

            return Task.FromResult(_account!);
        }
    }
}
