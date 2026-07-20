using GameSaves.App.Services;
using GameSaves.Core.Sync;

namespace GameSaves.Tests;

public sealed class SyncSettingsStoreTests
{
    [Fact]
    public void ProviderKind_UsesStablePersistedValues()
    {
        Assert.Equal(-1, (int)SyncProviderKind.Unknown);
        Assert.Equal(0, (int)SyncProviderKind.LocalFolder);
        Assert.Equal(1, (int)SyncProviderKind.Sftp);
        Assert.Equal(2, (int)SyncProviderKind.GoogleDrive);
        Assert.Equal(3, (int)SyncProviderKind.WebDav);
        Assert.Equal(4, (int)SyncProviderKind.OneDrive);
    }

    [Fact]
    public void DefaultSettings_SelectLocalFolder()
    {
        Assert.Equal(SyncUiSettings.CurrentSchemaVersion, SyncUiSettings.Default.SchemaVersion);
        Assert.Equal(SyncProviderKind.LocalFolder, SyncUiSettings.Default.SelectedProviderKind);
    }

    [Fact]
    public void LocalFolderSettings_RoundTripInTheNewFormat()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncSettingsStore(temp.GetPath("sync-settings.json"));
        SyncUiSettings expected = CreateSettings(
            SyncProviderKind.LocalFolder,
            localFolderPath: temp.GetPath("remote"));

        store.Save(expected);
        SyncUiSettings actual = store.Load();

        Assert.Equal(expected, actual);
        string json = File.ReadAllText(temp.GetPath("sync-settings.json"));
        Assert.Contains("SchemaVersion", json);
        Assert.Contains("SelectedProviderKind", json);
        Assert.DoesNotContain("UseSftp", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SftpSettings_RoundTripInTheNewFormat()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncSettingsStore(temp.GetPath("sync-settings.json"));
        SyncUiSettings expected = CreateSettings(
            SyncProviderKind.Sftp,
            localFolderPath: temp.GetPath("local"),
            host: "backup.example.test",
            port: "2222",
            username: "alice",
            usePrivateKey: true,
            keyFilePath: temp.GetPath("id_ed25519"),
            remotePath: "/srv/game-saves");

        store.Save(expected);

        Assert.Equal(expected, store.Load());
    }

    [Fact]
    public void SelectedRemoteProfileId_RoundTripsInSchemaTwo()
    {
        using var temp = new TemporaryDirectory();
        var store = new SyncSettingsStore(temp.GetPath("sync-settings.json"));
        Guid profileId = Guid.NewGuid();
        SyncUiSettings expected = SyncUiSettings.Default with
        {
            SelectedRemoteProfileId = profileId,
            SelectedProviderKind = SyncProviderKind.Sftp,
            LegacyProfileMigrationCompleted = true
        };

        store.Save(expected);
        SyncUiSettings actual = store.Load();

        Assert.Equal(profileId, actual.SelectedRemoteProfileId);
        Assert.True(actual.LegacyProfileMigrationCompleted);
        Assert.Equal(SyncUiSettings.CurrentSchemaVersion, actual.SchemaVersion);
    }

    [Theory]
    [InlineData(false, SyncProviderKind.LocalFolder)]
    [InlineData(true, SyncProviderKind.Sftp)]
    public void LegacyUseSftp_MigratesToProviderKind(
        bool useSftp,
        SyncProviderKind expectedKind)
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        File.WriteAllText(path, $$"""
        {
          "UseSftp": {{useSftp.ToString().ToLowerInvariant()}}
        }
        """);

        SyncUiSettings loaded = new SyncSettingsStore(path).Load();

        Assert.Equal(expectedKind, loaded.SelectedProviderKind);
    }

    [Fact]
    public void LegacySettings_PreserveEveryNonSecretConnectionValue()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        File.WriteAllText(path, """
        {
          "SftpRemotePath": "/gamesave-sync",
          "SftpUsername": "legacy-user",
          "LocalFolderPath": "D:\\Backups",
          "SftpUsePrivateKey": true,
          "UseSftp": true,
          "SftpKeyFilePath": "C:\\Keys\\legacy.key",
          "SftpPort": "2200",
          "SftpHost": "legacy.example.test"
        }
        """);

        SyncUiSettings loaded = new SyncSettingsStore(path).Load();

        Assert.Equal(SyncProviderKind.Sftp, loaded.SelectedProviderKind);
        Assert.Equal(@"D:\Backups", loaded.LocalFolderPath);
        Assert.Equal("legacy.example.test", loaded.SftpHost);
        Assert.Equal("2200", loaded.SftpPort);
        Assert.Equal("legacy-user", loaded.SftpUsername);
        Assert.True(loaded.SftpUsePrivateKey);
        Assert.Equal(@"C:\Keys\legacy.key", loaded.SftpKeyFilePath);
        Assert.Equal("/gamesave-sync", loaded.SftpRemotePath);
    }

    [Fact]
    public void LegacyMigration_IsIdempotentAndDoesNotRewriteOnLoad()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        const string legacy = """
        {
          "UseSftp": true,
          "SftpHost": "legacy.example.test"
        }
        """;
        File.WriteAllText(path, legacy);
        var store = new SyncSettingsStore(path);

        SyncUiSettings first = store.Load();
        SyncUiSettings second = store.Load();

        Assert.Equal(first, second);
        Assert.Equal(legacy, File.ReadAllText(path));

        store.Save(first);
        Assert.Equal(first, store.Load());
        Assert.DoesNotContain("UseSftp", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PasswordsAndPassphrases_AreNeverPersisted()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        File.WriteAllText(path, """
        {
          "UseSftp": true,
          "SftpPassword": "do-not-keep",
          "SftpKeyPassphrase": "also-do-not-keep",
          "SftpHost": "server"
        }
        """);
        var store = new SyncSettingsStore(path);

        store.Save(store.Load());
        string saved = File.ReadAllText(path);

        Assert.DoesNotContain("do-not-keep", saved);
        Assert.DoesNotContain("also-do-not-keep", saved);
        Assert.DoesNotContain("Password", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passphrase", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(SyncUiSettings).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CorruptedSettings_FallBackSafelyWithoutRewriting()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        const string corrupted = "{ this is not json";
        File.WriteAllText(path, corrupted);

        SyncUiSettings loaded = new SyncSettingsStore(path).Load();

        Assert.Equal(SyncUiSettings.Default, loaded);
        Assert.Equal(corrupted, File.ReadAllText(path));
    }

    [Fact]
    public void UnknownProviderValue_IsPreservedAndNeverMappedToAnImplementedProvider()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        File.WriteAllText(path, """
        {
          "SchemaVersion": 9,
          "SelectedProviderKind": 99,
          "LocalFolderPath": "D:\\Preserved",
          "SftpHost": "preserved.example.test"
        }
        """);

        SyncUiSettings loaded = new SyncSettingsStore(path).Load();

        Assert.Equal(9, loaded.SchemaVersion);
        Assert.Equal(99, (int)loaded.SelectedProviderKind);
        Assert.NotEqual(SyncProviderKind.LocalFolder, loaded.SelectedProviderKind);
        Assert.NotEqual(SyncProviderKind.Sftp, loaded.SelectedProviderKind);
        Assert.Equal(@"D:\Preserved", loaded.LocalFolderPath);
        Assert.Equal("preserved.example.test", loaded.SftpHost);
    }

    [Fact]
    public void MissingOptionalFields_UseSafeDefaults()
    {
        using var temp = new TemporaryDirectory();
        string path = temp.GetPath("sync-settings.json");
        File.WriteAllText(path, """{ "UseSftp": false }""");

        SyncUiSettings loaded = new SyncSettingsStore(path).Load();

        Assert.Equal(SyncProviderKind.LocalFolder, loaded.SelectedProviderKind);
        Assert.Equal("22", loaded.SftpPort);
        Assert.Equal("/gamesave-sync", loaded.SftpRemotePath);
    }

    private static SyncUiSettings CreateSettings(
        SyncProviderKind kind,
        string localFolderPath = "",
        string host = "",
        string port = "22",
        string username = "",
        bool usePrivateKey = false,
        string keyFilePath = "",
        string remotePath = "/gamesave-sync")
    {
        return new SyncUiSettings(
            SchemaVersion: SyncUiSettings.CurrentSchemaVersion,
            SelectedProviderKind: kind,
            LocalFolderPath: localFolderPath,
            SftpHost: host,
            SftpPort: port,
            SftpUsername: username,
            SftpUsePrivateKey: usePrivateKey,
            SftpKeyFilePath: keyFilePath,
            SftpRemotePath: remotePath);
    }
}
