using GameSaves.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class SqliteManualBackupPresetRepository : IManualBackupPresetRepository
    {
        private readonly string _databasePath;
        private readonly string _connectionString;

        public SqliteManualBackupPresetRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _databasePath = databasePath;

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();
        }

        public IReadOnlyList<ManualBackupPreset> GetAll()
        {
            var presets = new List<ManualBackupPreset>();

            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            SELECT id, name, destination_root, include_userdata, include_mappings,
                   created_utc, last_used_utc
            FROM manual_backup_presets
            ORDER BY name COLLATE NOCASE ASC;
            """;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                presets.Add(new ManualBackupPreset(
                    Id: reader.GetInt64(0),
                    Name: reader.GetString(1),
                    DestinationRoot: reader.GetString(2),
                    IncludeSteamUserDataGameFolder: reader.GetInt64(3) != 0,
                    IncludeApprovedMappings: reader.GetInt64(4) != 0,
                    CreatedUtc: DateTimeOffset.Parse(reader.GetString(5)),
                    LastUsedUtc: reader.IsDBNull(6)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(6))));
            }

            return presets;
        }

        public ManualBackupPreset Save(ManualBackupPreset preset)
        {
            string name = preset.Name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A preset name is required.", nameof(preset));

            if (string.IsNullOrWhiteSpace(preset.DestinationRoot))
                throw new ArgumentException("A preset destination is required.", nameof(preset));

            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            INSERT INTO manual_backup_presets (
                name, destination_root, include_userdata, include_mappings, created_utc)
            VALUES ($name, $destination_root, $include_userdata, $include_mappings, $created_utc)
            ON CONFLICT(name) DO UPDATE SET
                destination_root = $destination_root,
                include_userdata = $include_userdata,
                include_mappings = $include_mappings;
            SELECT id, name, destination_root, include_userdata, include_mappings,
                   created_utc, last_used_utc
            FROM manual_backup_presets
            WHERE name = $name COLLATE NOCASE;
            """;

            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$destination_root", preset.DestinationRoot);
            command.Parameters.AddWithValue("$include_userdata", preset.IncludeSteamUserDataGameFolder ? 1 : 0);
            command.Parameters.AddWithValue("$include_mappings", preset.IncludeApprovedMappings ? 1 : 0);
            command.Parameters.AddWithValue("$created_utc", DateTimeOffset.UtcNow.ToString("O"));

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
                throw new InvalidOperationException("The preset could not be saved.");

            return new ManualBackupPreset(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                DestinationRoot: reader.GetString(2),
                IncludeSteamUserDataGameFolder: reader.GetInt64(3) != 0,
                IncludeApprovedMappings: reader.GetInt64(4) != 0,
                CreatedUtc: DateTimeOffset.Parse(reader.GetString(5)),
                LastUsedUtc: reader.IsDBNull(6)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(6)));
        }

        public void Delete(long id)
        {
            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "DELETE FROM manual_backup_presets WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        public void MarkUsed(long id)
        {
            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            UPDATE manual_backup_presets
            SET last_used_utc = $last_used_utc
            WHERE id = $id;
            """;

            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$last_used_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        private SqliteConnection OpenConnection()
        {
            string? directory = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS manual_backup_presets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                destination_root TEXT NOT NULL,
                include_userdata INTEGER NOT NULL,
                include_mappings INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                last_used_utc TEXT NULL
            );
            """;
            command.ExecuteNonQuery();

            return connection;
        }
    }
}
