using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.GoogleDrive;
using GameSaves.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class GoogleDriveConnectionSettingsTests
{
    private static readonly Guid ProfileId =
        Guid.Parse("7b28c445-0b18-49cf-88af-585615b92485");

    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-07-22T12:00:00Z");

    [Fact]
    public void PersistedSettings_UseSchemaVersionOneAndExactDriveFileScope()
    {
        var settings = new GoogleDriveSyncRemoteSettings(
            "user@example.invalid",
            GoogleDriveAuthorizationScopes.DriveFile);

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(1, GoogleDriveSyncRemoteSettings.CurrentSchemaVersion);
        Assert.Equal(
            "https://www.googleapis.com/auth/drive.file",
            GoogleDriveAuthorizationScopes.DriveFile);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile, settings.RequestedScope);
    }

    [Theory]
    [InlineData("https://www.googleapis.com/auth/drive")]
    [InlineData("https://www.googleapis.com/auth/drive.readonly")]
    [InlineData("")]
    [InlineData(" https://www.googleapis.com/auth/drive.file ")]
    public void PersistedSettings_RejectUnsupportedOrNonExactScopes(string scope)
    {
        Assert.Throws<ArgumentException>(() =>
            new GoogleDriveSyncRemoteSettings(null, scope));
    }

    [Fact]
    public void RuntimeSettings_RejectEmptyProfileIdAndUndefinedStatus()
    {
        Assert.Throws<ArgumentException>(() => RuntimeSettings(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeSettings(
            ProfileId,
            (GoogleDriveConnectionStatus)99));
    }

    [Fact]
    public void OptionalMetadata_NormalizesBlankAndPreservesUnicode()
    {
        var persisted = new GoogleDriveSyncRemoteSettings(
            "   ",
            GoogleDriveAuthorizationScopes.DriveFile);
        var runtime = new GoogleDriveConnectionSettings(
            ProfileId,
            "  使用者 Αλίκη  ",
            persisted.AccountEmail,
            "   ",
            "  備份 Δίσκος  ",
            persisted.RequestedScope,
            GoogleDriveConnectionStatus.Disconnected,
            hasStoredToken: false);

        Assert.Null(persisted.AccountEmail);
        Assert.Equal("使用者 Αλίκη", runtime.AccountDisplayName);
        Assert.Null(runtime.AccountEmail);
        Assert.Null(runtime.RootFolderId);
        Assert.Equal("備份 Δίσκος", runtime.RootFolderDisplayName);
    }

    [Fact]
    public void RuntimeSettings_ContainAllRoadmapFieldsAndSafeDerivedState()
    {
        GoogleDriveConnectionSettings settings = RuntimeSettings(ProfileId);

        Assert.Equal(ProfileId, settings.RemoteProfileId);
        Assert.Equal("Example Account", settings.AccountDisplayName);
        Assert.Equal("user@example.invalid", settings.AccountEmail);
        Assert.Equal("folder-id-without-format-assumptions", settings.RootFolderId);
        Assert.Equal("GameSave Manager Backups", settings.RootFolderDisplayName);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile, settings.RequestedScope);
        Assert.Equal(GoogleDriveConnectionStatus.Disconnected, settings.ConnectionStatus);
        Assert.False(settings.HasStoredToken);
        Assert.True(settings.HasAccountMetadata);
        Assert.True(settings.HasRootFolder);
        Assert.True(settings.RequiresAuthentication);
        Assert.DoesNotContain("user@example.invalid", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Example Account", settings.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GoogleDriveConnectionStatus.Unknown, 0)]
    [InlineData(GoogleDriveConnectionStatus.NotConfigured, 1)]
    [InlineData(GoogleDriveConnectionStatus.Disconnected, 2)]
    [InlineData(GoogleDriveConnectionStatus.StoredAuthenticationAvailable, 3)]
    [InlineData(GoogleDriveConnectionStatus.Connecting, 4)]
    [InlineData(GoogleDriveConnectionStatus.Connected, 5)]
    [InlineData(GoogleDriveConnectionStatus.ReauthenticationRequired, 6)]
    [InlineData(GoogleDriveConnectionStatus.Unavailable, 7)]
    [InlineData(GoogleDriveConnectionStatus.Failed, 8)]
    public void ConnectionStatus_HasStableNumericValues(
        GoogleDriveConnectionStatus status,
        int expected)
    {
        Assert.Equal(expected, (int)status);
    }

    [Fact]
    public void Serializer_UsesOnlyExplicitGoogleDriveAllowlist()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        string json = serializer.Serialize(
            SyncProviderKind.GoogleDrive,
            new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile));
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "schemaVersion", "accountEmail", "requestedScope" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasStoredToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionStatus", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serializer_DeserializesValidGoogleDriveSettings()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        const string json =
            "{\"schemaVersion\":1,\"accountEmail\":\"user@example.invalid\",\"requestedScope\":\"https://www.googleapis.com/auth/drive.file\"}";

        SyncRemoteProfileSettingsReadResult result = serializer.Deserialize(
            SyncProviderKind.GoogleDrive,
            1,
            json);

        GoogleDriveSyncRemoteSettings settings =
            Assert.IsType<GoogleDriveSyncRemoteSettings>(result.Settings);
        Assert.Null(result.Error);
        Assert.Equal("user@example.invalid", settings.AccountEmail);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile, settings.RequestedScope);
    }

    [Fact]
    public void Serializer_RejectsUnsupportedGoogleDriveScope()
    {
        var serializer = new SyncRemoteProfileSettingsSerializer();
        const string json =
            "{\"schemaVersion\":1,\"accountEmail\":null,\"requestedScope\":\"https://www.googleapis.com/auth/drive\"}";

        SyncRemoteProfileSettingsReadResult result = serializer.Deserialize(
            SyncProviderKind.GoogleDrive,
            1,
            json);

        Assert.Null(result.Settings);
        Assert.Contains("scope", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleDriveProfile_RoundTripsThroughExistingSqliteColumns()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        SqliteSyncRemoteProfileRepository repository = CreateRepository(databasePath);
        SyncRemoteProfile created = repository.Create(GoogleProfile());

        SyncRemoteProfile loaded = repository.GetById(ProfileId)!;
        GoogleDriveSyncRemoteSettings settings =
            Assert.IsType<GoogleDriveSyncRemoteSettings>(loaded.ProviderSettings);

        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("使用者 Account", loaded.AccountDisplayName);
        Assert.Equal("user@example.invalid", settings.AccountEmail);
        Assert.Equal("folder-id-without-format-assumptions", loaded.RemoteFolderId);
        Assert.Equal("備份 Backups", loaded.RemoteRootDisplayName);
        Assert.Equal(GoogleDriveAuthorizationScopes.DriveFile, settings.RequestedScope);
        Assert.Null(loaded.SettingsError);
    }

    [Fact]
    public void RawSqliteProfileRow_ContainsNoTokenOrRuntimeState()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        CreateRepository(databasePath).Create(GoogleProfile());

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT provider_settings_json FROM sync_remote_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", ProfileId.ToString("D"));
        string json = (string)command.ExecuteScalar()!;

        Assert.DoesNotContain("ACCESS_TOKEN_MARKER_7F8B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("REFRESH_TOKEN_MARKER_7F8B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIENT_ID_MARKER_7F8B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_ReturnsSafeResultForMissingProfile()
    {
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(null),
            new TrackingSecretStore());

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.Equal(GoogleDriveConnectionSettingsResultStatus.ProfileNotFound, result.Status);
        Assert.Equal(GoogleDriveConnectionErrorCodes.ProfileNotFound, result.ErrorCode);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task Service_ReturnsSafeResultForWrongProviderKind()
    {
        SyncRemoteProfile profile = GoogleProfile() with
        {
            ProviderKind = SyncProviderKind.LocalFolder,
            ProviderSettings = new LocalFolderSyncRemoteSettings("X:\\Backups")
        };
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(profile),
            new TrackingSecretStore());

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.Equal(GoogleDriveConnectionSettingsResultStatus.WrongProviderKind, result.Status);
        Assert.Equal(GoogleDriveConnectionErrorCodes.WrongProviderKind, result.ErrorCode);
    }

    [Fact]
    public async Task Service_ReturnsSafeResultForMissingSettings()
    {
        SyncRemoteProfile profile = GoogleProfile() with { ProviderSettings = null };
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(profile),
            new TrackingSecretStore());

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.Equal(GoogleDriveConnectionSettingsResultStatus.SettingsMissing, result.Status);
        Assert.Equal(GoogleDriveConnectionErrorCodes.SettingsMissing, result.ErrorCode);
    }

    [Theory]
    [InlineData("{not-json", 1, GoogleDriveConnectionSettingsResultStatus.SettingsCorrupted)]
    [InlineData("{\"schemaVersion\":2,\"accountEmail\":null,\"requestedScope\":\"https://www.googleapis.com/auth/drive.file\"}", 2, GoogleDriveConnectionSettingsResultStatus.SettingsCorrupted)]
    [InlineData("{\"schemaVersion\":1,\"accountEmail\":null,\"requestedScope\":\"https://www.googleapis.com/auth/drive\"}", 1, GoogleDriveConnectionSettingsResultStatus.UnsupportedScope)]
    public async Task Service_HandlesUnreadableVersionedAndUnsupportedScopeSettings(
        string json,
        int settingsVersion,
        GoogleDriveConnectionSettingsResultStatus expectedStatus)
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        SqliteSyncRemoteProfileRepository repository = CreateRepository(databasePath);
        repository.GetAll();
        InsertRawGoogleProfile(databasePath, json, settingsVersion);
        var service = Service(repository, new TrackingSecretStore());

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Settings);
        Assert.NotNull(result.ErrorCode);
        Assert.DoesNotContain("user@example.invalid", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStoredOAuthToken_ProducesDisconnectedState()
    {
        var store = new TrackingSecretStore();
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(GoogleProfile()),
            store);

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.True(result.Succeeded);
        Assert.False(result.Settings!.HasStoredToken);
        Assert.Equal(
            GoogleDriveConnectionStatus.Disconnected,
            result.Settings.ConnectionStatus);
        Assert.Equal(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData),
            Assert.Single(store.ExistsKeys));
    }

    [Fact]
    public async Task StoredOAuthToken_ProducesAvailableButNotConnectedState()
    {
        var store = new TrackingSecretStore();
        await store.StoreAsync(
            new SecretKey(ProfileId, SecretNames.OAuthTokenData),
            Encoding.UTF8.GetBytes("TOKEN_BYTES_ARE_NEVER_READ"));
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(GoogleProfile()),
            store);

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.True(result.Settings!.HasStoredToken);
        Assert.Equal(
            GoogleDriveConnectionStatus.StoredAuthenticationAvailable,
            result.Settings.ConnectionStatus);
        Assert.NotEqual(GoogleDriveConnectionStatus.Connected, result.Settings.ConnectionStatus);
        Assert.Equal(0, store.ReadCallCount);
    }

    [Theory]
    [InlineData(SecretNames.SftpPassword)]
    [InlineData(SecretNames.OneDriveTokenData)]
    [InlineData(SecretNames.WebDavPassword)]
    public async Task UnrelatedStoredSecret_DoesNotSetGoogleTokenFlag(string secretName)
    {
        var store = new TrackingSecretStore();
        await store.StoreAsync(
            new SecretKey(ProfileId, secretName),
            new byte[] { 1, 2, 3 });
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(GoogleProfile()),
            store);

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.False(result.Settings!.HasStoredToken);
        Assert.Equal(GoogleDriveConnectionStatus.Disconnected, result.Settings.ConnectionStatus);
        Assert.Equal(SecretNames.OAuthTokenData, Assert.Single(store.ExistsKeys).Name);
        Assert.Equal(0, store.ReadCallCount);
    }

    [Fact]
    public async Task SecretStoreFailure_ReturnsSafeUnavailableResult()
    {
        var store = new TrackingSecretStore { ThrowWhenCheckingExistence = true };
        GoogleDriveConnectionSettingsService service = Service(
            new SingleProfileRepository(GoogleProfile()),
            store);

        GoogleDriveConnectionSettingsResult result = await service.GetAsync(ProfileId);

        Assert.Equal(
            GoogleDriveConnectionSettingsResultStatus.SecretStoreUnavailable,
            result.Status);
        Assert.Equal(
            GoogleDriveConnectionErrorCodes.SecretStoreUnavailable,
            result.ErrorCode);
        Assert.DoesNotContain("token", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_DependsOnlyOnGameSaveManagerOwnedBoundaries()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(GoogleDriveConnectionSettingsService).GetConstructors());

        Assert.Equal(
            new[]
            {
                typeof(ISyncRemoteProfileRepository),
                typeof(ISecretStore),
                typeof(ISyncProviderCatalog)
            },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(GoogleDriveConnectionSettingsService).GetMethods(),
            method => method.ReturnType.FullName?.StartsWith("Google.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void InfrastructureRegistration_ContainsConnectionSettingsService()
    {
        var services = new ServiceCollection();

        services.AddGameSavesInfrastructure();

        ServiceDescriptor registration = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IGoogleDriveConnectionSettingsService));
        Assert.Equal(typeof(GoogleDriveConnectionSettingsService), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void GoogleDrive_RemainsPersistableButUnavailableForExecution()
    {
        var catalog = new SyncProviderCatalog();
        var serializer = new SyncRemoteProfileSettingsSerializer(catalog);

        Assert.NotEmpty(serializer.Serialize(
            SyncProviderKind.GoogleDrive,
            new GoogleDriveSyncRemoteSettings(
                null,
                GoogleDriveAuthorizationScopes.DriveFile)));
        Assert.False(catalog.GetDescriptor(SyncProviderKind.GoogleDrive).IsImplemented);
        Assert.DoesNotContain(
            catalog.GetDescriptor(SyncProviderKind.GoogleDrive),
            catalog.GetAll().Where(descriptor => descriptor.IsImplemented));
    }

    private static GoogleDriveConnectionSettings RuntimeSettings(
        Guid id,
        GoogleDriveConnectionStatus status = GoogleDriveConnectionStatus.Disconnected) =>
        new(
            id,
            "Example Account",
            "user@example.invalid",
            "folder-id-without-format-assumptions",
            "GameSave Manager Backups",
            GoogleDriveAuthorizationScopes.DriveFile,
            status,
            hasStoredToken: false);

    private static SyncRemoteProfile GoogleProfile() =>
        new(
            ProfileId,
            "Personal Google Drive",
            SyncProviderKind.GoogleDrive,
            "使用者 Account",
            "備份 Backups",
            new GoogleDriveSyncRemoteSettings(
                "user@example.invalid",
                GoogleDriveAuthorizationScopes.DriveFile),
            Timestamp,
            Timestamp,
            null,
            null,
            "folder-id-without-format-assumptions");

    private static SqliteSyncRemoteProfileRepository CreateRepository(string databasePath) =>
        new(databasePath, new SyncRemoteProfileSettingsSerializer());

    private static GoogleDriveConnectionSettingsService Service(
        ISyncRemoteProfileRepository repository,
        ISecretStore secretStore) =>
        new(repository, secretStore, new SyncProviderCatalog());

    private static void InsertRawGoogleProfile(
        string databasePath,
        string json,
        int settingsVersion)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_remote_profiles (
                id, display_name, provider_kind, account_display_name,
                remote_root_display_name, provider_settings_json,
                provider_settings_version, remote_folder_id, created_utc, updated_utc)
            VALUES (
                $id, $name, $kind, $account, $root_name, $json,
                $version, $root_id, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$name", "Raw Google Drive");
        command.Parameters.AddWithValue("$kind", (int)SyncProviderKind.GoogleDrive);
        command.Parameters.AddWithValue("$account", "Example Account");
        command.Parameters.AddWithValue("$root_name", "Backups");
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$version", settingsVersion);
        command.Parameters.AddWithValue("$root_id", "folder-id");
        command.Parameters.AddWithValue("$created", Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$updated", Timestamp.ToString("O"));
        command.ExecuteNonQuery();
    }

    private sealed class SingleProfileRepository : ISyncRemoteProfileRepository
    {
        private readonly SyncRemoteProfile? _profile;

        public SingleProfileRepository(SyncRemoteProfile? profile) => _profile = profile;

        public IReadOnlyList<SyncRemoteProfile> GetAll() =>
            _profile is null ? Array.Empty<SyncRemoteProfile>() : new[] { _profile };

        public SyncRemoteProfile? GetById(Guid id) =>
            _profile?.Id == id ? _profile : null;

        public SyncRemoteProfile Create(SyncRemoteProfile profile) => throw new NotSupportedException();
        public SyncRemoteProfile Update(SyncRemoteProfile profile) => throw new NotSupportedException();
        public SyncRemoteProfile Rename(Guid id, string displayName, DateTimeOffset updatedUtc) => throw new NotSupportedException();
        public void Delete(Guid id) => throw new NotSupportedException();
        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc) => throw new NotSupportedException();
        public SyncRemoteProfile UpdateLastSuccessfulConnection(Guid id, DateTimeOffset lastSuccessfulConnectionUtc) => throw new NotSupportedException();
    }

    private sealed class TrackingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public List<SecretKey> ExistsKeys { get; } = new();

        public int ReadCallCount { get; private set; }

        public bool ThrowWhenCheckingExistence { get; set; }

        public Task<SecretOperationResult> StoreAsync(
            SecretKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            _inner.StoreAsync(key, value, cancellationToken);

        public Task<SecretReadResult> ReadAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            return _inner.ReadAsync(key, cancellationToken);
        }

        public Task<SecretOperationResult> DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(key, cancellationToken);

        public Task<bool> ExistsAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            ExistsKeys.Add(key);

            if (ThrowWhenCheckingExistence)
                throw new InvalidOperationException("Simulated secret store failure.");

            return _inner.ExistsAsync(key, cancellationToken);
        }

        public Task<SecretOperationResult> DeleteAllForOwnerAsync(
            Guid ownerId,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteAllForOwnerAsync(ownerId, cancellationToken);
    }
}
