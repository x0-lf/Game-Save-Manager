using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Platform;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.OneDrive;
using GameSaves.Infrastructure.Sync;
using Xunit;

namespace GameSaves.Tests;

public sealed class OneDriveSyncProviderTests
{
    private static readonly Guid TestProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string ValidClientId = "11111111-2222-3333-4444-555555555555";

    #region 1. Catalog & Capabilities Tests

    [Fact]
    public void SyncProviderCatalog_ReflectsOneDriveAsImplemented()
    {
        var catalog = new SyncProviderCatalog();
        var descriptor = catalog.GetDescriptor(SyncProviderKind.OneDrive);

        Assert.NotNull(descriptor);
        Assert.True(descriptor.IsImplemented);
        Assert.Null(descriptor.UnavailableMessage);
        Assert.True(descriptor.IsConfigurationAvailable);
        Assert.Equal(SyncProviderConfigurationSurface.InteractiveOAuth, descriptor.ConfigurationSurface);
        Assert.True(descriptor.Capabilities.RequiresInteractiveLogin);
        Assert.True(descriptor.Capabilities.SupportsPersistentAuthentication);
        Assert.True(descriptor.Capabilities.SupportsLogout);
        Assert.True(descriptor.Capabilities.SupportsRemoteQuota);
        Assert.False(descriptor.Capabilities.SupportsRemoteFolderSelection);
    }

    #endregion

    #region 2. Authorization Scopes & Settings Serializer Tests

    [Fact]
    public void OneDriveAuthorizationScopes_AllowsAppFolderAndOfflineAccess()
    {
        Assert.Equal(
            OneDriveAuthorizationScopes.AppFolder,
            OneDriveAuthorizationScopes.ValidateRequestedScope(OneDriveAuthorizationScopes.AppFolder));

        string multiScope = $"{OneDriveAuthorizationScopes.AppFolder} {OneDriveAuthorizationScopes.OfflineAccess}";
        Assert.Equal(
            multiScope,
            OneDriveAuthorizationScopes.ValidateRequestedScope(multiScope));

        string defaultScopes = $"{OneDriveAuthorizationScopes.AppFolder} {OneDriveAuthorizationScopes.OfflineAccess} {OneDriveAuthorizationScopes.UserRead}";
        Assert.Equal(
            defaultScopes,
            OneDriveAuthorizationScopes.ValidateRequestedScope(defaultScopes));
    }

    [Fact]
    public void OneDriveAuthorizationScopes_RejectsBroadOrDangerousScopes()
    {
        Assert.Throws<ArgumentException>(() =>
            OneDriveAuthorizationScopes.ValidateRequestedScope("Files.ReadWrite"));
        Assert.Throws<ArgumentException>(() =>
            OneDriveAuthorizationScopes.ValidateRequestedScope("Files.ReadWrite.All"));
        Assert.Throws<ArgumentException>(() =>
            OneDriveAuthorizationScopes.ValidateRequestedScope("Sites.ReadWrite.All"));
        Assert.Throws<ArgumentException>(() =>
            OneDriveAuthorizationScopes.ValidateRequestedScope("User.Read.All"));
        Assert.Throws<ArgumentException>(() =>
            OneDriveAuthorizationScopes.ValidateRequestedScope("Files.ReadWrite.All Files.ReadWrite.AppFolder"));
    }

    [Fact]
    public void SyncRemoteProfileSettingsSerializer_RoundTripsOneDriveSettings()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        var original = new OneDriveSyncRemoteSettings(
            accountEmail: "gamer@example.com",
            requestedScope: $"{OneDriveAuthorizationScopes.AppFolder} {OneDriveAuthorizationScopes.OfflineAccess}");

        string json = serializer.Serialize(SyncProviderKind.OneDrive, original);
        Assert.NotNull(json);

        var readResult = serializer.Deserialize(SyncProviderKind.OneDrive, OneDriveSyncRemoteSettings.CurrentSchemaVersion, json);
        Assert.Null(readResult.Error);

        var deserialized = readResult.Settings as OneDriveSyncRemoteSettings;
        Assert.NotNull(deserialized);
        Assert.Equal("gamer@example.com", deserialized.AccountEmail);
        Assert.Equal(original.RequestedScope, deserialized.RequestedScope);
        Assert.Equal(OneDriveSyncRemoteSettings.CurrentSchemaVersion, deserialized.SchemaVersion);
    }

    [Fact]
    public void SyncRemoteProfileSettingsSerializer_RejectsInvalidScope()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        string invalidJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            accountEmail = "gamer@example.com",
            requestedScope = "Files.ReadWrite.All" // Broad scope forbidden
        });

        var readResult = serializer.Deserialize(SyncProviderKind.OneDrive, 1, invalidJson);
        Assert.NotNull(readResult.Error);
        Assert.Null(readResult.Settings);
    }

    #endregion

    #region 3. OAuth & Secret Store Tests

    [Fact]
    public void ClientConfiguration_IsMissingOrInvalid_WithoutAUsableClientId()
    {
        var repo = new InMemorySyncRemoteProfileRepository();
        var store = new InMemorySecretStore();

        Assert.Equal(
            OneDriveOAuthClientConfigurationStatus.Missing,
            CreateService(repo, store, new FakeOneDriveApiClient(), clientId: " ").GetClientConfigurationState().Status);
        Assert.Equal(
            OneDriveOAuthClientConfigurationStatus.Invalid,
            CreateService(repo, store, new FakeOneDriveApiClient(), clientId: "not-a-guid").GetClientConfigurationState().Status);
        Assert.True(
            CreateService(repo, store, new FakeOneDriveApiClient()).GetClientConfigurationState().IsAvailable);
    }

    [Fact]
    public async Task ConnectAsync_RefusesToStart_WhenClientIdIsMissing()
    {
        var repo = new InMemorySyncRemoteProfileRepository();
        repo.Create(CreateProfile());
        bool authorizerCalled = false;
        var service = CreateService(
            repo,
            new InMemorySecretStore(),
            new FakeOneDriveApiClient(),
            authorizer: new FakeAuthorizer((_, _, _) => { authorizerCalled = true; return "code"; }),
            clientId: "");

        var result = await service.ConnectAsync(TestProfileId);

        Assert.Equal(OneDriveAuthenticationStatus.ClientConfigurationMissing, result.Status);
        Assert.Equal(OneDriveOAuthErrorCodes.ClientIdMissing, result.ErrorCode);
        Assert.False(authorizerCalled);
    }

    [Fact]
    public async Task ConnectAsync_UsesStateAndPkce_StoresEmail_AndKeepsConcurrentRename()
    {
        var clock = new FixedUtcClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var repo = new InMemorySyncRemoteProfileRepository();
        repo.Create(CreateProfile());
        var store = new InMemorySecretStore();
        string? authUrl = null;
        string? redirect = null;
        string? state = null;

        var service = CreateService(
            repo,
            store,
            new FakeOneDriveApiClient(),
            clock,
            new FakeAuthorizer((url, redirectUri, expectedState) =>
            {
                (authUrl, redirect, state) = (url, redirectUri, expectedState);
                repo.Rename(TestProfileId, "Renamed while signing in", clock.UtcNow);
                return "auth-code";
            }));

        var result = await service.ConnectAsync(TestProfileId);

        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(state));
        Assert.Contains($"state={state}", authUrl);
        Assert.Contains("code_challenge_method=S256", authUrl);
        Assert.StartsWith("http://localhost:", redirect);

        SyncRemoteProfile profile = repo.GetById(TestProfileId)!;
        Assert.Equal("Renamed while signing in", profile.DisplayName);
        Assert.Equal("chief@unsc.gov", Assert.IsType<OneDriveSyncRemoteSettings>(profile.ProviderSettings).AccountEmail);
        Assert.Equal(OneDriveRemoteFileSystem.DisplayRootName, profile.RemoteRootDisplayName);

        // Expiry comes from the injected clock, not the wall clock.
        StoredOneDriveTokenData stored = await ReadStoredTokenAsync(store);
        Assert.Equal(clock.UtcNow.AddSeconds(3600), stored.ExpiresAtUtc);
    }

    [Fact]
    public async Task ConnectAsync_MapsCancellationAndDenial()
    {
        var repo = new InMemorySyncRemoteProfileRepository();
        repo.Create(CreateProfile());

        var cancelled = await CreateService(
                repo,
                new InMemorySecretStore(),
                new FakeOneDriveApiClient(),
                authorizer: new FakeAuthorizer((_, _, _) => throw new OperationCanceledException()))
            .ConnectAsync(TestProfileId);
        var denied = await CreateService(
                repo,
                new InMemorySecretStore(),
                new FakeOneDriveApiClient(),
                authorizer: new FakeAuthorizer((_, _, _) => throw new OneDriveAuthorizationCallbackException(denied: true)))
            .ConnectAsync(TestProfileId);

        Assert.Equal(OneDriveAuthenticationStatus.Cancelled, cancelled.Status);
        Assert.Equal(OneDriveAuthenticationStatus.AuthorizationDenied, denied.Status);
    }

    [Fact]
    public async Task ConnectAsync_Fails_WhenTokenCannotBeStored()
    {
        var repo = new InMemorySyncRemoteProfileRepository();
        repo.Create(CreateProfile());
        var store = new InMemorySecretStore { StoreOverride = SecretOperationResult.Unavailable("SecretStoreUnavailable") };

        var result = await CreateService(repo, store, new FakeOneDriveApiClient()).ConnectAsync(TestProfileId);

        Assert.False(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.SecretStoreUnavailable, result.Status);
        Assert.Null(repo.GetById(TestProfileId)!.AccountDisplayName);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsNoStoredAuthentication_WhenSecretStoreIsEmpty()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile());

        var result = await CreateService(profileRepo, new InMemorySecretStore(), new FakeOneDriveApiClient())
            .RestoreAsync(TestProfileId);

        Assert.False(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.NoStoredAuthentication, result.Status);
    }

    [Fact]
    public async Task RestoreAsync_ReportsSecretStoreUnavailable_WhenStoreCannotBeRead()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile());
        var store = new InMemorySecretStore { ReadOverride = SecretReadResult.Unavailable("SecretStoreUnavailable") };

        var result = await CreateService(profileRepo, store, new FakeOneDriveApiClient()).RestoreAsync(TestProfileId);

        Assert.Equal(OneDriveAuthenticationStatus.SecretStoreUnavailable, result.Status);
    }

    [Fact]
    public async Task RestoreAsync_RequiresReauthentication_OnlyForInvalidGrant()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile());
        var store = new InMemorySecretStore();
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddMinutes(-1));

        var revoked = await CreateService(
                profileRepo,
                store,
                new FakeOneDriveApiClient { RefreshFailure = new OneDriveApiException(400, oauthError: "invalid_grant") })
            .RestoreAsync(TestProfileId);
        var offline = await CreateService(
                profileRepo,
                store,
                new FakeOneDriveApiClient { RefreshFailure = new OneDriveApiException(null) })
            .RestoreAsync(TestProfileId);

        Assert.Equal(OneDriveAuthenticationStatus.ReauthenticationRequired, revoked.Status);
        Assert.Equal(OneDriveAuthenticationStatus.Unavailable, offline.Status);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsConnected_WhenValidTokenInStore()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile(email: "chief@unsc.gov", accountDisplayName: "Master Chief"));
        var store = new InMemorySecretStore();
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddHours(1));

        var result = await CreateService(profileRepo, store, new FakeOneDriveApiClient()).RestoreAsync(TestProfileId);

        Assert.True(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.Connected, result.Status);
        Assert.Equal("chief@unsc.gov", result.AccountEmail);
        Assert.Equal("Master Chief", result.AccountDisplayName);
    }

    [Fact]
    public async Task RestoreAsync_RefreshesToken_WhenTokenNearExpiration()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile(email: "chief@unsc.gov", accountDisplayName: "Master Chief"));
        var store = new InMemorySecretStore();

        // Expires in 2 minutes, which is within the 5-minute refresh window
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddMinutes(2), "old-access-token");

        var fakeClient = new FakeOneDriveApiClient
        {
            RefreshedToken = new OneDriveTokenResponse(
                AccessToken: "new-access-token",
                RefreshToken: "new-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder)
        };

        var result = await CreateService(profileRepo, store, fakeClient).RestoreAsync(TestProfileId);
        Assert.True(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.Connected, result.Status);

        // Verify that secret store was updated with new token
        StoredOneDriveTokenData updated = await ReadStoredTokenAsync(store);
        Assert.Equal("new-access-token", updated.AccessToken);
        Assert.Equal("new-refresh-token", updated.RefreshToken);
    }

    [Fact]
    public async Task DisconnectAsync_RemovesStoredToken_AndPreservesProfile()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile(email: "cortana@unsc.gov", accountDisplayName: "Cortana"));
        var store = new InMemorySecretStore();
        await store.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData), Encoding.UTF8.GetBytes("dummy-token"));

        var disconnectResult = await CreateService(profileRepo, store, new FakeOneDriveApiClient())
            .DisconnectAsync(TestProfileId);

        Assert.True(disconnectResult.Succeeded);
        Assert.Equal(OneDriveDisconnectionStatus.Disconnected, disconnectResult.Status);
        Assert.True(disconnectResult.LocalAuthenticationRemoved);
        Assert.True(disconnectResult.ProfilePreserved);

        // Token removed from store
        var readResult = await store.ReadAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData));
        Assert.Equal(SecretReadStatus.NotFound, readResult.Status);

        // Profile preserved in repository, account metadata cleared
        var profile = profileRepo.GetById(TestProfileId);
        Assert.NotNull(profile);
        Assert.Null(profile.AccountDisplayName);
        Assert.Null(Assert.IsType<OneDriveSyncRemoteSettings>(profile.ProviderSettings).AccountEmail);
    }

    [Fact]
    public async Task DisconnectAsync_ReportsFailures_InsteadOfClaimingSuccess()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(CreateProfile(email: "cortana@unsc.gov", accountDisplayName: "Cortana"));
        Guid googleId = Guid.NewGuid();
        profileRepo.Create(CreateProfile(accountDisplayName: "Google user") with
        {
            Id = googleId,
            ProviderKind = SyncProviderKind.GoogleDrive,
            ProviderSettings = null
        });

        var failing = new InMemorySecretStore { DeleteOverride = SecretOperationResult.Unavailable("SecretStoreUnavailable") };
        var unavailable = await CreateService(profileRepo, failing, new FakeOneDriveApiClient()).DisconnectAsync(TestProfileId);

        Assert.False(unavailable.Succeeded);
        Assert.Equal(OneDriveDisconnectionStatus.SecretStoreUnavailable, unavailable.Status);
        Assert.False(unavailable.LocalAuthenticationRemoved);
        Assert.Equal("Cortana", profileRepo.GetById(TestProfileId)!.AccountDisplayName);

        var service = CreateService(profileRepo, new InMemorySecretStore(), new FakeOneDriveApiClient());
        var wrongKind = await service.DisconnectAsync(googleId);
        Assert.Equal(OneDriveDisconnectionStatus.WrongProviderKind, wrongKind.Status);
        Assert.Equal("Google user", profileRepo.GetById(googleId)!.AccountDisplayName);

        var nothingStored = await service.DisconnectAsync(TestProfileId);
        Assert.Equal(OneDriveDisconnectionStatus.AlreadyDisconnected, nothingStored.Status);
        Assert.False(nothingStored.LocalAuthenticationRemoved);
    }

    [Fact]
    public void TokenTypes_DoNotPrintTokens()
    {
        var response = new OneDriveTokenResponse("access-secret", "refresh-secret", 3600, "Bearer", null);
        var stored = new StoredOneDriveTokenData("access-secret", "refresh-secret", DateTimeOffset.UtcNow, "Bearer", null);

        Assert.DoesNotContain("secret", response.ToString());
        Assert.DoesNotContain("secret", stored.ToString());
    }

    #endregion

    #region 4. OneDriveRemoteFileSystem & Invariants Tests

    [Fact]
    public void OneDriveRemoteFileSystem_SupportsArchiveContainers()
    {
        var fakeClient = new FakeOneDriveApiClient();
        var oauth = CreateService(new InMemorySyncRemoteProfileRepository(), new InMemorySecretStore(), fakeClient);

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth);
        Assert.True(fs.SupportsArchiveContainers);
    }

    [Fact]
    public async Task UploadAsync_EnforcesCreateOnly_ThrowsWhenFileAlreadyExists()
    {
        var fakeClient = new FakeOneDriveApiClient();
        var fs = await CreateFileSystemAsync(fakeClient);

        // Simulate file existing remotely
        fakeClient.ExistingFiles.Add("Game1/2026-01-01_Run1/save.dat");

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "test data");

            // Uploading existing file should throw InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fs.UploadFileAsync(tempFile, "Game1/2026-01-01_Run1/save.dat"));

            Assert.Contains("Refusing to overwrite existing file", ex.Message);
            Assert.Equal(new[] { true }, fakeClient.UploadCreateOnlyFlags);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UploadAsync_Succeeds_WhenFileDoesNotExist()
    {
        var fakeClient = new FakeOneDriveApiClient();
        var fs = await CreateFileSystemAsync(fakeClient);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "new data");
            await fs.UploadFileAsync(tempFile, "Game1/2026-01-01_Run1/save.dat");

            Assert.True(fakeClient.UploadedFiles.ContainsKey("Game1/2026-01-01_Run1/save.dat"));
            Assert.Equal("new data", Encoding.UTF8.GetString(fakeClient.UploadedFiles["Game1/2026-01-01_Run1/save.dat"]));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReplaceProviderMetadataAsync_RejectsPathsOtherThanTheSyncLog()
    {
        var fakeClient = new FakeOneDriveApiClient();
        var fs = await CreateFileSystemAsync(fakeClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fs.ReplaceProviderMetadataAsync(".gamesave-sync/../Run1/manifest.json", "{}"));
        Assert.Empty(fakeClient.UploadedFiles);

        await fs.ReplaceProviderMetadataAsync(".gamesave-sync/sync-log.json", "{}");
        Assert.Equal(new[] { false }, fakeClient.UploadCreateOnlyFlags);
    }

    [Fact]
    public async Task ValidateAsync_DoesNotBlock_WhenQuotaExceeded()
    {
        var fakeClient = new FakeOneDriveApiClient
        {
            Quota = new OneDriveQuotaInfo(TotalBytes: 5_000_000_000, UsedBytes: 5_000_000_000, RemainingBytes: 0, State: "exceeded")
        };
        var fs = await CreateFileSystemAsync(fakeClient);

        // A full drive must not block download-only restores.
        Assert.Null(await fs.ValidateAsync());
    }

    [Fact]
    public async Task ValidateAsync_PropagatesCancellation()
    {
        var fs = await CreateFileSystemAsync(new FakeOneDriveApiClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fs.ValidateAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task DownloadFileAsync_NeverOverwritesAnExistingLocalFile()
    {
        var fakeClient = new FakeOneDriveApiClient();
        fakeClient.UploadedFiles["Run1/save.dat"] = Encoding.UTF8.GetBytes("remote");
        var fs = await CreateFileSystemAsync(fakeClient);

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "local");

            await Assert.ThrowsAsync<IOException>(() => fs.DownloadFileAsync("Run1/save.dat", tempFile));
            Assert.Equal("local", await File.ReadAllTextAsync(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RemoteFileSystem_RetriesThrottling_HonouringRetryAfter()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (calls++ == 0)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return throttled;
            }

            return Json(HttpStatusCode.OK, """{"value":[{"name":"Run1","folder":{}}]}""");
        });
        using var apiClient = new OneDriveApiClient(new HttpClient(handler));
        var store = new InMemorySecretStore();
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddHours(1));
        var oauth = CreateService(new InMemorySyncRemoteProfileRepository(), store, apiClient);
        var delay = new RecordingDelayProvider();

        IRemoteFileSystem fs = OneDriveSyncProviderFactory.WithRetries(
            new OneDriveRemoteFileSystem(TestProfileId, apiClient, oauth),
            delay,
            backoffNotifier: null);

        Assert.Equal(new[] { "Run1" }, await fs.ListRunFolderNamesAsync());
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delay.Requested);
    }

    #endregion

    #region 5. OneDriveApiClient HTTP Tests

    [Fact]
    public async Task ListChildrenAsync_FollowsEveryPage()
    {
        const string page2 = "https://graph.microsoft.com/v1.0/me/drive/special/approot/children?$skiptoken=abc";
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsoluteUri == page2
                ? Json(HttpStatusCode.OK, """{"value":[{"name":"b.dat","file":{}}]}""")
                : Json(HttpStatusCode.OK, $$$"""{"value":[{"name":"a.dat","file":{}}],"@odata.nextLink":"{{{page2}}}"}"""));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        var children = await client.ListChildrenAsync("token");

        Assert.Equal(new[] { "a.dat", "b.dat" }, children.Select(c => c.Name));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer token", r.Authorization));
    }

    [Fact]
    public async Task ListChildrenAsync_RefusesPagingLinksOutsideGraph()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://attacker.example/steal"}"""));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListChildrenAsync("token"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadContentAsync_CreateOnly_AsksGraphToFail_AndMapsConflictToRefusal()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict, """{"error":{"code":"nameAlreadyExists"}}"""));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UploadContentAsync("token", "Run1/manifest.json", new MemoryStream([1, 2, 3]), createOnly: true));

        Assert.Contains("Refusing to overwrite existing file in create-only mode", ex.Message);
        Assert.EndsWith(":/content?@microsoft.graph.conflictBehavior=fail", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task UploadContentAsync_Replace_UsesPlainPut()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"name":"sync-log.json","file":{}}"""));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        await client.UploadContentAsync("token", ".gamesave-sync/sync-log.json", new MemoryStream([1]), createOnly: false);

        Assert.EndsWith(":/content", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task UploadContentAsync_LargeFile_UsesUploadSessionChunksWithoutBearerToken()
    {
        const string sessionUrl = "https://upload.example/session/123";
        int total = OneDriveApiClient.UploadChunkSize + (2 * 1024 * 1024);
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.OK, $$"""{"uploadUrl":"{{sessionUrl}}"}""")
                : Json(HttpStatusCode.Accepted, "{}"));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        await client.UploadContentAsync("token", "Run1.7z", new MemoryStream(new byte[total]), createOnly: true);

        Assert.Equal(3, handler.Requests.Count);
        var create = handler.Requests[0];
        Assert.EndsWith("/approot:/Run1.7z:/createUploadSession", create.Url);
        Assert.Equal("Bearer token", create.Authorization);
        Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"fail\"", Encoding.UTF8.GetString(create.Body!));

        var chunks = handler.Requests.Skip(1).ToList();
        Assert.All(chunks, c => Assert.Equal(sessionUrl, c.Url));
        Assert.All(chunks, c => Assert.Null(c.Authorization));
        Assert.Equal(
            new[] { $"bytes 0-{OneDriveApiClient.UploadChunkSize - 1}/{total}", $"bytes {OneDriveApiClient.UploadChunkSize}-{total - 1}/{total}" },
            chunks.Select(c => c.ContentRange));
        Assert.Equal(total, chunks.Sum(c => c.Body!.Length));
    }

    [Fact]
    public async Task Failures_CarryOnlyTheStatus_NotTheResponseBody()
    {
        var handler = new StubHandler(_ =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"SECRET-BODY-MARKER"}"""));
        using var client = new OneDriveApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<OneDriveApiException>(() =>
            client.RefreshTokenAsync(ValidClientId, "refresh-token"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_grant", ex.OAuthError);
        Assert.DoesNotContain("SECRET-BODY-MARKER", ex.Message);
        Assert.DoesNotContain("refresh-token", ex.Message);
    }

    #endregion

    #region 6. OneDrive Provider Lifecycle Tests

    [Fact]
    public async Task OneDriveSyncProvider_PlanAndExecuteParity()
    {
        const string Email = "person@example.com";
        var fakeClient = new FakeOneDriveApiClient();
        var store = new InMemorySecretStore();
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddHours(1));
        var repo = new InMemorySyncRemoteProfileRepository();
        repo.Create(CreateProfile(email: Email));
        var factory = new OneDriveSyncProviderFactory(
            repo,
            fakeClient,
            CreateService(repo, store, fakeClient),
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository(),
            new RecordingDelayProvider());

        using ISyncProvider provider = factory.Create(TestProfileId);

        Assert.IsType<EngineSyncProvider>(provider);
        Assert.Equal("OneDrive", provider.ProviderName);

        // The remote root is persisted in history, so it never carries the account email.
        Assert.Equal("OneDrive: AppRoot (GameSave Manager)", provider.RemoteRoot);
        Assert.DoesNotContain(Email, provider.RemoteRoot, StringComparison.OrdinalIgnoreCase);

        // Preview with no runs
        var plan = await provider.CreatePreviewAsync(new SyncOptions { DryRun = true });
        Assert.NotNull(plan);
        Assert.Equal(0, plan.UploadCount);
        Assert.Equal(0, plan.DownloadCount);
        Assert.Equal(0, plan.InSyncCount);
    }

    #endregion

    #region Helpers & Test Doubles

    private static SyncRemoteProfile CreateProfile(string? email = null, string? accountDisplayName = null) =>
        new(
            Id: TestProfileId,
            DisplayName: "OneDrive",
            ProviderKind: SyncProviderKind.OneDrive,
            AccountDisplayName: accountDisplayName,
            RemoteRootDisplayName: "OneDrive (App Folder)",
            ProviderSettings: new OneDriveSyncRemoteSettings(accountEmail: email, requestedScope: OneDriveAuthorizationScopes.AppFolder),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null);

    private static OneDriveOAuthService CreateService(
        ISyncRemoteProfileRepository repo,
        ISecretStore store,
        IOneDriveApiClient client,
        IUtcClock? clock = null,
        IOneDriveInteractiveAuthorizer? authorizer = null,
        string clientId = ValidClientId) =>
        new(repo, store, client, clock ?? new SystemUtcClock(), authorizer ?? new FakeAuthorizer((_, _, _) => "code"), clientId);

    private static async Task SeedTokenAsync(InMemorySecretStore store, DateTimeOffset expiresAtUtc, string accessToken = "valid-token")
    {
        var tokenData = new StoredOneDriveTokenData(
            AccessToken: accessToken,
            RefreshToken: "refresh-token",
            ExpiresAtUtc: expiresAtUtc,
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);
        await store.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData)));
    }

    private static async Task<StoredOneDriveTokenData> ReadStoredTokenAsync(InMemorySecretStore store)
    {
        var readResult = await store.ReadAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData));
        Assert.Equal(SecretReadStatus.Found, readResult.Status);
        return JsonSerializer.Deserialize<StoredOneDriveTokenData>(Encoding.UTF8.GetString(readResult.Value!))!;
    }

    private static async Task<OneDriveRemoteFileSystem> CreateFileSystemAsync(FakeOneDriveApiClient fakeClient)
    {
        var store = new InMemorySecretStore();
        await SeedTokenAsync(store, DateTimeOffset.UtcNow.AddHours(1));
        var oauth = CreateService(new InMemorySyncRemoteProfileRepository(), store, fakeClient);
        return new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Url, string? Authorization, string? ContentRange, byte[]? Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.OriginalString,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentRange?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class FakeAuthorizer(Func<string, string, string, string> authorize) : IOneDriveInteractiveAuthorizer
    {
        public Task<string> AuthorizeAsync(string authorizationUrl, string redirectUri, string expectedState, CancellationToken cancellationToken = default) =>
            Task.FromResult(authorize(authorizationUrl, redirectUri, expectedState));
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = new();

        public SecretReadResult? ReadOverride { get; init; }
        public SecretOperationResult? StoreOverride { get; init; }
        public SecretOperationResult? DeleteOverride { get; init; }

        public Task<SecretReadResult> ReadAsync(SecretKey key, CancellationToken cancellationToken = default)
        {
            if (ReadOverride is not null)
                return Task.FromResult(ReadOverride);

            string lookup = $"{key.OwnerId}:{key.Name}";
            if (_secrets.TryGetValue(lookup, out byte[]? value))
            {
                return Task.FromResult(SecretReadResult.Found(value));
            }
            return Task.FromResult(SecretReadResult.NotFound());
        }

        public Task<SecretOperationResult> StoreAsync(SecretKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            if (StoreOverride is not null)
                return Task.FromResult(StoreOverride);

            string lookup = $"{key.OwnerId}:{key.Name}";
            _secrets[lookup] = value.ToArray();
            return Task.FromResult(SecretOperationResult.Success(1));
        }

        public Task<SecretOperationResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken = default)
        {
            if (DeleteOverride is not null)
                return Task.FromResult(DeleteOverride);

            string lookup = $"{key.OwnerId}:{key.Name}";
            bool removed = _secrets.Remove(lookup);
            return Task.FromResult(SecretOperationResult.Success(removed ? 1 : 0));
        }

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken cancellationToken = default)
        {
            string lookup = $"{key.OwnerId}:{key.Name}";
            return Task.FromResult(_secrets.ContainsKey(lookup));
        }

        public Task<SecretOperationResult> DeleteAllForOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
        {
            string prefix = $"{ownerId}:";
            var keysToRemove = _secrets.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keysToRemove)
            {
                _secrets.Remove(k);
            }
            return Task.FromResult(SecretOperationResult.Success(keysToRemove.Count));
        }
    }

    private sealed class InMemorySyncRemoteProfileRepository : ISyncRemoteProfileRepository
    {
        private readonly Dictionary<Guid, SyncRemoteProfile> _profiles = new();

        public IReadOnlyList<SyncRemoteProfile> GetAll() => _profiles.Values.ToList();

        public SyncRemoteProfile? GetById(Guid id) =>
            _profiles.TryGetValue(id, out var profile) ? profile : null;

        public SyncRemoteProfile Create(SyncRemoteProfile profile)
        {
            _profiles[profile.Id] = profile;
            return profile;
        }

        public SyncRemoteProfile Update(SyncRemoteProfile profile)
        {
            _profiles[profile.Id] = profile;
            return profile;
        }

        public SyncRemoteProfile Rename(Guid id, string displayName, DateTimeOffset updatedUtc)
        {
            if (!_profiles.TryGetValue(id, out var profile))
                throw new KeyNotFoundException();

            var updated = profile with { DisplayName = displayName, UpdatedUtc = updatedUtc };
            _profiles[id] = updated;
            return updated;
        }

        public void Delete(Guid id) =>
            _profiles.Remove(id);

        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc)
        {
            if (!_profiles.TryGetValue(id, out var profile))
                throw new KeyNotFoundException();

            var updated = profile with { LastUsedUtc = lastUsedUtc };
            _profiles[id] = updated;
            return updated;
        }

        public SyncRemoteProfile UpdateLastSuccessfulConnection(Guid id, DateTimeOffset lastSuccessfulConnectionUtc)
        {
            if (!_profiles.TryGetValue(id, out var profile))
                throw new KeyNotFoundException();

            return profile;
        }
    }

    private sealed class InMemoryTransferHistoryRepository : ITransferHistoryRepository
    {
        public List<TransferRunRecord> Records { get; } = new();

        public long RecordRun(TransferRunRecord record)
        {
            Records.Add(record);
            return Records.Count;
        }

        public IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit) => new List<TransferRunInfo>();

        public IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId) => new List<TransferRunItemRecord>();

        public int CountRuns() => Records.Count;
    }

    private sealed class InMemoryBackupHistoryService : IBackupHistoryService
    {
        public Task<IReadOnlyList<TransferBackupRunInfo>> GetRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferBackupRunInfo>>(new List<TransferBackupRunInfo>());

        public string GetBackupBasePath() => "C:\\FakeBackupBase";
    }

    private sealed class FakeOneDriveApiClient : IOneDriveApiClient
    {
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> UploadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<bool> UploadCreateOnlyFlags { get; } = new();
        public OneDriveTokenResponse? RefreshedToken { get; set; }
        public Exception? RefreshFailure { get; set; }
        public OneDriveQuotaInfo Quota { get; set; } = new(10_000_000_000, 1_000_000_000, 9_000_000_000, "normal");

        public Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(string clientId, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OneDriveTokenResponse(
                AccessToken: "test-access-token",
                RefreshToken: "test-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder));
        }

        public Task<OneDriveTokenResponse> RefreshTokenAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
        {
            if (RefreshFailure is not null)
                return Task.FromException<OneDriveTokenResponse>(RefreshFailure);

            return Task.FromResult(RefreshedToken ?? new OneDriveTokenResponse(
                AccessToken: "refreshed-access-token",
                RefreshToken: "refreshed-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder));
        }

        public Task<OneDriveAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OneDriveAccountInfo(
                DisplayName: "Master Chief",
                EmailOrUpn: "chief@unsc.gov"));
        }

        public Task<OneDriveQuotaInfo> GetQuotaAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Quota);
        }

        public Task<OneDriveItemInfo?> GetItemAsync(string accessToken, string pathUnderAppRoot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(pathUnderAppRoot))
                return Task.FromResult<OneDriveItemInfo?>(new OneDriveItemInfo("approot", IsFolder: true, IsFile: false));

            if (ExistingFiles.Contains(pathUnderAppRoot) || UploadedFiles.ContainsKey(pathUnderAppRoot))
                return Task.FromResult<OneDriveItemInfo?>(new OneDriveItemInfo(Path.GetFileName(pathUnderAppRoot), IsFolder: false, IsFile: true));

            return Task.FromResult<OneDriveItemInfo?>(null);
        }

        public Task<IReadOnlyList<OneDriveItemInfo>> ListChildrenAsync(string accessToken, string pathUnderAppRoot = "", CancellationToken cancellationToken = default)
        {
            var results = new List<OneDriveItemInfo>();
            string prefix = string.IsNullOrEmpty(pathUnderAppRoot) ? "" : pathUnderAppRoot.TrimEnd('/') + "/";
            foreach (var path in ExistingFiles.Concat(UploadedFiles.Keys))
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string sub = path.Substring(prefix.Length);
                    if (sub.IndexOf('/') < 0)
                        results.Add(new OneDriveItemInfo(sub, IsFolder: false, IsFile: true));
                }
            }
            return Task.FromResult<IReadOnlyList<OneDriveItemInfo>>(results);
        }

        public Task<string?> ReadTextAsync(string accessToken, string pathUnderAppRoot, CancellationToken cancellationToken = default)
        {
            if (UploadedFiles.TryGetValue(pathUnderAppRoot, out byte[]? bytes))
            {
                return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            return Task.FromResult<string?>(null);
        }

        public Task UploadContentAsync(string accessToken, string pathUnderAppRoot, Stream contentStream, bool createOnly, CancellationToken cancellationToken = default)
        {
            UploadCreateOnlyFlags.Add(createOnly);

            // Mirrors Graph's conflictBehavior=fail: the server, not a pre-check, refuses.
            if (createOnly && (ExistingFiles.Contains(pathUnderAppRoot) || UploadedFiles.ContainsKey(pathUnderAppRoot)))
            {
                return Task.FromException(new InvalidOperationException(
                    $"Refusing to overwrite existing file in create-only mode: {pathUnderAppRoot}"));
            }

            using var ms = new MemoryStream();
            contentStream.CopyTo(ms);
            UploadedFiles[pathUnderAppRoot] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task DownloadContentAsync(string accessToken, string pathUnderAppRoot, Stream destinationStream, CancellationToken cancellationToken = default)
        {
            if (UploadedFiles.TryGetValue(pathUnderAppRoot, out byte[]? bytes))
            {
                destinationStream.Write(bytes, 0, bytes.Length);
                return Task.CompletedTask;
            }
            throw new FileNotFoundException($"Remote file '{pathUnderAppRoot}' not found.");
        }
    }

    #endregion
}
