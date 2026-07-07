using Microsoft.Data.Sqlite;

namespace GameSaves.Reviewer
{
    public sealed class MappingReviewRepository
    {
        private readonly string _connectionString;

        public MappingReviewRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            _connectionString = builder.ToString();
        }

        public void InitializeReviewColumns()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            EnsureColumn(connection, "save_path_mappings", "review_status", "TEXT NOT NULL DEFAULT 'Pending'");
            EnsureColumn(connection, "save_path_mappings", "reviewed_utc", "TEXT NULL");
            EnsureColumn(connection, "save_path_mappings", "review_notes", "TEXT NULL");

            using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_save_path_mappings_review_status
                ON save_path_mappings (source_name, review_status, enabled);
            """;
            indexCommand.ExecuteNonQuery();
        }

        public List<MappingReviewItem> LoadPending(
            int limit,
            string? searchText)
        {
            var results = new List<MappingReviewItem>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            string searchSql = string.IsNullOrWhiteSpace(searchText)
                ? string.Empty
                : """
                  AND (
                      steam_app_id LIKE $search
                      OR game_name LIKE $search
                      OR platform LIKE $search
                      OR path_template LIKE $search
                      OR source_url LIKE $search
                  )
                  """;

            command.CommandText = $"""
            SELECT
                id,
                steam_app_id,
                COALESCE(game_name, '') AS game_name,
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
                review_notes
            FROM save_path_mappings
            WHERE source_name = 'PCGamingWiki-AutoExtracted'
              AND COALESCE(review_status, 'Pending') = 'Pending'
              AND enabled = 0
              {searchSql}
            ORDER BY
                game_name COLLATE NOCASE,
                platform COLLATE NOCASE,
                path_template COLLATE NOCASE
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$limit", limit <= 0 ? 1000 : limit);

            if (!string.IsNullOrWhiteSpace(searchText))
                command.Parameters.AddWithValue("$search", $"%{searchText.Trim()}%");

            using var reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(ReadItem(reader));

            return results;
        }

        public List<MappingReviewItem> LoadByStatus(
            string status,
            int limit,
            string? searchText)
        {
            var results = new List<MappingReviewItem>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            string searchSql = string.IsNullOrWhiteSpace(searchText)
                ? string.Empty
                : """
                  AND (
                      steam_app_id LIKE $search
                      OR game_name LIKE $search
                      OR platform LIKE $search
                      OR path_template LIKE $search
                      OR source_url LIKE $search
                  )
                  """;

            command.CommandText = $"""
            SELECT
                id,
                steam_app_id,
                COALESCE(game_name, '') AS game_name,
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
                review_notes
            FROM save_path_mappings
            WHERE source_name = 'PCGamingWiki-AutoExtracted'
              AND COALESCE(review_status, 'Pending') = $status
              {searchSql}
            ORDER BY
                game_name COLLATE NOCASE,
                platform COLLATE NOCASE,
                path_template COLLATE NOCASE
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$limit", limit <= 0 ? 1000 : limit);

            if (!string.IsNullOrWhiteSpace(searchText))
                command.Parameters.AddWithValue("$search", $"%{searchText.Trim()}%");

            using var reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(ReadItem(reader));

            return results;
        }

        public int CountByStatus(string status)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT COUNT(*)
            FROM save_path_mappings
            WHERE source_name = 'PCGamingWiki-AutoExtracted'
              AND COALESCE(review_status, 'Pending') = $status;
            """;

            command.Parameters.AddWithValue("$status", status);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void ApproveMappings(
            IReadOnlyCollection<long> ids,
            int priority,
            string? reviewNotes)
        {
            if (ids.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (long id in ids)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                UPDATE save_path_mappings
                SET enabled = 1,
                    priority = $priority,
                    review_status = 'Approved',
                    reviewed_utc = CURRENT_TIMESTAMP,
                    review_notes = $review_notes,
                    notes = CASE
                        WHEN notes IS NULL OR notes = ''
                            THEN 'Reviewed manually.'
                        WHEN notes NOT LIKE '%Reviewed manually.%'
                            THEN notes || ' Reviewed manually.'
                        ELSE notes
                    END,
                    updated_utc = CURRENT_TIMESTAMP
                WHERE id = $id
                  AND source_name = 'PCGamingWiki-AutoExtracted';
                """;

                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$priority", priority);
                command.Parameters.AddWithValue("$review_notes", ToDbValue(reviewNotes));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void RejectMappings(
            IReadOnlyCollection<long> ids,
            string? reviewNotes)
        {
            if (ids.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (long id in ids)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                UPDATE save_path_mappings
                SET enabled = 0,
                    review_status = 'Rejected',
                    reviewed_utc = CURRENT_TIMESTAMP,
                    review_notes = $review_notes,
                    notes = CASE
                        WHEN notes IS NULL OR notes = ''
                            THEN 'Rejected manually.'
                        WHEN notes NOT LIKE '%Rejected manually.%'
                            THEN notes || ' Rejected manually.'
                        ELSE notes
                    END,
                    updated_utc = CURRENT_TIMESTAMP
                WHERE id = $id
                  AND source_name = 'PCGamingWiki-AutoExtracted';
                """;

                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$review_notes", ToDbValue(reviewNotes));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void MarkNeedsFix(
            IReadOnlyCollection<long> ids,
            string? reviewNotes)
        {
            if (ids.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (long id in ids)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                UPDATE save_path_mappings
                SET enabled = 0,
                    review_status = 'NeedsFix',
                    reviewed_utc = CURRENT_TIMESTAMP,
                    review_notes = $review_notes,
                    notes = CASE
                        WHEN notes IS NULL OR notes = ''
                            THEN 'Needs manual fix.'
                        WHEN notes NOT LIKE '%Needs manual fix.%'
                            THEN notes || ' Needs manual fix.'
                        ELSE notes
                    END,
                    updated_utc = CURRENT_TIMESTAMP
                WHERE id = $id
                  AND source_name = 'PCGamingWiki-AutoExtracted';
                """;

                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$review_notes", ToDbValue(reviewNotes));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void ResetToPending(IReadOnlyCollection<long> ids)
        {
            if (ids.Count == 0)
                return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (long id in ids)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                UPDATE save_path_mappings
                SET enabled = 0,
                    review_status = 'Pending',
                    reviewed_utc = NULL,
                    review_notes = NULL,
                    updated_utc = CURRENT_TIMESTAMP
                WHERE id = $id
                  AND source_name = 'PCGamingWiki-AutoExtracted';
                """;

                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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
                string existingColumn = reader.GetString(1);

                if (existingColumn.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static MappingReviewItem ReadItem(SqliteDataReader reader)
        {
            return new MappingReviewItem
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SteamAppId = reader.GetString(reader.GetOrdinal("steam_app_id")),
                GameName = reader.GetString(reader.GetOrdinal("game_name")),
                Platform = reader.GetString(reader.GetOrdinal("platform")),
                PathTemplate = reader.GetString(reader.GetOrdinal("path_template")),
                PathKind = reader.GetString(reader.GetOrdinal("path_kind")),
                SourceName = reader.GetString(reader.GetOrdinal("source_name")),
                SourceUrl = GetNullableString(reader, "source_url"),
                SourceLicense = GetNullableString(reader, "source_license"),
                Notes = GetNullableString(reader, "notes"),
                Priority = reader.GetInt32(reader.GetOrdinal("priority")),
                Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
                ReviewStatus = reader.GetString(reader.GetOrdinal("review_status")),
                ReviewNotes = GetNullableString(reader, "review_notes")
            };
        }

        private static string? GetNullableString(
            SqliteDataReader reader,
            string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            return reader.IsDBNull(ordinal)
                ? null
                : reader.GetString(ordinal);
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value.Trim();
        }
    }
}