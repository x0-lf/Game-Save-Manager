using GameSave.Data;
using GameSave.SavePaths;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GameSave.Data
{
    public sealed class SavePathDatabase
    {
        private readonly string _connectionString;

        public SavePathDatabase(string databasePath)
        {
            string? directory = Path.GetDirectoryName(databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            _connectionString = builder.ToString();
        }

        public void Initialize()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = SavePathSchema.CreateSchemaSql;
            command.ExecuteNonQuery();
        }

        public void ImportMappingsFromJson(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);

            var items = JsonSerializer.Deserialize<List<SavePathImportItem>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (items is null || items.Count == 0)
                return;

            ImportMappings(items, enabled: true);
        }

        public void ImportMappings(
            IEnumerable<SavePathImportItem> items,
            bool enabled)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (SavePathImportItem item in items)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = """
                INSERT INTO save_path_mappings (
                    steam_app_id,
                    game_name,
                    platform,
                    path_template,
                    path_kind,
                    source_name,
                    source_url,
                    source_license,
                    notes,
                    priority,
                    enabled,
                    updated_utc
                )
                VALUES (
                    $steam_app_id,
                    $game_name,
                    $platform,
                    $path_template,
                    $path_kind,
                    $source_name,
                    $source_url,
                    $source_license,
                    $notes,
                    $priority,
                    $enabled,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (steam_app_id, platform, path_template)
                DO UPDATE SET
                    game_name = excluded.game_name,
                    path_kind = excluded.path_kind,
                    source_name = excluded.source_name,
                    source_url = excluded.source_url,
                    source_license = excluded.source_license,
                    notes = excluded.notes,
                    priority = excluded.priority,
                    updated_utc = CURRENT_TIMESTAMP;
                """;

                command.Parameters.AddWithValue("$steam_app_id", item.SteamAppId);
                command.Parameters.AddWithValue("$game_name", ToDbValue(item.GameName));
                command.Parameters.AddWithValue("$platform", item.Platform);
                command.Parameters.AddWithValue("$path_template", item.PathTemplate);
                command.Parameters.AddWithValue("$path_kind", item.PathKind);
                command.Parameters.AddWithValue("$source_name", item.SourceName);
                command.Parameters.AddWithValue("$source_url", ToDbValue(item.SourceUrl));
                command.Parameters.AddWithValue("$source_license", ToDbValue(item.SourceLicense));
                command.Parameters.AddWithValue("$notes", ToDbValue(item.Notes));
                command.Parameters.AddWithValue("$priority", item.Priority);
                command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public List<SavePathMapping> GetMappingsForApp(string steamAppId, string platform)
        {
            var mappings = new List<SavePathMapping>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT
                id,
                steam_app_id,
                game_name,
                platform,
                path_template,
                path_kind,
                source_name,
                source_url,
                source_license,
                notes,
                priority,
                enabled
            FROM save_path_mappings
            WHERE steam_app_id = $steam_app_id
              AND platform = $platform
              AND enabled = 1
            ORDER BY priority ASC, id ASC;
            """;

            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$platform", platform);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string pathKindText = reader.GetString(reader.GetOrdinal("path_kind"));

                if (!Enum.TryParse(pathKindText, ignoreCase: true, out SavePathKind pathKind))
                    pathKind = SavePathKind.Directory;

                mappings.Add(new SavePathMapping(
                    reader.GetInt64(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("steam_app_id")),
                    GetNullableString(reader, "game_name"),
                    reader.GetString(reader.GetOrdinal("platform")),
                    reader.GetString(reader.GetOrdinal("path_template")),
                    pathKind,
                    reader.GetString(reader.GetOrdinal("source_name")),
                    GetNullableString(reader, "source_url"),
                    GetNullableString(reader, "source_license"),
                    GetNullableString(reader, "notes"),
                    reader.GetInt32(reader.GetOrdinal("priority")),
                    reader.GetInt32(reader.GetOrdinal("enabled")) == 1));
            }

            return mappings;
        }

        public void SaveVerificationResult(SavePathVerificationResult result)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO verification_results (
                mapping_id,
                steam_app_id,
                expanded_path,
                normalized_path,
                exists_flag,
                is_directory,
                file_count,
                total_bytes,
                confidence,
                last_verified_utc,
                error
            )
            VALUES (
                $mapping_id,
                $steam_app_id,
                $expanded_path,
                $normalized_path,
                $exists_flag,
                $is_directory,
                $file_count,
                $total_bytes,
                $confidence,
                CURRENT_TIMESTAMP,
                $error
            )
            ON CONFLICT (mapping_id, normalized_path)
            DO UPDATE SET
                expanded_path = excluded.expanded_path,
                exists_flag = excluded.exists_flag,
                is_directory = excluded.is_directory,
                file_count = excluded.file_count,
                total_bytes = excluded.total_bytes,
                confidence = excluded.confidence,
                last_verified_utc = CURRENT_TIMESTAMP,
                error = excluded.error;
            """;

            command.Parameters.AddWithValue("$mapping_id", result.Mapping.Id);
            command.Parameters.AddWithValue("$steam_app_id", result.SteamAppId);
            command.Parameters.AddWithValue("$expanded_path", result.ExpandedPath);
            command.Parameters.AddWithValue("$normalized_path", result.NormalizedPath);
            command.Parameters.AddWithValue("$exists_flag", result.Exists ? 1 : 0);
            command.Parameters.AddWithValue("$is_directory", result.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$file_count", result.FileCount);
            command.Parameters.AddWithValue("$total_bytes", result.TotalBytes);
            command.Parameters.AddWithValue("$confidence", result.Confidence);
            command.Parameters.AddWithValue("$error", ToDbValue(result.Error));

            command.ExecuteNonQuery();
        }

        public long CreateBackupRun(string destinationRoot, bool dryRun)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO backup_runs (
                started_utc,
                destination_root,
                dry_run
            )
            VALUES (
                CURRENT_TIMESTAMP,
                $destination_root,
                $dry_run
            );

            SELECT last_insert_rowid();
            """;

            command.Parameters.AddWithValue("$destination_root", destinationRoot);
            command.Parameters.AddWithValue("$dry_run", dryRun ? 1 : 0);

            return (long)(command.ExecuteScalar() ?? 0L);
        }

        public void SaveBackupItem(
            long backupRunId,
            string steamAppId,
            string gameName,
            string sourcePath,
            string destinationPath,
            bool copied,
            long bytes,
            string? sha256,
            string? error)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO backup_items (
                backup_run_id,
                steam_app_id,
                game_name,
                source_path,
                destination_path,
                copied,
                bytes,
                sha256,
                error
            )
            VALUES (
                $backup_run_id,
                $steam_app_id,
                $game_name,
                $source_path,
                $destination_path,
                $copied,
                $bytes,
                $sha256,
                $error
            );
            """;

            command.Parameters.AddWithValue("$backup_run_id", backupRunId);
            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$game_name", gameName);
            command.Parameters.AddWithValue("$source_path", sourcePath);
            command.Parameters.AddWithValue("$destination_path", destinationPath);
            command.Parameters.AddWithValue("$copied", copied ? 1 : 0);
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$sha256", ToDbValue(sha256));
            command.Parameters.AddWithValue("$error", ToDbValue(error));

            command.ExecuteNonQuery();
        }

        public void CompleteBackupRun(long backupRunId, int itemCount, long totalBytes)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE backup_runs
            SET completed_utc = CURRENT_TIMESTAMP,
                item_count = $item_count,
                total_bytes = $total_bytes
            WHERE id = $id;
            """;

            command.Parameters.AddWithValue("$id", backupRunId);
            command.Parameters.AddWithValue("$item_count", itemCount);
            command.Parameters.AddWithValue("$total_bytes", totalBytes);

            command.ExecuteNonQuery();
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value;
        }

        private static string? GetNullableString(SqliteDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            return reader.IsDBNull(ordinal)
                ? null
                : reader.GetString(ordinal);
        }
    }
}