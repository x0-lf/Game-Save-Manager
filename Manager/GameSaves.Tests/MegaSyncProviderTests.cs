using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Mega;
using GameSaves.Infrastructure.Sync;
using Xunit;

namespace GameSaves.Tests;

public sealed class MegaSyncProviderTests
{
    private static readonly Guid TestProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    #region 1. Catalog & Capabilities Tests

    [Fact]
    public void SyncProviderCatalog_ReflectsMegaAsImplementedAndAvailable()
    {
        var catalog = new SyncProviderCatalog();
        var descriptor = catalog.GetDescriptor(SyncProviderKind.Mega);

        Assert.NotNull(descriptor);
        Assert.True(descriptor.IsImplemented);
        Assert.Null(descriptor.UnavailableMessage);
        Assert.True(descriptor.IsConfigurationAvailable);
        Assert.Equal("MEGA", descriptor.DisplayName);
        Assert.Equal(SyncProviderConfigurationSurface.ServerCredentials, descriptor.ConfigurationSurface);
        Assert.True(descriptor.Capabilities.SupportsPersistentAuthentication);
        Assert.True(descriptor.Capabilities.SupportsLogout);
        Assert.True(descriptor.Capabilities.SupportsRemoteQuota);
        Assert.True(descriptor.Capabilities.SupportsResumableUpload);
        Assert.True(descriptor.Capabilities.RequiresServerCredentials);
        Assert.False(descriptor.Capabilities.RequiresInteractiveLogin);
        Assert.False(descriptor.Capabilities.SupportsRemoteFolderSelection);
        Assert.False(descriptor.Capabilities.SupportsOpenRemoteLocation);
    }

    #endregion

    #region 2. Settings Serializer Tests

    [Fact]
    public void SyncRemoteProfileSettingsSerializer_RoundTripsMegaSettings()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        var original = new MegaSyncRemoteSettings(
            userEmail: "gamer@example.com",
            rootFolderNodeId: "node_root_123",
            rootFolderName: "GameSave Manager Backups");

        string json = serializer.Serialize(SyncProviderKind.Mega, original);
        Assert.NotNull(json);

        var readResult = serializer.Deserialize(SyncProviderKind.Mega, MegaSyncRemoteSettings.CurrentSchemaVersion, json);
        Assert.Null(readResult.Error);

        var deserialized = readResult.Settings as MegaSyncRemoteSettings;
        Assert.NotNull(deserialized);
        Assert.Equal("gamer@example.com", deserialized.UserEmail);
        Assert.Equal("node_root_123", deserialized.RootFolderNodeId);
        Assert.Equal("GameSave Manager Backups", deserialized.RootFolderName);
        Assert.Equal(MegaSyncRemoteSettings.CurrentSchemaVersion, deserialized.SchemaVersion);
    }

    [Fact]
    public void SyncRemoteProfileSettingsSerializer_HandlesNullOrMissingRootFolder_WithDefault()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            userEmail = "gamer@example.com"
        });

        var readResult = serializer.Deserialize(SyncProviderKind.Mega, 1, json);
        Assert.Null(readResult.Error);
        var deserialized = readResult.Settings as MegaSyncRemoteSettings;
        Assert.NotNull(deserialized);
        Assert.Equal("GameSave Manager Backups", deserialized.RootFolderName);
    }

    [Fact]
    public void SyncRemoteProfileSettingsSerializer_RejectsCorruptJson()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        var readResult = serializer.Deserialize(SyncProviderKind.Mega, 1, "not-json");
        Assert.NotNull(readResult.Error);
        Assert.Null(readResult.Settings);
    }

    #endregion

    #region 3. Session Service & Secret Store Tests

    [Fact]
    public async Task MegaSessionService_RestoreSession_ReturnsNoSession_WhenSecretStoreIsEmpty()
    {
        var secretStore = new InMemorySecretStore();
        var fakeClient = new FakeMegaApiClient();
        var sessionService = new MegaSessionService(fakeClient, secretStore);

        var session = await sessionService.GetSessionAsync(TestProfileId);
        Assert.Null(session);

        bool hasValid = await sessionService.HasValidSessionAsync(TestProfileId);
        Assert.False(hasValid);
    }

    [Fact]
    public async Task MegaSessionService_AuthenticateAsync_ProtectsSessionInSecretStore()
    {
        var secretStore = new InMemorySecretStore();
        var fakeClient = new FakeMegaApiClient
        {
            LoginResult = MegaAuthenticationResult.Success("gamer@example.com", "sess_token_abc")
        };
        var sessionService = new MegaSessionService(fakeClient, secretStore);

        var result = await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "MyP@ssword!");
        Assert.True(result.Succeeded);
        Assert.Equal(MegaAuthenticationStatus.Connected, result.Status);

        // Verify stored in secret store under MegaSessionData
        var readResult = await secretStore.ReadAsync(new SecretKey(TestProfileId, SecretNames.MegaSessionData));
        Assert.Equal(SecretReadStatus.Found, readResult.Status);
        Assert.NotNull(readResult.Value);

        var retrieved = await sessionService.GetSessionAsync(TestProfileId);
        Assert.NotNull(retrieved);
        Assert.Equal("gamer@example.com", retrieved.UserEmail);
        Assert.Equal("sess_token_abc", retrieved.SessionId);
    }

    [Fact]
    public async Task MegaSessionService_GetQuotaAsync_RetrievesQuota_WhenSessionValid()
    {
        var secretStore = new InMemorySecretStore();
        var fakeClient = new FakeMegaApiClient
        {
            LoginResult = MegaAuthenticationResult.Success("gamer@example.com", "sess_token_abc")
        };
        fakeClient.Quota = new MegaQuotaInfo(TotalBytes: 50_000_000_000, UsedBytes: 10_000_000_000, RemainingBytes: 40_000_000_000);
        var sessionService = new MegaSessionService(fakeClient, secretStore);

        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "MyP@ssword!");

        MegaQuotaInfo? quota = await sessionService.GetQuotaAsync(TestProfileId);
        Assert.NotNull(quota);
        Assert.Equal(50_000_000_000, quota.TotalBytes);
        Assert.Equal(10_000_000_000, quota.UsedBytes);
        Assert.Equal(40_000_000_000, quota.RemainingBytes);
    }

    [Fact]
    public async Task MegaSessionService_DisconnectAsync_RemovesStoredSession_AndPreservesProfile()
    {
        var secretStore = new InMemorySecretStore();
        var fakeClient = new FakeMegaApiClient
        {
            LoginResult = MegaAuthenticationResult.Success("gamer@example.com", "sess_token_abc")
        };
        var sessionService = new MegaSessionService(fakeClient, secretStore);

        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "MyP@ssword!");
        Assert.True(await sessionService.HasValidSessionAsync(TestProfileId));

        var disconnectResult = await sessionService.DisconnectAsync(TestProfileId);
        Assert.True(disconnectResult.Succeeded);
        Assert.True(disconnectResult.LocalSessionRemoved);

        Assert.False(await sessionService.HasValidSessionAsync(TestProfileId));
        var readResult = await secretStore.ReadAsync(new SecretKey(TestProfileId, SecretNames.MegaSessionData));
        Assert.Equal(SecretReadStatus.NotFound, readResult.Status);
    }

    #endregion

    #region 4. MegaRemoteFileSystem & Invariants Tests

    [Fact]
    public void MegaRemoteFileSystem_SupportsArchiveContainers_IsTrue()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        Assert.True(fs.SupportsArchiveContainers);
        Assert.Equal("MEGA: GameSave Manager Backups (gamer@example.com)", fs.DisplayRoot);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_EnforcesDedicatedRootFolder()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com", "Custom Root");

        Assert.Equal("MEGA: Custom Root (gamer@example.com)", fs.DisplayRoot);
        Assert.False(await fs.RootExistsAsync());

        // Create file under root
        await fs.CreateTextFileIfMissingAsync("test.txt", "hello");
        Assert.True(await fs.RootExistsAsync());
        Assert.Contains(fakeClient.Nodes, n => n.Name == "Custom Root" && n.Type == MegaNodeType.Folder);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ListRunFolderNames_DiscoversFoldersUnderRoot()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        // Seed root folder and runs
        var root = fakeClient.AddFolder("root", "GameSave Manager Backups");
        fakeClient.AddFolder(root.Id, "GameA_2026-01-01");
        fakeClient.AddFolder(root.Id, "GameB_2026-01-02");
        fakeClient.AddFolder(root.Id, ".hidden_run");

        var runs = await fs.ListRunFolderNamesAsync();
        Assert.Equal(2, runs.Count);
        Assert.Contains("GameA_2026-01-01", runs);
        Assert.Contains("GameB_2026-01-02", runs);
        Assert.DoesNotContain(".hidden_run", runs);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ListRunArchiveNames_DiscoversArchivesUnderRoot()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        var root = fakeClient.AddFolder("root", "GameSave Manager Backups");
        fakeClient.AddFile(root.Id, "GameA_2026-01-01.zip", Encoding.UTF8.GetBytes("zip-data"));
        fakeClient.AddFile(root.Id, "GameB_2026-01-02.7z", Encoding.UTF8.GetBytes("7z-data"));
        fakeClient.AddFile(root.Id, "readme.txt", Encoding.UTF8.GetBytes("text"));

        var archives = await fs.ListRunArchiveNamesAsync();
        Assert.Equal(2, archives.Count);
        Assert.Contains("GameA_2026-01-01.zip", archives);
        Assert.Contains("GameB_2026-01-02.7z", archives);
        Assert.DoesNotContain("readme.txt", archives);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_CreateOnlyGuard_ThrowsWhenFileAlreadyExists()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        var root = fakeClient.AddFolder("root", "GameSave Manager Backups");
        var runFolder = fakeClient.AddFolder(root.Id, "Run1");
        fakeClient.AddFile(runFolder.Id, "save.dat", Encoding.UTF8.GetBytes("existing-data"));

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "new data");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fs.UploadFileAsync(tempFile, "Run1/save.dat"));

            Assert.Contains("Refusing to overwrite existing file in create-only mode", ex.Message);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MegaRemoteFileSystem_UploadFileAsync_Succeeds_WhenFileDoesNotExist()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "brand-new-data");
            long bytes = await fs.UploadFileAsync(tempFile, "Run2/savegame.sav");

            Assert.True(bytes > 0);
            Assert.Contains("savegame.sav", fakeClient.UploadedFileNames);
            Assert.True(await fs.FileExistsAsync("Run2/savegame.sav"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MegaRemoteFileSystem_DownloadFileAsync_DownloadsAndCreatesLocalDirectory()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        var root = fakeClient.AddFolder("root", "GameSave Manager Backups");
        var runFolder = fakeClient.AddFolder(root.Id, "Run3");
        fakeClient.AddFile(runFolder.Id, "payload.bin", Encoding.UTF8.GetBytes("payload-contents"));

        string targetDir = Path.Combine(Path.GetTempPath(), "gsm_mega_test_" + Guid.NewGuid().ToString("N"));
        string targetFile = Path.Combine(targetDir, "sub", "payload.bin");

        try
        {
            long bytes = await fs.DownloadFileAsync("Run3/payload.bin", targetFile);
            Assert.True(File.Exists(targetFile));
            Assert.Equal("payload-contents", await File.ReadAllTextAsync(targetFile));
            Assert.Equal(new FileInfo(targetFile).Length, bytes);
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ZeroDeletion_NeverDeletesRunsDuringSync()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        await fs.CreateTextFileIfMissingAsync("Run1/manifest.json", "{}");

        Assert.Equal(0, fakeClient.DeleteCallCount);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ValidateAsync_ReturnsError_WhenAuthMissing()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");
        var warning = await fs.ValidateAsync();

        Assert.NotNull(warning);
        Assert.Equal("MegaAuthRequired", warning.Code);
        Assert.Equal(TransferWarningSeverity.Error, warning.Severity);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ValidateAsync_ReturnsError_WhenQuotaExceeded()
    {
        var fakeClient = new FakeMegaApiClient
        {
            Quota = new MegaQuotaInfo(TotalBytes: 50_000_000_000, UsedBytes: 50_000_000_000, RemainingBytes: 0)
        };
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");
        var warning = await fs.ValidateAsync();

        Assert.NotNull(warning);
        Assert.Equal("MegaQuotaExceeded", warning.Code);
        Assert.Equal(TransferWarningSeverity.Error, warning.Severity);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ValidateAsync_ReturnsWarning_WhenLowStorage()
    {
        var fakeClient = new FakeMegaApiClient
        {
            // 4% remaining (< 10%)
            Quota = new MegaQuotaInfo(TotalBytes: 100_000_000_000, UsedBytes: 96_000_000_000, RemainingBytes: 4_000_000_000)
        };
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");
        var warning = await fs.ValidateAsync();

        Assert.NotNull(warning);
        Assert.Equal("MegaLowStorage", warning.Code);
        Assert.Equal(TransferWarningSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task MegaRemoteFileSystem_ProviderMetadata_AllowsAllowlistedMetadataOnly()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        // Allowed metadata
        await fs.ReplaceProviderMetadataAsync(".gamesave-sync/metadata.json", "{\"test\":true}");
        string? read = await fs.ReadProviderMetadataAsync(".gamesave-sync/metadata.json");
        Assert.Equal("{\"test\":true}", read);

        // Non-allowlisted metadata throws InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fs.ReplaceProviderMetadataAsync("arbitrary.json", "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fs.ReadProviderMetadataAsync("arbitrary.json"));
    }

    #endregion

    #region 5. MegaSyncProvider Lifecycle & SyncEngine Tests

    [Fact]
    public async Task MegaSyncProvider_PlanAndExecuteParity()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        await sessionService.AuthenticateAsync(TestProfileId, "gamer@example.com", "pass");

        var historyRepo = new InMemoryTransferHistoryRepository();
        var backupHistory = new InMemoryBackupHistoryService();

        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");
        var provider = new MegaSyncProvider(fs, backupHistory, historyRepo);

        Assert.Equal("Mega", provider.ProviderName);
        Assert.Equal("MEGA: GameSave Manager Backups (gamer@example.com)", provider.RemoteRoot);

        // Preview with no runs
        var plan = await provider.CreatePreviewAsync(new SyncOptions { DryRun = true });
        Assert.NotNull(plan);
        Assert.Equal(0, plan.UploadCount);
        Assert.Equal(0, plan.DownloadCount);
        Assert.Equal(0, plan.InSyncCount);

        var log = await provider.GetSyncLogAsync();
        Assert.NotNull(log);
    }

    [Fact]
    public void MegaSyncProvider_Disposed_ThrowsObjectDisposedException()
    {
        var fakeClient = new FakeMegaApiClient();
        var secretStore = new InMemorySecretStore();
        var sessionService = new MegaSessionService(fakeClient, secretStore);
        var fs = new MegaRemoteFileSystem(TestProfileId, fakeClient, sessionService, "gamer@example.com");

        var provider = new MegaSyncProvider(fs, new InMemoryBackupHistoryService(), new InMemoryTransferHistoryRepository());
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            provider.CreatePreviewAsync(new SyncOptions()).GetAwaiter().GetResult());
    }

    #endregion

    #region 6. Factory Resolution Tests

    [Fact]
    public void MegaSyncProviderFactory_Throws_WhenProfileNotFound()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var fakeClient = new FakeMegaApiClient();
        var sessionService = new MegaSessionService(fakeClient, new InMemorySecretStore());
        var fsFactory = new MegaRemoteFileSystemFactory(profileRepo, fakeClient, sessionService);

        var factory = new MegaSyncProviderFactory(
            profileRepo,
            fsFactory,
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(Guid.NewGuid()));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void MegaSyncProviderFactory_Throws_WhenProfileNotMega()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        var nonMegaId = Guid.NewGuid();
        profileRepo.Create(new SyncRemoteProfile(
            Id: nonMegaId,
            DisplayName: "Local Profile",
            ProviderKind: SyncProviderKind.LocalFolder,
            AccountDisplayName: null,
            RemoteRootDisplayName: @"C:\Folder",
            ProviderSettings: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        var fakeClient = new FakeMegaApiClient();
        var sessionService = new MegaSessionService(fakeClient, new InMemorySecretStore());
        var fsFactory = new MegaRemoteFileSystemFactory(profileRepo, fakeClient, sessionService);

        var factory = new MegaSyncProviderFactory(
            profileRepo,
            fsFactory,
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(nonMegaId));

        Assert.Contains("is not a MEGA profile", ex.Message);
    }

    [Fact]
    public void MegaSyncProviderFactory_CreatesProvider_WhenProfileValid()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "My Mega Drive",
            ProviderKind: SyncProviderKind.Mega,
            AccountDisplayName: "Cloud Gamer",
            RemoteRootDisplayName: "GameSave Manager Backups",
            ProviderSettings: new MegaSyncRemoteSettings("gamer@example.com"),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        var fakeClient = new FakeMegaApiClient();
        var sessionService = new MegaSessionService(fakeClient, new InMemorySecretStore());
        var fsFactory = new MegaRemoteFileSystemFactory(profileRepo, fakeClient, sessionService);

        var factory = new MegaSyncProviderFactory(
            profileRepo,
            fsFactory,
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository());

        var provider = factory.Create(TestProfileId);
        Assert.NotNull(provider);
        Assert.Equal("Mega", provider.ProviderName);
        Assert.Equal("MEGA: GameSave Manager Backups (gamer@example.com)", provider.RemoteRoot);
    }

    [Fact]
    public void SyncProviderFactory_CreateMegaProvider_ResolvesThroughInternalFactory()
    {
        var profileRepo = new InMemorySyncRemoteProfileRepository();
        profileRepo.Create(new SyncRemoteProfile(
            Id: TestProfileId,
            DisplayName: "My Mega Drive",
            ProviderKind: SyncProviderKind.Mega,
            AccountDisplayName: "Cloud Gamer",
            RemoteRootDisplayName: "GameSave Manager Backups",
            ProviderSettings: new MegaSyncRemoteSettings("gamer@example.com"),
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            LastUsedUtc: null,
            LastSuccessfulConnectionUtc: null,
            RemoteFolderId: null));

        var fakeClient = new FakeMegaApiClient();
        var sessionService = new MegaSessionService(fakeClient, new InMemorySecretStore());
        var fsFactory = new MegaRemoteFileSystemFactory(profileRepo, fakeClient, sessionService);
        var megaFactory = new MegaSyncProviderFactory(
            profileRepo,
            fsFactory,
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository());

        var topFactory = new SyncProviderFactory(
            new InMemoryBackupHistoryService(),
            new InMemoryTransferHistoryRepository(),
            new TestDatabasePathProvider(Path.Combine(Path.GetTempPath(), "test.db")),
            new FakeGoogleDriveSyncProviderFactory(),
            oneDriveProviders: null,
            megaProviders: megaFactory);

        var provider = topFactory.CreateMegaProvider(TestProfileId);
        Assert.NotNull(provider);
        Assert.Equal("Mega", provider.ProviderName);
    }

    #endregion

    #region Test Doubles

    private sealed class FakeGoogleDriveSyncProviderFactory : IGoogleDriveSyncProviderFactory
    {
        public ISyncProvider Create(Guid remoteProfileId) => throw new NotImplementedException();
    }

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

    private sealed class FakeMegaApiClient : IMegaApiClient
    {
        private int _nextId = 100;
        private readonly List<MegaNode> _nodes = new()
        {
            new MegaNode("root", null, MegaNodeType.Root, "My Cloud", 0, DateTimeOffset.UtcNow)
        };
        private readonly Dictionary<string, byte[]> _fileData = new();

        public IReadOnlyList<MegaNode> Nodes => _nodes;
        public MegaAuthenticationResult LoginResult { get; set; } =
            MegaAuthenticationResult.Success("gamer@example.com", "fake_session_123");
        public MegaQuotaInfo Quota { get; set; } =
            new MegaQuotaInfo(20L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, 15L * 1024 * 1024 * 1024);
        public List<string> UploadedFileNames { get; } = new();
        public int DeleteCallCount { get; private set; }

        public MegaNode AddFolder(string parentId, string name)
        {
            string id = $"folder_{Interlocked.Increment(ref _nextId)}";
            var node = new MegaNode(id, parentId, MegaNodeType.Folder, name, 0, DateTimeOffset.UtcNow);
            _nodes.Add(node);
            return node;
        }

        public MegaNode AddFile(string parentId, string name, byte[] content)
        {
            string id = $"file_{Interlocked.Increment(ref _nextId)}";
            var node = new MegaNode(id, parentId, MegaNodeType.File, name, content.Length, DateTimeOffset.UtcNow);
            _nodes.Add(node);
            _fileData[id] = content;
            return node;
        }

        public Task<MegaAuthenticationResult> LoginAsync(string email, string password, string? twoFactorCode = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(LoginResult);

        public Task<MegaQuotaInfo> GetQuotaAsync(MegaSessionToken session, CancellationToken cancellationToken = default) =>
            Task.FromResult(Quota);

        public Task<IReadOnlyList<MegaNode>> GetNodesAsync(MegaSessionToken session, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MegaNode>>(_nodes.ToList());

        public Task<MegaNode> CreateFolderAsync(MegaSessionToken session, string parentNodeId, string folderName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AddFolder(parentNodeId, folderName));
        }

        public Task<MegaNode> UploadFileChunkedAsync(
            MegaSessionToken session,
            string parentFolderNodeId,
            string fileName,
            Stream contentStream,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            contentStream.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            UploadedFileNames.Add(fileName);
            return Task.FromResult(AddFile(parentFolderNodeId, fileName, bytes));
        }

        public Task<Stream> DownloadFileAsync(MegaSessionToken session, string fileNodeId, CancellationToken cancellationToken = default)
        {
            if (_fileData.TryGetValue(fileNodeId, out byte[]? bytes))
            {
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            }
            throw new FileNotFoundException($"Node '{fileNodeId}' not found.");
        }

        public Task<string?> ReadTextFileAsync(MegaSessionToken session, string fileNodeId, CancellationToken cancellationToken = default)
        {
            if (_fileData.TryGetValue(fileNodeId, out byte[]? bytes))
            {
                return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            return Task.FromResult<string?>(null);
        }

        public Task<bool> DeleteNodeAsync(MegaSessionToken session, string nodeId, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            _nodes.RemoveAll(n => n.Id == nodeId);
            return Task.FromResult(true);
        }
    }

    #endregion
}
