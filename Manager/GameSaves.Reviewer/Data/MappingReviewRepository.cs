using GameSaves.Reviewer.Models;
using Microsoft.Data.Sqlite;

namespace GameSaves.Reviewer.Data
{
    /// <summary>Counts per review status, fetched in a single query.</summary>
    public sealed record ReviewStatusCounts(
        int Pending,
        int Approved,
        int Rejected,
        int NeedsFix);

    /// <summary>
    /// Reads and reviews harvested save-path mappings in the shared SQLite
    /// database. The reviewer only ever touches rows harvested from
    /// PCGamingWiki; manually curated mappings are out of its reach.
    /// </summary>
    public sealed class MappingReviewRepository
    {
        // The only source this tool is allowed to review.
        private const string ReviewedSource = "PCGamingWiki-AutoExtracted";

        private readonly string _connectionString;

        public MappingReviewRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();
        }

        // ---------------------------------------------------------------
        // Schema
        // ---------------------------------------------------------------

        public void InitializeReviewColumns()
        {
            using var connection = OpenConnection();

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

        // ---------------------------------------------------------------
        // Queries
        // ---------------------------------------------------------------

        public List<MappingReviewItem> LoadByStatus(
            string status,
            int limit,
            string? searchText)
        {
            var results = new List<MappingReviewItem>();

            using var connection = OpenConnection();
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
            WHERE source_name = $source
              AND COALESCE(review_status, 'Pending') = $status
              {searchSql}
            ORDER BY
                game_name COLLATE NOCASE,
                platform COLLATE NOCASE,
                path_template COLLATE NOCASE
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$source", ReviewedSource);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$limit", limit <= 0 ? 1000 : limit);

            if (!string.IsNullOrWhiteSpace(searchText))
                command.Parameters.AddWithValue("$search", $"%{searchText.Trim()}%");

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(ReadItem(reader));

            return results;
        }

        public ReviewStatusCounts CountByStatus()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            SELECT COALESCE(review_status, 'Pending') AS status, COUNT(*)
            FROM save_path_mappings
            WHERE source_name = $source
            GROUP BY COALESCE(review_status, 'Pending');
            """;

            command.Parameters.AddWithValue("$source", ReviewedSource);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
                counts[reader.GetString(0)] = reader.GetInt32(1);

            return new ReviewStatusCounts(
                Pending: counts.GetValueOrDefault("Pending"),
                Approved: counts.GetValueOrDefault("Approved"),
                Rejected: counts.GetValueOrDefault("Rejected"),
                NeedsFix: counts.GetValueOrDefault("NeedsFix"));
        }

        // ---------------------------------------------------------------
        // Review decisions
        // ---------------------------------------------------------------

        public void Approve(IReadOnlyCollection<long> ids, int priority, string? reviewNotes)
        {
            ApplyDecision(ids, "Approved", enabled: true, priority, reviewNotes,
                noteMarker: "Reviewed manually.");
        }

        public void Reject(IReadOnlyCollection<long> ids, string? reviewNotes)
        {
            ApplyDecision(ids, "Rejected", enabled: false, priority: null, reviewNotes,
                noteMarker: "Rejected manually.");
        }

        public void MarkNeedsFix(IReadOnlyCollection<long> ids, string? reviewNotes)
        {
            ApplyDecision(ids, "NeedsFix", enabled: false, priority: null, reviewNotes,
                noteMarker: "Needs manual fix.");
        }

        public void ResetToPending(IReadOnlyCollection<long> ids)
        {
            if (ids.Count == 0)
                return;

            using var connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

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
              AND source_name = $source;
            """;

            SqliteParameter idParameter = command.Parameters.Add("$id", SqliteType.Integer);
            command.Parameters.AddWithValue("$source", ReviewedSource);

            foreach (long id in ids)
            {
                idParameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // All three decisions share this shape: set the status, enable or
        // disable the mapping, record when/why, and append a marker to the
        // free-form notes exactly once.
        private void ApplyDecision(
            IReadOnlyCollection<long> ids,
            string reviewStatus,
            bool enabled,
            int? priority,
            string? reviewNotes,
            string noteMarker)
        {
            if (ids.Count == 0)
                return;

            using var connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            string prioritySql = priority is null
                ? string.Empty
                : "priority = $priority,";

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
            UPDATE save_path_mappings
            SET enabled = $enabled,
                {prioritySql}
                review_status = $review_status,
                reviewed_utc = CURRENT_TIMESTAMP,
                review_notes = $review_notes,
                notes = CASE
                    WHEN notes IS NULL OR notes = ''
                        THEN $marker
                    WHEN notes NOT LIKE '%' || $marker || '%'
                        THEN notes || ' ' || $marker
                    ELSE notes
                END,
                updated_utc = CURRENT_TIMESTAMP
            WHERE id = $id
              AND source_name = $source;
            """;

            SqliteParameter idParameter = command.Parameters.Add("$id", SqliteType.Integer);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$review_status", reviewStatus);
            command.Parameters.AddWithValue("$review_notes", ToDbValue(reviewNotes));
            command.Parameters.AddWithValue("$marker", noteMarker);
            command.Parameters.AddWithValue("$source", ReviewedSource);

            if (priority is not null)
                command.Parameters.AddWithValue("$priority", priority.Value);

            foreach (long id in ids)
            {
                idParameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
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

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
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

        private static string? GetNullableString(SqliteDataReader reader, string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value.Trim();
        }
    }
}
