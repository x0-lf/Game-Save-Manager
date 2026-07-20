using GameSaves.Core.Sync;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace GameSaves.Infrastructure.Sync
{
    public sealed class SqliteSyncRemoteProfileRepository : ISyncRemoteProfileRepository
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly SyncRemoteProfileSettingsSerializer _settingsSerializer;

        public SqliteSyncRemoteProfileRepository(
            string databasePath,
            SyncRemoteProfileSettingsSerializer settingsSerializer)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _databasePath = databasePath;
            _settingsSerializer = settingsSerializer;
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true
            }.ToString();
        }

        public IReadOnlyList<SyncRemoteProfile> GetAll()
        {
            var profiles = new List<SyncRemoteProfile>();

            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = SelectColumns + "\n" + """
                ORDER BY
                    CASE WHEN last_used_utc IS NULL THEN 1 ELSE 0 END,
                    last_used_utc DESC,
                    display_name COLLATE NOCASE ASC;
                """;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
                profiles.Add(ReadProfile(reader));

            return profiles;
        }

        public SyncRemoteProfile? GetById(Guid id)
        {
            using SqliteConnection connection = OpenConnection();
            return GetById(connection, null, id);
        }

        public SyncRemoteProfile Create(SyncRemoteProfile profile)
        {
            if (profile.Id == Guid.Empty)
                throw new ArgumentException("A stable remote profile ID is required.", nameof(profile));

            string displayName = SyncRemoteProfileValidation.NormalizeDisplayName(profile.DisplayName);
            string settingsJson = SerializeSettings(profile);

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureNameAvailable(connection, transaction, displayName, excludingId: null);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sync_remote_profiles (
                    id, display_name, provider_kind, account_display_name,
                    remote_root_display_name, provider_settings_json,
                    provider_settings_version, remote_folder_id, created_utc,
                    updated_utc, last_used_utc,
                    last_successful_connection_utc)
                VALUES (
                    $id, $display_name, $provider_kind, $account_display_name,
                    $remote_root_display_name, $provider_settings_json,
                    $provider_settings_version, $remote_folder_id, $created_utc,
                    $updated_utc, $last_used_utc,
                    $last_successful_connection_utc);
                """;
            AddProfileParameters(command, profile with { DisplayName = displayName }, settingsJson);
            command.ExecuteNonQuery();
            transaction.Commit();

            return GetById(profile.Id) ?? throw new SyncRemoteProfileNotFoundException(profile.Id);
        }

        public SyncRemoteProfile Update(SyncRemoteProfile profile)
        {
            string displayName = SyncRemoteProfileValidation.NormalizeDisplayName(profile.DisplayName);
            string settingsJson = SerializeSettings(profile);

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            RequireProfile(connection, transaction, profile.Id);
            EnsureNameAvailable(connection, transaction, displayName, profile.Id);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_remote_profiles
                SET display_name = $display_name,
                    provider_kind = $provider_kind,
                    account_display_name = $account_display_name,
                    remote_root_display_name = $remote_root_display_name,
                    provider_settings_json = $provider_settings_json,
                    provider_settings_version = $provider_settings_version,
                    remote_folder_id = $remote_folder_id,
                    updated_utc = $updated_utc
                WHERE id = $id;
                """;
            AddProfileParameters(command, profile with { DisplayName = displayName }, settingsJson);
            command.ExecuteNonQuery();
            transaction.Commit();

            return GetById(profile.Id) ?? throw new SyncRemoteProfileNotFoundException(profile.Id);
        }

        public SyncRemoteProfile Rename(Guid id, string displayName, DateTimeOffset updatedUtc)
        {
            string normalized = SyncRemoteProfileValidation.NormalizeDisplayName(displayName);

            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            RequireProfile(connection, transaction, id);
            EnsureNameAvailable(connection, transaction, normalized, id);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sync_remote_profiles
                SET display_name = $display_name,
                    updated_utc = $updated_utc
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$display_name", normalized);
            command.Parameters.AddWithValue("$updated_utc", ToUtcText(updatedUtc));
            command.ExecuteNonQuery();
            transaction.Commit();

            return GetById(id) ?? throw new SyncRemoteProfileNotFoundException(id);
        }

        public void Delete(Guid id)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sync_remote_profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));

            if (command.ExecuteNonQuery() == 0)
                throw new SyncRemoteProfileNotFoundException(id);
        }

        public SyncRemoteProfile UpdateLastUsed(Guid id, DateTimeOffset lastUsedUtc) =>
            UpdateTimestamp(id, "last_used_utc", lastUsedUtc);

        public SyncRemoteProfile UpdateLastSuccessfulConnection(
            Guid id,
            DateTimeOffset lastSuccessfulConnectionUtc) =>
            UpdateTimestamp(id, "last_successful_connection_utc", lastSuccessfulConnectionUtc);

        private SyncRemoteProfile UpdateTimestamp(
            Guid id,
            string columnName,
            DateTimeOffset timestampUtc)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE sync_remote_profiles SET {columnName} = $timestamp WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$timestamp", ToUtcText(timestampUtc));

            if (command.ExecuteNonQuery() == 0)
                throw new SyncRemoteProfileNotFoundException(id);

            return GetById(id) ?? throw new SyncRemoteProfileNotFoundException(id);
        }

        private string SerializeSettings(SyncRemoteProfile profile)
        {
            if (profile.ProviderSettings is null)
                throw new ArgumentException("Usable provider settings are required.", nameof(profile));

            return _settingsSerializer.Serialize(profile.ProviderKind, profile.ProviderSettings);
        }

        private static void AddProfileParameters(
            SqliteCommand command,
            SyncRemoteProfile profile,
            string settingsJson)
        {
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$display_name", profile.DisplayName);
            command.Parameters.AddWithValue("$provider_kind", (int)profile.ProviderKind);
            command.Parameters.AddWithValue("$account_display_name", (object?)profile.AccountDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$remote_root_display_name", (object?)profile.RemoteRootDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$provider_settings_json", settingsJson);
            command.Parameters.AddWithValue("$provider_settings_version", profile.ProviderSettings!.SchemaVersion);
            command.Parameters.AddWithValue("$remote_folder_id", (object?)profile.RemoteFolderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_utc", ToUtcText(profile.CreatedUtc));
            command.Parameters.AddWithValue("$updated_utc", ToUtcText(profile.UpdatedUtc));
            command.Parameters.AddWithValue("$last_used_utc", profile.LastUsedUtc is null ? DBNull.Value : ToUtcText(profile.LastUsedUtc.Value));
            command.Parameters.AddWithValue(
                "$last_successful_connection_utc",
                profile.LastSuccessfulConnectionUtc is null
                    ? DBNull.Value
                    : ToUtcText(profile.LastSuccessfulConnectionUtc.Value));
        }

        private SyncRemoteProfile ReadProfile(SqliteDataReader reader)
        {
            SyncProviderKind providerKind = (SyncProviderKind)reader.GetInt32(2);
            int settingsVersion = reader.GetInt32(6);
            string settingsJson = reader.GetString(5);
            SyncRemoteProfileSettingsReadResult settings =
                _settingsSerializer.Deserialize(providerKind, settingsVersion, settingsJson);

            return new SyncRemoteProfile(
                Id: Guid.Parse(reader.GetString(0)),
                DisplayName: reader.GetString(1),
                ProviderKind: providerKind,
                AccountDisplayName: reader.IsDBNull(3) ? null : reader.GetString(3),
                RemoteRootDisplayName: reader.IsDBNull(4) ? null : reader.GetString(4),
                ProviderSettings: settings.Settings,
                CreatedUtc: ParseUtc(reader.GetString(8)),
                UpdatedUtc: ParseUtc(reader.GetString(9)),
                LastUsedUtc: reader.IsDBNull(10) ? null : ParseUtc(reader.GetString(10)),
                LastSuccessfulConnectionUtc: reader.IsDBNull(11) ? null : ParseUtc(reader.GetString(11)),
                RemoteFolderId: reader.IsDBNull(7) ? null : reader.GetString(7),
                SettingsError: settings.Error);
        }

        private SyncRemoteProfile? GetById(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            Guid id)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SelectColumns + " WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));

            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadProfile(reader) : null;
        }

        private static void RequireProfile(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid id)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM sync_remote_profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));

            if (command.ExecuteScalar() is null)
                throw new SyncRemoteProfileNotFoundException(id);
        }

        private static void EnsureNameAvailable(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string displayName,
            Guid? excludingId)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = excludingId is null
                ? "SELECT 1 FROM sync_remote_profiles WHERE display_name = $display_name COLLATE NOCASE;"
                : "SELECT 1 FROM sync_remote_profiles WHERE display_name = $display_name COLLATE NOCASE AND id <> $id;";
            command.Parameters.AddWithValue("$display_name", displayName);

            if (excludingId is not null)
                command.Parameters.AddWithValue("$id", excludingId.Value.ToString("D"));

            if (command.ExecuteScalar() is not null)
                throw new SyncRemoteProfileDuplicateNameException(displayName);
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
                CREATE TABLE IF NOT EXISTS sync_remote_profiles (
                    id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    provider_kind INTEGER NOT NULL,
                    account_display_name TEXT NULL,
                    remote_root_display_name TEXT NULL,
                    provider_settings_json TEXT NOT NULL,
                    provider_settings_version INTEGER NOT NULL,
                    remote_folder_id TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    last_used_utc TEXT NULL,
                    last_successful_connection_utc TEXT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_remote_profiles_display_name
                    ON sync_remote_profiles(display_name COLLATE NOCASE);
                """;
            command.ExecuteNonQuery();

            return connection;
        }

        private static string ToUtcText(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static DateTimeOffset ParseUtc(string value) =>
            DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToUniversalTime();

        private const string SelectColumns = """
            SELECT id, display_name, provider_kind, account_display_name,
                   remote_root_display_name, provider_settings_json,
                   provider_settings_version, remote_folder_id, created_utc,
                   updated_utc, last_used_utc,
                   last_successful_connection_utc
            FROM sync_remote_profiles
            """;
    }
}
