using GameSaves.App.Services;
using GameSaves.Core.Sync;
using GameSaves.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace GameSaves.Tests;

public sealed class SyncRemoteProfileMigrationTests
{
    [Fact]
    public void LegacyLocalFolderSettings_MigrateOnceAndPreservePath()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = temp.GetPath("sync-settings.json");
        var store = new SyncSettingsStore(settingsPath);
        store.Save(SyncUiSettings.Default with
        {
            SchemaVersion = 1,
            LegacyProfileMigrationCompleted = false,
            LocalFolderPath = temp.GetPath("usb")
        });
        var repository = CreateRepository(temp);
        var service = CreateService(store, repository);

        SyncUiSettings migrated = service.LoadAndMigrate();
        SyncRemoteProfile profile = Assert.Single(repository.GetAll());

        Assert.Equal("Migrated Local Folder", profile.DisplayName);
        Assert.Equal(SyncProviderKind.LocalFolder, profile.ProviderKind);
        Assert.Equal(temp.GetPath("usb"),
            Assert.IsType<LocalFolderSyncRemoteSettings>(profile.ProviderSettings).LocalFolderPath);
        Assert.Equal(profile.Id, migrated.SelectedRemoteProfileId);
        Assert.True(migrated.LegacyProfileMigrationCompleted);
        Assert.Equal(SyncUiSettings.CurrentSchemaVersion, migrated.SchemaVersion);

        service.LoadAndMigrate();
        Assert.Single(repository.GetAll());
    }

    [Fact]
    public void LegacyUseSftpSettings_MigrateOnlyAllowlistedFields()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = temp.GetPath("sync-settings.json");
        File.WriteAllText(settingsPath, """
        {
          "UseSftp": true,
          "SftpHost": "legacy.example.test",
          "SftpPort": "2200",
          "SftpUsername": "legacy-user",
          "SftpUsePrivateKey": true,
          "SftpKeyFilePath": "C:\\Keys\\legacy.key",
          "SftpRemotePath": "/legacy-backups",
          "SftpPassword": "must-not-migrate",
          "SftpKeyPassphrase": "must-not-migrate-either"
        }
        """);
        var store = new SyncSettingsStore(settingsPath);
        var repository = CreateRepository(temp);

        SyncUiSettings migrated = CreateService(store, repository).LoadAndMigrate();
        SyncRemoteProfile profile = Assert.Single(repository.GetAll());
        SftpSyncRemoteSettings settings =
            Assert.IsType<SftpSyncRemoteSettings>(profile.ProviderSettings);

        Assert.Equal("Migrated SFTP Server", profile.DisplayName);
        Assert.Equal("legacy.example.test", settings.Host);
        Assert.Equal(2200, settings.Port);
        Assert.Equal("legacy-user", settings.Username);
        Assert.Equal(SftpAuthMethod.PrivateKey, settings.AuthenticationMethod);
        Assert.Equal(@"C:\Keys\legacy.key", settings.PrivateKeyFilePath);
        Assert.Equal("/legacy-backups", settings.RemotePath);
        Assert.Equal(profile.Id, migrated.SelectedRemoteProfileId);
        using var connection = new SqliteConnection(
            $"Data Source={temp.GetPath("data", "gamesave.db")}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT provider_settings_json FROM sync_remote_profiles;";
        string rawJson = (string)command.ExecuteScalar()!;
        Assert.DoesNotContain("must-not-migrate", rawJson);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeletedMigratedProfile_IsNotRecreated()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncSettingsStore(temp.GetPath("sync-settings.json"));
        store.Save(SyncUiSettings.Default with
        {
            SchemaVersion = 1,
            LegacyProfileMigrationCompleted = false,
            LocalFolderPath = temp.GetPath("nas")
        });
        var repository = CreateRepository(temp);
        var service = CreateService(store, repository);
        SyncUiSettings migrated = service.LoadAndMigrate();
        repository.Delete(migrated.SelectedRemoteProfileId!.Value);

        SyncUiSettings restarted = CreateService(store, repository).LoadAndMigrate();

        Assert.Empty(repository.GetAll());
        Assert.True(restarted.LegacyProfileMigrationCompleted);
        Assert.Equal(migrated.SelectedRemoteProfileId, restarted.SelectedRemoteProfileId);
    }

    [Fact]
    public void EmptyOrMalformedSettings_DoNotCreateProfiles()
    {
        using var temp = new TemporaryDirectory();
        string settingsPath = temp.GetPath("sync-settings.json");
        File.WriteAllText(settingsPath, "{ malformed");
        var store = new SyncSettingsStore(settingsPath);
        var repository = CreateRepository(temp);

        SyncUiSettings loaded = CreateService(store, repository).LoadAndMigrate();

        Assert.Empty(repository.GetAll());
        Assert.True(loaded.LegacyProfileMigrationCompleted);
        Assert.Equal("{ malformed", File.ReadAllText(settingsPath));
    }

    private static SqliteSyncRemoteProfileRepository CreateRepository(TemporaryDirectory temp) =>
        new(temp.GetPath("data", "gamesave.db"), new SyncRemoteProfileSettingsSerializer());

    private static SyncRemoteProfileMigrationService CreateService(
        ISyncSettingsStore store,
        ISyncRemoteProfileRepository repository) =>
        new(
            store,
            repository,
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")));
}
