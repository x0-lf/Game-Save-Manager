using GameSaves.Core.Secrets;
using GameSaves.Core.Sync;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA1416 // Every DPAPI call is guarded by the injected Windows platform check.

namespace GameSaves.Infrastructure.Secrets
{
    /// <summary>
    /// Stores DPAPI CurrentUser ciphertext in the application SQLite database.
    /// Construction is platform-safe; DPAPI is invoked only by store/read.
    /// </summary>
    public sealed class WindowsDpapiSecretStore : ISecretStore
    {
        internal const string ProtectionScheme = "dpapi-current-user-v1";
        internal const int FormatVersion = 1;
        internal static DataProtectionScope ProtectionScope =>
            DataProtectionScope.CurrentUser;

        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly IUtcClock _clock;
        private readonly Func<bool> _isWindows;

        public WindowsDpapiSecretStore(
            string databasePath,
            IUtcClock clock)
            : this(databasePath, clock, OperatingSystem.IsWindows)
        {
        }

        internal WindowsDpapiSecretStore(
            string databasePath,
            IUtcClock clock,
            Func<bool> isWindows)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _databasePath = databasePath;
            _clock = clock;
            _isWindows = isWindows;
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true
            }.ToString();
        }

        public Task<SecretOperationResult> StoreAsync(
            SecretKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isWindows())
            {
                return Task.FromResult(
                    SecretOperationResult.Unavailable("SecretStorePlatformUnsupported"));
            }

            byte[] plaintext = value.ToArray();
            byte[] entropy = BuildEntropy(key);
            byte[]? protectedPayload = null;

            try
            {
                protectedPayload = ProtectedData.Protect(
                    plaintext,
                    entropy,
                    ProtectionScope);

                using SqliteConnection connection = OpenConnection();
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO protected_sync_secrets (
                        owner_id, secret_name, protection_scheme, format_version,
                        protected_payload, created_utc, updated_utc)
                    VALUES (
                        $owner_id, $secret_name, $protection_scheme, $format_version,
                        $protected_payload, $created_utc, $updated_utc)
                    ON CONFLICT(owner_id, secret_name) DO UPDATE SET
                        protection_scheme = excluded.protection_scheme,
                        format_version = excluded.format_version,
                        protected_payload = excluded.protected_payload,
                        updated_utc = excluded.updated_utc;
                    """;
                string timestamp = ToUtcText(_clock.UtcNow);
                AddKeyParameters(command, key);
                command.Parameters.AddWithValue("$protection_scheme", ProtectionScheme);
                command.Parameters.AddWithValue("$format_version", FormatVersion);
                command.Parameters.Add(
                    "$protected_payload",
                    SqliteType.Blob).Value = protectedPayload;
                command.Parameters.AddWithValue("$created_utc", timestamp);
                command.Parameters.AddWithValue("$updated_utc", timestamp);
                command.ExecuteNonQuery();
                transaction.Commit();
                return Task.FromResult(SecretOperationResult.Success(1));
            }
            catch (PlatformNotSupportedException)
            {
                return Task.FromResult(
                    SecretOperationResult.Unavailable("SecretStorePlatformUnsupported"));
            }
            catch (CryptographicException)
            {
                return Task.FromResult(
                    SecretOperationResult.Failed("SecretProtectionFailed"));
            }
            catch (SqliteException)
            {
                return Task.FromResult(
                    SecretOperationResult.Failed("SecretStoreWriteFailed"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(entropy);
            }
        }

        public Task<SecretReadResult> ReadAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isWindows())
            {
                return Task.FromResult(
                    SecretReadResult.Unavailable("SecretStorePlatformUnsupported"));
            }

            byte[]? entropy = null;
            byte[]? plaintext = null;

            try
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT protection_scheme, format_version, protected_payload
                    FROM protected_sync_secrets
                    WHERE owner_id = $owner_id AND secret_name = $secret_name;
                    """;
                AddKeyParameters(command, key);

                using SqliteDataReader reader = command.ExecuteReader();

                if (!reader.Read())
                    return Task.FromResult(SecretReadResult.NotFound());

                string protectionScheme = reader.GetString(0);
                int formatVersion = reader.GetInt32(1);

                if (!protectionScheme.Equals(
                        ProtectionScheme,
                        StringComparison.Ordinal) ||
                    formatVersion != FormatVersion)
                {
                    return Task.FromResult(
                        SecretReadResult.Corrupted("SecretProtectionFormatUnsupported"));
                }

                byte[] protectedPayload = (byte[])reader.GetValue(2);
                entropy = BuildEntropy(key);
                plaintext = ProtectedData.Unprotect(
                    protectedPayload,
                    entropy,
                    ProtectionScope);

                return Task.FromResult(SecretReadResult.Found(plaintext));
            }
            catch (PlatformNotSupportedException)
            {
                return Task.FromResult(
                    SecretReadResult.Unavailable("SecretStorePlatformUnsupported"));
            }
            catch (CryptographicException)
            {
                return Task.FromResult(
                    SecretReadResult.Corrupted("SecretDataUnreadable"));
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult(
                    SecretReadResult.Corrupted("SecretDataCorrupted"));
            }
            catch (InvalidCastException)
            {
                return Task.FromResult(
                    SecretReadResult.Corrupted("SecretDataCorrupted"));
            }
            catch (SqliteException)
            {
                return Task.FromResult(
                    SecretReadResult.Failed("SecretStoreReadFailed"));
            }
            finally
            {
                if (plaintext is not null)
                    CryptographicOperations.ZeroMemory(plaintext);
                if (entropy is not null)
                    CryptographicOperations.ZeroMemory(entropy);
            }
        }

        public Task<SecretOperationResult> DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isWindows())
            {
                return Task.FromResult(
                    SecretOperationResult.Unavailable("SecretStorePlatformUnsupported"));
            }

            try
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM protected_sync_secrets
                    WHERE owner_id = $owner_id AND secret_name = $secret_name;
                    """;
                AddKeyParameters(command, key);
                return Task.FromResult(
                    SecretOperationResult.Success(command.ExecuteNonQuery()));
            }
            catch (SqliteException)
            {
                return Task.FromResult(
                    SecretOperationResult.Failed("SecretStoreDeleteFailed"));
            }
        }

        public Task<bool> ExistsAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isWindows())
                return Task.FromResult(false);

            try
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT 1
                    FROM protected_sync_secrets
                    WHERE owner_id = $owner_id AND secret_name = $secret_name;
                    """;
                AddKeyParameters(command, key);
                return Task.FromResult(command.ExecuteScalar() is not null);
            }
            catch (SqliteException)
            {
                return Task.FromResult(false);
            }
        }

        public Task<SecretOperationResult> DeleteAllForOwnerAsync(
            Guid ownerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ownerId == Guid.Empty)
                throw new ArgumentException("A non-empty secret owner ID is required.", nameof(ownerId));

            if (!_isWindows())
            {
                return Task.FromResult(
                    SecretOperationResult.Unavailable("SecretStorePlatformUnsupported"));
            }

            try
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM protected_sync_secrets WHERE owner_id = $owner_id;";
                command.Parameters.AddWithValue("$owner_id", ownerId.ToString("D"));
                int deleted = command.ExecuteNonQuery();
                transaction.Commit();
                return Task.FromResult(SecretOperationResult.Success(deleted));
            }
            catch (SqliteException)
            {
                return Task.FromResult(
                    SecretOperationResult.Failed("SecretStoreDeleteFailed"));
            }
        }

        private SqliteConnection OpenConnection()
        {
            string? directory = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS protected_sync_secrets (
                    owner_id TEXT NOT NULL,
                    secret_name TEXT NOT NULL,
                    protection_scheme TEXT NOT NULL,
                    format_version INTEGER NOT NULL,
                    protected_payload BLOB NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (owner_id, secret_name)
                );

                CREATE INDEX IF NOT EXISTS idx_protected_sync_secrets_owner
                    ON protected_sync_secrets(owner_id);
                """;
            command.ExecuteNonQuery();
            return connection;
        }

        private static byte[] BuildEntropy(SecretKey key) =>
            Encoding.UTF8.GetBytes(
                $"GameSaveManager|secret-store|{FormatVersion}|" +
                $"{key.OwnerId:D}|{key.Name}");

        private static void AddKeyParameters(
            SqliteCommand command,
            SecretKey key)
        {
            command.Parameters.AddWithValue("$owner_id", key.OwnerId.ToString("D"));
            command.Parameters.AddWithValue("$secret_name", key.Name);
        }

        private static string ToUtcText(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

#pragma warning restore CA1416
