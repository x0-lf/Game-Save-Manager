using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using GameSaves.Core.Transfers;
using GameSaves.Infrastructure.Secrets;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace GameSaves.Tests;

public sealed class WindowsDpapiSecretStoreTests
{
    [Fact]
    public async Task CurrentUserDpapi_RoundTripsPasswordBinaryAndOAuthData()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        Guid owner = Guid.NewGuid();
        var passwordKey = new SecretKey(owner, SecretNames.WebDavPassword);
        var binaryKey = new SecretKey(owner, SecretNames.SftpPrivateKeyPassphrase);
        var tokenKey = new SecretKey(owner, SecretNames.OAuthTokenData);
        byte[] password = Encoding.UTF8.GetBytes("password-marker");
        byte[] binary = { 0, 1, 2, 127, 128, 255 };
        byte[] token = Encoding.UTF8.GetBytes(
            """{"access_token":"access","refresh_token":"refresh"}""");

        await store.StoreAsync(passwordKey, password);
        await store.StoreAsync(binaryKey, binary);
        await store.StoreAsync(tokenKey, token);

        Assert.Equal(DataProtectionScope.CurrentUser,
            WindowsDpapiSecretStore.ProtectionScope);
        Assert.Equal(password, (await store.ReadAsync(passwordKey)).Value);
        Assert.Equal(binary, (await store.ReadAsync(binaryKey)).Value);
        Assert.Equal(token, (await store.ReadAsync(tokenKey)).Value);
    }

    [Fact]
    public async Task SqliteStoresOnlyCiphertextBlobAndNeverPlaintextMarker()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        const string marker = "UNIQUE-PLAINTEXT-MARKER-20260720";
        byte[] plaintext = Encoding.UTF8.GetBytes(marker);

        await store.StoreAsync(key, plaintext);

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT protection_scheme, format_version, protected_payload
            FROM protected_sync_secrets
            WHERE owner_id = $owner AND secret_name = $name;
            """;
        command.Parameters.AddWithValue("$owner", key.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$name", key.Name);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(WindowsDpapiSecretStore.ProtectionScheme, reader.GetString(0));
        Assert.Equal(WindowsDpapiSecretStore.FormatVersion, reader.GetInt32(1));
        byte[] ciphertext = (byte[])reader.GetValue(2);
        Assert.NotEqual(plaintext, ciphertext);
        Assert.True(ciphertext.AsSpan().IndexOf(plaintext) < 0);
        reader.Close();
        command.Dispose();
        connection.Dispose();
        SqliteConnection.ClearAllPools();

        byte[] databaseBytes = File.ReadAllBytes(databasePath);
        Assert.True(databaseBytes.AsSpan().IndexOf(plaintext) < 0);
    }

    [Fact]
    public async Task StoreUpdatesExistingSecretAndExistsDoesNotDecrypt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);

        await store.StoreAsync(key, new byte[] { 1, 2, 3 });
        Assert.True(await store.ExistsAsync(key));
        await store.StoreAsync(key, new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 4, 5, 6 }, (await store.ReadAsync(key)).Value);
        Assert.Equal(1, CountRows(temp, key.OwnerId));
    }

    [Fact]
    public async Task DeleteAndDeleteAll_AreScopedToTheRequestedKeysAndOwner()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        var first = new SecretKey(firstOwner, SecretNames.OAuthTokenData);
        var second = new SecretKey(firstOwner, SecretNames.WebDavPassword);
        var other = new SecretKey(secondOwner, SecretNames.OAuthTokenData);
        await store.StoreAsync(first, new byte[] { 1 });
        await store.StoreAsync(second, new byte[] { 2 });
        await store.StoreAsync(other, new byte[] { 3 });

        await store.DeleteAsync(first);
        Assert.False(await store.ExistsAsync(first));
        Assert.True(await store.ExistsAsync(second));

        SecretOperationResult result =
            await store.DeleteAllForOwnerAsync(firstOwner);
        Assert.Equal(1, result.AffectedCount);
        Assert.False(await store.ExistsAsync(second));
        Assert.True(await store.ExistsAsync(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedOrTruncatedCiphertext_ReturnsCorrupted(bool truncate)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        await store.StoreAsync(key, Encoding.UTF8.GetBytes("protected-value"));

        MutatePayload(temp, key, payload =>
            truncate
                ? payload.Take(Math.Max(1, payload.Length / 3)).ToArray()
                : payload.Select((value, index) =>
                    index == payload.Length / 2 ? (byte)(value ^ 0xFF) : value).ToArray());

        SecretReadResult result = await store.ReadAsync(key);
        Assert.Equal(SecretReadStatus.Corrupted, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CiphertextCannotBeMovedToAnotherSecretKey()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        var original = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        var copied = new SecretKey(Guid.NewGuid(), SecretNames.OneDriveTokenData);
        await store.StoreAsync(original, Encoding.UTF8.GetBytes("protected-value"));
        CopyRowToDifferentKey(temp, original, copied);

        SecretReadResult result = await store.ReadAsync(copied);
        Assert.Equal(SecretReadStatus.Corrupted, result.Status);
    }

    [Fact]
    public async Task UnknownProtectionVersion_IsCorruptedButCanBeDeleted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        await store.StoreAsync(key, Encoding.UTF8.GetBytes("protected-value"));

        Execute(temp, """
            UPDATE protected_sync_secrets
            SET format_version = 99
            WHERE owner_id = $owner AND secret_name = $name;
            """, key);

        Assert.Equal(
            SecretReadStatus.Corrupted,
            (await store.ReadAsync(key)).Status);
        Assert.True((await store.DeleteAsync(key)).Succeeded);
        Assert.False(await store.ExistsAsync(key));
    }

    [Fact]
    public async Task ProfileDeletionRemovesSecretsButPreservesHistoryBackupsAndKnownHosts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        var repository = new SqliteSyncRemoteProfileRepository(
            databasePath,
            new SyncRemoteProfileSettingsSerializer(new SyncProviderCatalog()));
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        SyncRemoteProfile profile = repository.Create(new SyncRemoteProfile(
            Guid.NewGuid(), "USB Backup", SyncProviderKind.LocalFolder,
            null, temp.GetPath("remote"),
            new LocalFolderSyncRemoteSettings(temp.GetPath("remote")),
            now, now, null, null, null));
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(profile.Id, SecretNames.OAuthTokenData);
        await store.StoreAsync(key, Encoding.UTF8.GetBytes("protected-value"));
        var history = new SqliteTransferHistoryRepository(databasePath);
        history.RecordRun(new TransferRunRecord(
            TransferRunKind.Sync, "(backup sync)", "-", "device", "remote",
            false, false, false, 0, 0, 0, 0, 0, 0, null, null,
            now, now, Array.Empty<TransferRunItemRecord>()));
        string backupPath = temp.GetPath("TransferBackups", "run", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.WriteAllText(backupPath, "backup-data");
        string knownHostsPath = temp.GetPath("sftp-known-hosts.json");
        var knownHosts = new SftpKnownHostsStore(knownHostsPath);
        knownHosts.SaveFingerprint("host.example", 22, "SHA256:test");

        var service = new SyncRemoteProfileService(repository, store);
        SyncRemoteProfileDeleteResult result = await service.DeleteAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.False(await store.ExistsAsync(key));
        Assert.Null(repository.GetById(profile.Id));
        Assert.Equal(1, history.CountRuns());
        Assert.Equal("backup-data", File.ReadAllText(backupPath));
        Assert.Equal("SHA256:test", knownHosts.GetFingerprint("host.example", 22));
    }

    [Fact]
    public async Task DisconnectRemovesSecretsAndPreservesProfile()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        string databasePath = temp.GetPath("data", "gamesave.db");
        var repository = new SqliteSyncRemoteProfileRepository(
            databasePath,
            new SyncRemoteProfileSettingsSerializer());
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        SyncRemoteProfile profile = repository.Create(new SyncRemoteProfile(
            Guid.NewGuid(), "USB Backup", SyncProviderKind.LocalFolder,
            null, temp.GetPath("remote"),
            new LocalFolderSyncRemoteSettings(temp.GetPath("remote")),
            now, now, null, null, null));
        WindowsDpapiSecretStore store = CreateStore(temp);
        await store.StoreAsync(
            new SecretKey(profile.Id, SecretNames.OAuthTokenData),
            new byte[] { 42 });
        var service = new SyncRemoteProfileService(repository, store);

        SyncRemoteProfileAuthenticationResult result =
            await service.DisconnectAuthenticationAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.NotNull(repository.GetById(profile.Id));
        Assert.Equal(0, CountRows(temp, profile.Id));
    }

    [Fact]
    public async Task UnsupportedPlatform_ReturnsUnavailableWithoutConstructionFailure()
    {
        using var temp = new TemporaryDirectory();
        var store = new WindowsDpapiSecretStore(
            temp.GetPath("data", "gamesave.db"),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")),
            isWindows: () => false);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);

        Assert.Equal(
            SecretOperationStatus.Unavailable,
            (await store.StoreAsync(key, new byte[] { 1 })).Status);
        Assert.Equal(
            SecretReadStatus.Unavailable,
            (await store.ReadAsync(key)).Status);
        Assert.Equal(
            SecretOperationStatus.Unavailable,
            (await store.DeleteAsync(key)).Status);
        Assert.False(await store.ExistsAsync(key));
    }

    [Fact]
    public async Task SecretValuesNeverAppearInFailureFormatting()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        WindowsDpapiSecretStore store = CreateStore(temp);
        var key = new SecretKey(Guid.NewGuid(), SecretNames.OAuthTokenData);
        const string marker = "NEVER-SHOW-THIS-SECRET";
        await store.StoreAsync(key, Encoding.UTF8.GetBytes(marker));
        MutatePayload(temp, key, _ => new byte[] { 1, 2, 3 });

        SecretReadResult result = await store.ReadAsync(key);
        Assert.DoesNotContain(marker, result.ToString());
        Assert.DoesNotContain(marker, result.ErrorCode ?? string.Empty);
    }

    private static WindowsDpapiSecretStore CreateStore(TemporaryDirectory temp) =>
        new(
            temp.GetPath("data", "gamesave.db"),
            new FixedUtcClock(DateTimeOffset.Parse("2026-07-20T12:00:00Z")));

    private static int CountRows(TemporaryDirectory temp, Guid ownerId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={temp.GetPath("data", "gamesave.db")}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM protected_sync_secrets WHERE owner_id = $owner;";
        command.Parameters.AddWithValue("$owner", ownerId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void MutatePayload(
        TemporaryDirectory temp,
        SecretKey key,
        Func<byte[], byte[]> mutate)
    {
        using var connection = new SqliteConnection(
            $"Data Source={temp.GetPath("data", "gamesave.db")}");
        connection.Open();
        byte[] original;

        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT protected_payload FROM protected_sync_secrets
                WHERE owner_id = $owner AND secret_name = $name;
                """;
            select.Parameters.AddWithValue("$owner", key.OwnerId.ToString("D"));
            select.Parameters.AddWithValue("$name", key.Name);
            original = (byte[])select.ExecuteScalar()!;
        }

        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = """
            UPDATE protected_sync_secrets SET protected_payload = $payload
            WHERE owner_id = $owner AND secret_name = $name;
            """;
        update.Parameters.Add("$payload", SqliteType.Blob).Value = mutate(original);
        update.Parameters.AddWithValue("$owner", key.OwnerId.ToString("D"));
        update.Parameters.AddWithValue("$name", key.Name);
        update.ExecuteNonQuery();
    }

    private static void CopyRowToDifferentKey(
        TemporaryDirectory temp,
        SecretKey original,
        SecretKey copied)
    {
        using var connection = new SqliteConnection(
            $"Data Source={temp.GetPath("data", "gamesave.db")}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO protected_sync_secrets (
                owner_id, secret_name, protection_scheme, format_version,
                protected_payload, created_utc, updated_utc)
            SELECT $new_owner, $new_name, protection_scheme, format_version,
                   protected_payload, created_utc, updated_utc
            FROM protected_sync_secrets
            WHERE owner_id = $owner AND secret_name = $name;
            """;
        command.Parameters.AddWithValue("$new_owner", copied.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$new_name", copied.Name);
        command.Parameters.AddWithValue("$owner", original.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$name", original.Name);
        command.ExecuteNonQuery();
    }

    private static void Execute(
        TemporaryDirectory temp,
        string sql,
        SecretKey key)
    {
        using var connection = new SqliteConnection(
            $"Data Source={temp.GetPath("data", "gamesave.db")}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", key.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$name", key.Name);
        command.ExecuteNonQuery();
    }
}
