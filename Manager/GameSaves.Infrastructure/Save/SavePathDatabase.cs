using GameSaves.Core.Catalog;
using GameSaves.Core.Data;
using GameSaves.Core.Save;
using GameSaves.Infrastructure.Catalog;
using GameSaves.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GameSaves.Infrastructure.Save
{
    public sealed class SavePathDatabase
    {
        private readonly string _connectionString;
        private readonly string _databasePath;

        public SavePathDatabase(string databasePath)
        {
            _databasePath = databasePath;
            string? directory = Path.GetDirectoryName(databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            _connectionString = builder.ToString();
        }

        public string DatabasePath => _databasePath;

        public CuratedSeedResult SeedCuratedMappings(ICuratedMappingSeeder? seeder = null)
        {
            seeder ??= new CuratedMappingSeeder();
            return seeder.Seed(_databasePath);
        }

        public MigrationExecutionResult MigrateSchema(ISchemaMigrator? migrator = null)
        {
            migrator ??= new SchemaMigrator();
            return migrator.Migrate(_databasePath);
        }

        public void Initialize()
        {
            MigrateSchema();
        }

        public MappingImportReport ImportWithReport(string jsonPath, MappingImportOptions? options = null)
        {
            var service = new MappingImportService();
            return service.ImportFile(_databasePath, jsonPath, options);
        }

        public MappingImportReport ImportJsonWithReport(string jsonContent, MappingImportOptions? options = null)
        {
            var service = new MappingImportService();
            return service.ImportJson(_databasePath, jsonContent, options);
        }

        public MissingTitlesTracklist GenerateTracklist(
            IEnumerable<MissingTitleCandidate>? candidates = null,
            TracklistOptions? options = null,
            ITracklistGeneratorService? generator = null)
        {
            generator ??= new TracklistGeneratorService();
            return generator.GenerateTracklist(_databasePath, candidates, options);
        }

        public MissingTitlesTracklist GenerateTracklistFromInstalled(
            TracklistOptions? options = null,
            ITracklistGeneratorService? generator = null)
        {
            generator ??= new TracklistGeneratorService();
            return generator.GenerateTracklistFromInstalled(_databasePath, options);
        }

        public void ImportMappingsFromJson(string jsonPath)
        {
            ImportMappingsFromJson(jsonPath, enabled: false, reviewStatus: "Pending");
        }

        public void ImportMappingsFromJson(
            string jsonPath,
            bool enabled,
            string reviewStatus = "Pending")
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

            ImportMappings(items, enabled: enabled, reviewStatus: reviewStatus);
        }

        public void ImportMappings(
            IEnumerable<SavePathImportItem> items,
            bool enabled = false,
            string reviewStatus = "Pending")
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureReviewColumns(connection);

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
                    review_status,
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
                    $review_status,
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
                    -- An import that explicitly approves must reach rows that already
                    -- exist, or the caller is told the mapping was approved when it
                    -- was not. Otherwise the previous review stands, except when the
                    -- import changes how the path is used: that invalidates the
                    -- review it was granted under, so it returns to Pending.
                    review_status = CASE
                        WHEN $force_review = 1 THEN excluded.review_status
                        WHEN save_path_mappings.path_kind <> excluded.path_kind THEN 'Pending'
                        ELSE save_path_mappings.review_status
                    END,
                    enabled = CASE
                        WHEN $force_review = 1 THEN excluded.enabled
                        WHEN save_path_mappings.path_kind <> excluded.path_kind THEN 0
                        ELSE save_path_mappings.enabled
                    END,
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
                string effectiveReviewStatus = string.IsNullOrWhiteSpace(reviewStatus) ? "Pending" : reviewStatus;
                command.Parameters.AddWithValue("$review_status", effectiveReviewStatus);
                command.Parameters.AddWithValue(
                    "$force_review",
                    effectiveReviewStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public List<SavePathMapping> GetApprovedMappingsForApp(string steamAppId, string platform)
        {
            return GetMappingsForApp(steamAppId, platform, includeDisabled: false, onlyApproved: true);
        }

        public List<SavePathMapping> GetMappingsForApp(
            string steamAppId,
            string platform,
            bool includeDisabled = false,
            bool onlyApproved = false)
        {
            var mappings = new List<SavePathMapping>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureReviewColumns(connection);

            using var command = connection.CreateCommand();

            string enabledFilter = includeDisabled ? string.Empty : "AND enabled = 1";
            string approvedFilter = onlyApproved ? "AND COALESCE(review_status, '') = 'Approved'" : string.Empty;

            command.CommandText = $"""
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
                enabled,
                COALESCE(review_status, 'Pending') AS review_status,
                review_notes,
                reviewed_utc
            FROM save_path_mappings
            WHERE steam_app_id = $steam_app_id
              AND platform = $platform
              {enabledFilter}
              {approvedFilter}
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
                    reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
                    reader.GetString(reader.GetOrdinal("review_status")),
                    GetNullableString(reader, "review_notes"),
                    GetNullableDateTimeOffset(reader, "reviewed_utc")));
            }

            if (onlyApproved)
            {
                return mappings
                    .Where(m => m.Enabled && string.Equals(m.ReviewStatus, "Approved", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return mappings;
        }

        public void ApproveMapping(long id, string? notes = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureReviewColumns(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE save_path_mappings
            SET enabled = 1,
                review_status = 'Approved',
                reviewed_utc = CURRENT_TIMESTAMP,
                review_notes = COALESCE($notes, review_notes),
                updated_utc = CURRENT_TIMESTAMP
            WHERE id = $id;
            """;

            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$notes", ToDbValue(notes));
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Approves the mappings of one game. A mapping a reviewer explicitly rejected
        /// is left alone unless <paramref name="includeRejected"/> says otherwise:
        /// bulk approval must not quietly undo an individual decision. Returns the
        /// number of mappings actually approved, so a caller can report the truth.
        /// </summary>
        public int ApproveMappingsForApp(
            string steamAppId,
            string? notes = null,
            string? platform = null,
            bool includeRejected = false)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureReviewColumns(connection);

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE save_path_mappings
            SET enabled = 1,
                review_status = 'Approved',
                reviewed_utc = CURRENT_TIMESTAMP,
                review_notes = COALESCE($notes, review_notes),
                updated_utc = CURRENT_TIMESTAMP
            WHERE steam_app_id = $steam_app_id
              AND ($platform IS NULL OR platform = $platform)
              AND ($include_rejected = 1 OR COALESCE(review_status, '') <> 'Rejected');
            """;

            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$notes", ToDbValue(notes));
            command.Parameters.AddWithValue("$platform", ToDbValue(platform));
            command.Parameters.AddWithValue("$include_rejected", includeRejected ? 1 : 0);

            return command.ExecuteNonQuery();
        }

        public int MigrateLegacyMappings(bool trustLegacyEnabledAsApproved = false)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureReviewColumns(connection);

            using var command = connection.CreateCommand();
            if (trustLegacyEnabledAsApproved)
            {
                command.CommandText = """
                UPDATE save_path_mappings
                SET review_status = 'Approved',
                    reviewed_utc = CURRENT_TIMESTAMP,
                    review_notes = 'Migrated from legacy enabled mapping.'
                WHERE enabled = 1
                  AND (review_status IS NULL OR review_status = 'Pending');
                """;
            }
            else
            {
                command.CommandText = """
                UPDATE save_path_mappings
                SET enabled = 0,
                    review_status = 'Pending'
                WHERE review_status IS NULL
                   OR (review_status = 'Pending' AND enabled = 1);
                """;
            }

            return command.ExecuteNonQuery();
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

        private static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
                return null;

            string text = reader.GetString(ordinal);
            return DateTimeOffset.TryParse(text, out DateTimeOffset dto) ? dto : null;
        }

        // The review columns are added once per database file. This used to run on
        // every call that opened a connection - three PRAGMA queries, a DDL statement
        // and a full-table UPDATE that takes a write lock - which meant a read of one
        // game's mappings wrote to the database, once per installed game.
        private static readonly ConcurrentDictionary<string, bool> MigratedDatabases =
            new(StringComparer.OrdinalIgnoreCase);

        public static void EnsureReviewColumns(SqliteConnection connection)
        {
            string databaseKey = connection.DataSource ?? string.Empty;

            if (MigratedDatabases.ContainsKey(databaseKey))
                return;

            EnsureReviewColumnsCore(connection);

            MigratedDatabases[databaseKey] = true;
        }

        private static void EnsureReviewColumnsCore(SqliteConnection connection)
        {
            EnsureColumn(connection, "save_path_mappings", "review_status", "TEXT NOT NULL DEFAULT 'Pending'");
            EnsureColumn(connection, "save_path_mappings", "reviewed_utc", "TEXT NULL");
            EnsureColumn(connection, "save_path_mappings", "review_notes", "TEXT NULL");

            using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_save_path_mappings_review_status
                ON save_path_mappings (source_name, review_status, enabled);
            """;
            indexCommand.ExecuteNonQuery();

            using var migrateCommand = connection.CreateCommand();
            migrateCommand.CommandText = """
            UPDATE save_path_mappings
            SET review_status = 'Pending'
            WHERE review_status IS NULL;
            """;
            migrateCommand.ExecuteNonQuery();
        }

        private static void EnsureColumn(
            SqliteConnection connection,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            if (ColumnExists(connection, tableName, columnName))
                return;

            using var command = connection.CreateCommand();
            command.CommandText = $"""
            ALTER TABLE {tableName}
            ADD COLUMN {columnName} {columnDefinition};
            """;
            command.ExecuteNonQuery();
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string name = reader.GetString(reader.GetOrdinal("name"));

                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}