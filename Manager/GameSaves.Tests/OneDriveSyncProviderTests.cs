using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        Assert.True(descriptor.Capabilities.SupportsRemoteFolderSelection);
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
    public async Task RestoreAsync_ReturnsNoStoredAuthentication_WhenSecretStoreIsEmpty()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "OneDrive",
            ProviderKind: SyncProviderKind.OneDrive,
            AccountDisplayName: null,
            RemoteRootDisplayName: "OneDrive (App Folder)",
            ProviderSettings: new OneDriveSyncRemoteSettings(accountEmail: null, requestedScope: OneDriveAuthorizationScopes.AppFolder),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        var oauthService = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            new FakeOneDriveApiClient(),
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var result = await oauthService.RestoreAsync(TestProfileId);
        Assert.False(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.NoStoredAuthentication, result.Status);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsConnected_WhenValidTokenInStore()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "OneDrive",
            ProviderKind: SyncProviderKind.OneDrive,
            AccountDisplayName: "Master Chief",
            RemoteRootDisplayName: "OneDrive (App Folder)",
            ProviderSettings: new OneDriveSyncRemoteSettings(accountEmail: "chief@unsc.gov", requestedScope: OneDriveAuthorizationScopes.AppFolder),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "valid-access-token",
            RefreshToken: "valid-refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData));
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData), bytes);

        var oauthService = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            new FakeOneDriveApiClient(),
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var result = await oauthService.RestoreAsync(TestProfileId);
        Assert.True(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.Connected, result.Status);
        Assert.Equal("chief@unsc.gov", result.AccountEmail);
        Assert.Equal("Master Chief", result.AccountDisplayName);
    }

    [Fact]
    public async Task RestoreAsync_RefreshesToken_WhenTokenNearExpiration()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "OneDrive",
            ProviderKind: SyncProviderKind.OneDrive,
            AccountDisplayName: "Master Chief",
            RemoteRootDisplayName: "OneDrive (App Folder)",
            ProviderSettings: new OneDriveSyncRemoteSettings(accountEmail: "chief@unsc.gov", requestedScope: OneDriveAuthorizationScopes.AppFolder),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        // Expires in 2 minutes, which is within the 5-minute refresh window
        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "old-access-token",
            RefreshToken: "old-refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData));
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData), bytes);

        var fakeClient = new FakeOneDriveApiClient
        {
            RefreshedToken = new OneDriveTokenResponse(
                AccessToken: "new-access-token",
                RefreshToken: "new-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1))
        };

        var oauthService = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var result = await oauthService.RestoreAsync(TestProfileId);
        Assert.True(result.Succeeded);
        Assert.Equal(OneDriveAuthenticationStatus.Connected, result.Status);

        // Verify that secret store was updated with new token
        var readResult = await secretStore.ReadAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData));
        Assert.Equal(SecretReadStatus.Found, readResult.Status);
        var updated = JsonSerializer.Deserialize<StoredOneDriveTokenData>(Encoding.UTF8.GetString(readResult.Value!));
        Assert.NotNull(updated);
        Assert.Equal("new-access-token", updated.AccessToken);
        Assert.Equal("new-refresh-token", updated.RefreshToken);
    }

    [Fact]
    public async Task DisconnectAsync_RemovesStoredToken_AndPreservesProfile()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "OneDrive",
            ProviderKind: SyncProviderKind.OneDrive,
            AccountDisplayName: "Cortana",
            RemoteRootDisplayName: "OneDrive (App Folder)",
            ProviderSettings: new OneDriveSyncRemoteSettings(accountEmail: "cortana@unsc.gov", requestedScope: OneDriveAuthorizationScopes.AppFolder),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        byte[] bytes = Encoding.UTF8.GetBytes("dummy-token");
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData), bytes);

        var oauthService = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            new FakeOneDriveApiClient(),
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var disconnectResult = await oauthService.DisconnectAsync(TestProfileId);
        Assert.True(disconnectResult.Succeeded);
        Assert.True(disconnectResult.LocalAuthenticationRemoved);
        Assert.True(disconnectResult.ProfilePreserved);

        // Token removed from store
        var readResult = await secretStore.ReadAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData));
        Assert.Equal(SecretReadStatus.NotFound, readResult.Status);

        // Profile preserved in repository
        var profile = profileRepo.GetById(TestProfileId);
        Assert.NotNull(profile);
        Assert.Null(profile.AccountDisplayName);
    }

    #endregion

    #region 4. OneDriveRemoteFileSystem & Invariants Tests

    [Fact]
    public void OneDriveRemoteFileSystem_SupportsArchiveContainers()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeOneDriveApiClient();
        var oauth = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth, "gamer@example.com");
        Assert.True(fs.SupportsArchiveContainers);
    }

    [Fact]
    public async Task UploadAsync_EnforcesCreateOnly_ThrowsWhenFileAlreadyExists()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeOneDriveApiClient();

        // Seed a stored token
        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "valid-token",
            RefreshToken: "refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData)));

        var oauth = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth, "gamer@example.com");

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
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UploadAsync_Succeeds_WhenFileDoesNotExist()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeOneDriveApiClient();

        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "valid-token",
            RefreshToken: "refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData)));

        var oauth = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth, "gamer@example.com");

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
    public async Task ValidateAsync_ReturnsWarning_WhenQuotaExceeded()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeOneDriveApiClient
        {
            Quota = new OneDriveQuotaInfo(TotalBytes: 5_000_000_000, UsedBytes: 5_000_000_000, RemainingBytes: 0, State: "exceeded")
        };

        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "valid-token",
            RefreshToken: "refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData)));

        var oauth = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth, "gamer@example.com");
        var warning = await fs.ValidateAsync();

        Assert.NotNull(warning);
        Assert.Equal("OneDriveQuotaExceeded", warning.Code);
        Assert.Equal(TransferWarningSeverity.Error, warning.Severity);
    }

    #endregion

    #region 5. OneDriveSyncProvider Lifecycle Tests

    [Fact]
    public async Task OneDriveSyncProvider_PlanAndExecuteParity()
    {
        var secretStore = new InMemorySecretStore();
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeOneDriveApiClient();
        var historyRepo = new InMemoryTransferHistoryRepository();
        var backupHistory = new InMemoryBackupHistoryService();

        var tokenData = new StoredOneDriveTokenData(
            AccessToken: "valid-token",
            RefreshToken: "refresh-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            TokenType: "Bearer",
            Scope: OneDriveAuthorizationScopes.AppFolder);
        await secretStore.StoreAsync(new SecretKey(TestProfileId, SecretNames.OneDriveTokenData),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenData)));

        var oauth = new OneDriveOAuthService(
            profileRepo,
            secretStore,
            fakeClient,
            new SystemUtcClock(),
            authorizer: null,
            clientId: "11111111-2222-3333-4444-555555555555");

        var fs = new OneDriveRemoteFileSystem(TestProfileId, fakeClient, oauth, "gamer@example.com");
        var provider = new OneDriveSyncProvider(fs, backupHistory, historyRepo);

        Assert.Equal("OneDrive", provider.ProviderName);
        Assert.Equal("OneDrive: AppRoot (gamer@example.com)", provider.RemoteRoot);

        // Preview with no runs
        var plan = await provider.CreatePreviewAsync(new SyncOptions { DryRun = true });
        Assert.NotNull(plan);
        Assert.Equal(0, plan.UploadCount);
        Assert.Equal(0, plan.DownloadCount);
        Assert.Equal(0, plan.InSyncCount);
    }

    #endregion

    #region Test Doubles

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _secrets = new();

        public Task<SecretReadResult> ReadAsync(SecretKey key, CancellationToken cancellationToken = default)
        {
            string lookup = $"{key.OwnerId}:{key.Name}";
            if (_secrets.TryGetValue(lookup, out byte[]? value))
            {
                return Task.FromResult(SecretReadResult.Found(value));
            }
            return Task.FromResult(SecretReadResult.NotFound());
        }

        public Task<SecretOperationResult> StoreAsync(SecretKey key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            string lookup = $"{key.OwnerId}:{key.Name}";
            _secrets[lookup] = value.ToArray();
            return Task.FromResult(SecretOperationResult.Success(1));
        }

        public Task<SecretOperationResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken = default)
        {
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
        public OneDriveTokenResponse? RefreshedToken { get; set; }
        public OneDriveQuotaInfo Quota { get; set; } = new(10_000_000_000, 1_000_000_000, 9_000_000_000, "normal");

        public Task<OneDriveTokenResponse> ExchangeCodeForTokenAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OneDriveTokenResponse(
                AccessToken: "test-access-token",
                RefreshToken: "test-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<OneDriveTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RefreshedToken ?? new OneDriveTokenResponse(
                AccessToken: "refreshed-access-token",
                RefreshToken: "refreshed-refresh-token",
                ExpiresInSeconds: 3600,
                TokenType: "Bearer",
                Scope: OneDriveAuthorizationScopes.AppFolder,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<OneDriveAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OneDriveAccountInfo(
                DisplayName: "Master Chief",
                EmailOrUpn: "chief@unsc.gov",
                Id: "user-123"));
        }

        public Task<OneDriveQuotaInfo> GetQuotaAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Quota);
        }

        public Task<OneDriveItemInfo?> GetAppRootAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OneDriveItemInfo?>(new OneDriveItemInfo(
                Id: "approot-id",
                Name: "approot",
                Size: 0,
                IsFolder: true,
                IsFile: false,
                RelativePath: ""));
        }

        public Task<OneDriveItemInfo?> GetItemAsync(string accessToken, string pathUnderAppRoot, CancellationToken cancellationToken = default)
        {
            if (ExistingFiles.Contains(pathUnderAppRoot) || UploadedFiles.ContainsKey(pathUnderAppRoot))
            {
                long size = UploadedFiles.TryGetValue(pathUnderAppRoot, out byte[]? data) ? data.Length : 100;
                return Task.FromResult<OneDriveItemInfo?>(new OneDriveItemInfo(
                    Id: pathUnderAppRoot,
                    Name: Path.GetFileName(pathUnderAppRoot),
                    Size: size,
                    IsFolder: false,
                    IsFile: true,
                    RelativePath: pathUnderAppRoot));
            }
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
                    int slashIdx = sub.IndexOf('/');
                    if (slashIdx < 0)
                    {
                        results.Add(new OneDriveItemInfo(
                            Id: path,
                            Name: sub,
                            Size: 100,
                            IsFolder: false,
                            IsFile: true,
                            RelativePath: path));
                    }
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

        public Task<OneDriveItemInfo> UploadContentAsync(string accessToken, string pathUnderAppRoot, Stream contentStream, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            contentStream.CopyTo(ms);
            UploadedFiles[pathUnderAppRoot] = ms.ToArray();
            return Task.FromResult(new OneDriveItemInfo(
                Id: pathUnderAppRoot,
                Name: Path.GetFileName(pathUnderAppRoot),
                Size: ms.Length,
                IsFolder: false,
                IsFile: true,
                RelativePath: pathUnderAppRoot));
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
