using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GameSaves.Tests;

public sealed class SyncRemoteProfileRepositoryTests
{
    [Fact]
    public void LocalAndSftpProfiles_RoundTripWithStableUniqueIds()
    {
        using var temp = new TemporaryDirectory();
        var repository = CreateRepository(temp);
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        SyncRemoteProfile local = repository.Create(LocalProfile(
            Guid.NewGuid(), "USB Backup", temp.GetPath("usb"), now));
        SyncRemoteProfile sftp = repository.Create(SftpProfile(
            Guid.NewGuid(), "Home SFTP", now));

        SyncRemoteProfile loadedLocal = repository.GetById(local.Id)!;
        SyncRemoteProfile loadedSftp = repository.GetById(sftp.Id)!;

        Assert.NotEqual(local.Id, sftp.Id);
        Assert.Equal(local.Id, loadedLocal.Id);
        Assert.Equal(temp.GetPath("usb"),
            Assert.IsType<LocalFolderSyncRemoteSettings>(loadedLocal.ProviderSettings).LocalFolderPath);
        SftpSyncRemoteSettings settings =
            Assert.IsType<SftpSyncRemoteSettings>(loadedSftp.ProviderSettings);
        Assert.Equal("backup.example.test", settings.Host);
        Assert.Equal(2222, settings.Port);
        Assert.Equal("alice", settings.Username);
        Assert.Equal(SftpAuthMethod.PrivateKey, settings.AuthenticationMethod);
        Assert.Equal(@"C:\Keys\id_ed25519", settings.PrivateKeyFilePath);
        Assert.Equal("/srv/game-saves", settings.RemotePath);
    }

    [Fact]
    public void DisplayNames_AreRequiredTrimmedAndUniqueCaseInsensitively()
    {
        using var temp = new TemporaryDirectory();
        var repository = CreateRepository(temp);
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        SyncRemoteProfile created = repository.Create(LocalProfile(
            Guid.NewGuid(), "  USB Backup  ", temp.GetPath("usb"), now));

        Assert.Equal("USB Backup", created.DisplayName);
        Assert.Throws<ArgumentException>(() => repository.Create(LocalProfile(
            Guid.NewGuid(), "   ", temp.GetPath("other"), now)));
        Assert.Throws<SyncRemoteProfileDuplicateNameException>(() =>
            repository.Create(LocalProfile(
                Guid.NewGuid(), "usb backup", temp.GetPath("other"), now)));
    }

    [Fact]
    public void UpdatePreservesCreatedTimestampAndRenamePreservesSettings()
    {
        using var temp = new TemporaryDirectory();
        var repository = CreateRepository(temp);
        DateTimeOffset created = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        DateTimeOffset updated = created.AddHours(1);
        SyncRemoteProfile profile = repository.Create(LocalProfile(
            Guid.NewGuid(), "USB Backup", temp.GetPath("usb"), created));

        SyncRemoteProfile changed = repository.Update(profile with
        {
            ProviderSettings = new LocalFolderSyncRemoteSettings(temp.GetPath("usb-2")),
            RemoteRootDisplayName = temp.GetPath("usb-2"),
            CreatedUtc = created.AddDays(10),
            UpdatedUtc = updated
        });
        SyncRemoteProfile renamed = repository.Rename(
            changed.Id, "NAS Backup", updated.AddHours(1));

        Assert.Equal(created, changed.CreatedUtc);
        Assert.Equal(updated, changed.UpdatedUtc);
        Assert.Equal("NAS Backup", renamed.DisplayName);
        Assert.Equal(updated.AddHours(1), renamed.UpdatedUtc);
        Assert.Equal(temp.GetPath("usb-2"),
            Assert.IsType<LocalFolderSyncRemoteSettings>(renamed.ProviderSettings).LocalFolderPath);
    }

    [Fact]
    public void Profiles_OrderByRecentUsageThenDisplayName()
    {
        using var temp = new TemporaryDirectory();
        var repository = CreateRepository(temp);
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        SyncRemoteProfile alpha = repository.Create(LocalProfile(
            Guid.NewGuid(), "Alpha", temp.GetPath("a"), now));
        SyncRemoteProfile beta = repository.Create(LocalProfile(
            Guid.NewGuid(), "Beta", temp.GetPath("b"), now));
        SyncRemoteProfile recent = repository.Create(LocalProfile(
            Guid.NewGuid(), "Recent", temp.GetPath("r"), now));

        repository.UpdateLastUsed(beta.Id, now.AddMinutes(1));
        repository.UpdateLastUsed(recent.Id, now.AddMinutes(2));

        Assert.Equal(
            new[] { "Recent", "Beta", "Alpha" },
            repository.GetAll().Select(profile => profile.DisplayName));
        Assert.Null(repository.GetById(Guid.NewGuid()));
        Assert.Throws<SyncRemoteProfileNotFoundException>(() =>
            repository.Delete(Guid.NewGuid()));
    }

    [Fact]
    public void Delete_RemovesOnlyProfileRow()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        var repository = new SqliteSyncRemoteProfileRepository(
            databasePath, new SyncRemoteProfileSettingsSerializer());
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        SyncRemoteProfile profile = repository.Create(LocalProfile(
            Guid.NewGuid(), "USB Backup", temp.GetPath("usb"), now));
        var history = new SqliteTransferHistoryRepository(databasePath);
        history.RecordRun(HistoryRecord(now));
        string backupFile = temp.GetPath("TransferBackups", "run-one", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "backup-data");
        string knownHostsPath = temp.GetPath("sftp-known-hosts.json");
        var knownHosts = new SftpKnownHostsStore(knownHostsPath);
        knownHosts.SaveFingerprint("backup.example.test", 22, "SHA256:test");

        repository.Delete(profile.Id);

        Assert.Null(repository.GetById(profile.Id));
        Assert.Equal(1, history.CountRuns());
        Assert.Equal("backup-data", File.ReadAllText(backupFile));
        Assert.Equal("SHA256:test", knownHosts.GetFingerprint("backup.example.test", 22));
    }

    [Fact]
    public void PersistedSftpJsonAndRawRow_ContainOnlyAllowlistedNonSecretFields()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        var serializer = new SyncRemoteProfileSettingsSerializer();
        var repository = new SqliteSyncRemoteProfileRepository(databasePath, serializer);
        repository.Create(SftpProfile(
            Guid.NewGuid(), "Home SFTP", DateTimeOffset.Parse("2026-07-20T10:00:00Z")));

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT provider_settings_json FROM sync_remote_profiles;";
        string json = (string)command.ExecuteScalar()!;
        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "schemaVersion", "host", "port", "username",
                "authenticationMethod", "privateKeyFilePath", "remotePath"
            },
            names);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(SftpSyncRemoteSettings).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CorruptedAndUnsupportedProviderSettings_LoadAsUnavailable()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        var repository = new SqliteSyncRemoteProfileRepository(
            databasePath, new SyncRemoteProfileSettingsSerializer());
        repository.GetAll();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        InsertRaw(databasePath, Guid.NewGuid(), "Corrupted", 0, "{not-json", now);
        InsertRaw(databasePath, Guid.NewGuid(), "Future Drive", 2,
            """{"schemaVersion":1,"futureField":"preserved"}""", now);

        IReadOnlyList<SyncRemoteProfile> profiles = repository.GetAll();
        SyncRemoteProfile corrupted = profiles.Single(profile => profile.DisplayName == "Corrupted");
        SyncRemoteProfile future = profiles.Single(profile => profile.DisplayName == "Future Drive");

        Assert.Null(corrupted.ProviderSettings);
        Assert.Contains("corrupted", corrupted.SettingsError!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SyncProviderKind.GoogleDrive, future.ProviderKind);
        Assert.Null(future.ProviderSettings);
        Assert.Contains("not implemented", future.SettingsError!, StringComparison.OrdinalIgnoreCase);
    }

    private static SqliteSyncRemoteProfileRepository CreateRepository(TemporaryDirectory temp) =>
        new(temp.GetPath("data", "gamesave.db"), new SyncRemoteProfileSettingsSerializer());

    private static SyncRemoteProfile LocalProfile(
        Guid id,
        string name,
        string path,
        DateTimeOffset now) =>
        new(id, name, SyncProviderKind.LocalFolder, null, path,
            new LocalFolderSyncRemoteSettings(path), now, now, null, null, null);

    private static SyncRemoteProfile SftpProfile(
        Guid id,
        string name,
        DateTimeOffset now) =>
        new(
            id, name, SyncProviderKind.Sftp,
            "alice@backup.example.test",
            "sftp://alice@backup.example.test:2222/srv/game-saves",
            new SftpSyncRemoteSettings(
                "backup.example.test", 2222, "alice",
                SftpAuthMethod.PrivateKey, @"C:\Keys\id_ed25519", "/srv/game-saves"),
            now, now, null, null, null);

    private static TransferRunRecord HistoryRecord(DateTimeOffset now) =>
        new(
            TransferRunKind.Sync, "(backup sync)", "-", "device", "remote",
            false, false, false, 0, 0, 0, 0, 0, 0, null, null,
            now, now, Array.Empty<TransferRunItemRecord>());

    private static void InsertRaw(
        string databasePath,
        Guid id,
        string name,
        int providerKind,
        string json,
        DateTimeOffset now)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_remote_profiles (
                id, display_name, provider_kind, provider_settings_json,
                provider_settings_version, created_utc, updated_utc)
            VALUES ($id, $name, $kind, $json, 1, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$kind", providerKind);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.ExecuteNonQuery();
    }
}
