using GameSaves.Core.Save;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Save
{
    public sealed class SqliteSavePathMappingRepository : ISavePathMappingRepository
    {
        private readonly string _connectionString;

        public SqliteSavePathMappingRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            };

            _connectionString = builder.ToString();
        }

        public IReadOnlyList<SavePathMapping> GetApprovedMappingsForApp(
            string steamAppId,
            string platform)
        {
            var mappings = GetMappingsForApp(
                steamAppId,
                platform,
                includeDisabled: false);

            return mappings
                .Where(mapping => mapping.Enabled)
                .OrderBy(mapping => mapping.Priority)
                .ThenBy(mapping => mapping.Id)
                .ToList();
        }

        public IReadOnlyList<SavePathMapping> GetMappingsForApp(
            string steamAppId,
            string platform,
            bool includeDisabled)
        {
            if (string.IsNullOrWhiteSpace(steamAppId))
                return Array.Empty<SavePathMapping>();

            if (string.IsNullOrWhiteSpace(platform))
                return Array.Empty<SavePathMapping>();

            var results = new List<SavePathMapping>();

            using var connection = OpenConnectionAndPrepareReviewColumns();

            using var command = connection.CreateCommand();

            string enabledSql = includeDisabled
                ? string.Empty
                : "AND enabled = 1";

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
                COALESCE(review_status, CASE WHEN enabled = 1 THEN 'Approved' ELSE 'Pending' END) AS review_status,
                review_notes,
                reviewed_utc
            FROM save_path_mappings
            WHERE steam_app_id = $steam_app_id
              AND platform = $platform
              {enabledSql}
            ORDER BY priority ASC, id ASC;
            """;

            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$platform", platform);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(ReadMapping(reader));

            return results;
        }

        public IReadOnlyDictionary<string, SavePathMappingStatus> GetMappingStatusesForApps(
            IEnumerable<string> steamAppIds,
            string platform)
        {
            var requestedAppIds = steamAppIds
                .Where(appId => !string.IsNullOrWhiteSpace(appId))
                .Select(appId => appId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = requestedAppIds.ToDictionary(
                appId => appId,
                appId => new SavePathMappingStatus(
                    appId,
                    TotalMappings: 0,
                    EnabledMappings: 0,
                    ApprovedMappings: 0,
                    PendingMappings: 0,
                    NeedsFixMappings: 0,
                    RejectedMappings: 0),
                StringComparer.OrdinalIgnoreCase);

            if (requestedAppIds.Count == 0)
                return results;

            using var connection = OpenConnectionAndPrepareReviewColumns();

            foreach (string appId in requestedAppIds)
            {
                using var command = connection.CreateCommand();

                command.CommandText = """
                SELECT
                    COUNT(*) AS total_mappings,
                    SUM(CASE WHEN enabled = 1 THEN 1 ELSE 0 END) AS enabled_mappings,
                    SUM(CASE WHEN enabled = 1 THEN 1 ELSE 0 END) AS approved_mappings,
                    SUM(CASE WHEN COALESCE(review_status, 'Pending') = 'Pending' THEN 1 ELSE 0 END) AS pending_mappings,
                    SUM(CASE WHEN COALESCE(review_status, '') = 'NeedsFix' THEN 1 ELSE 0 END) AS needs_fix_mappings,
                    SUM(CASE WHEN COALESCE(review_status, '') = 'Rejected' THEN 1 ELSE 0 END) AS rejected_mappings
                FROM save_path_mappings
                WHERE steam_app_id = $steam_app_id
                  AND platform = $platform;
                """;

                command.Parameters.AddWithValue("$steam_app_id", appId);
                command.Parameters.AddWithValue("$platform", platform);

                using var reader = command.ExecuteReader();

                if (!reader.Read())
                    continue;

                results[appId] = new SavePathMappingStatus(
                    appId,
                    TotalMappings: GetInt(reader, "total_mappings"),
                    EnabledMappings: GetInt(reader, "enabled_mappings"),
                    ApprovedMappings: GetInt(reader, "approved_mappings"),
                    PendingMappings: GetInt(reader, "pending_mappings"),
                    NeedsFixMappings: GetInt(reader, "needs_fix_mappings"),
                    RejectedMappings: GetInt(reader, "rejected_mappings"));
            }

            return results;
        }

        public int CountApprovedMappings(string platform)
        {
            return CountMappingsBySql(
                platform,
                "enabled = 1");
        }

        public int CountNeedsFixMappings(string platform)
        {
            return CountMappingsBySql(
                platform,
                "COALESCE(review_status, '') = 'NeedsFix'");
        }

        public int CountPendingMappings(string platform)
        {
            return CountMappingsBySql(
                platform,
                "COALESCE(review_status, 'Pending') = 'Pending'");
        }

        private int CountMappingsBySql(
            string platform,
            string conditionSql)
        {
            using var connection = OpenConnectionAndPrepareReviewColumns();

            using var command = connection.CreateCommand();
            command.CommandText = $"""
            SELECT COUNT(*)
            FROM save_path_mappings
            WHERE platform = $platform
              AND {conditionSql};
            """;

            command.Parameters.AddWithValue("$platform", platform);

            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        private SqliteConnection OpenConnectionAndPrepareReviewColumns()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            EnsureReviewColumns(connection);

            return connection;
        }

        private static void EnsureReviewColumns(SqliteConnection connection)
        {
            EnsureColumn(connection, "save_path_mappings", "review_status", "TEXT NULL");
            EnsureColumn(connection, "save_path_mappings", "reviewed_utc", "TEXT NULL");
            EnsureColumn(connection, "save_path_mappings", "review_notes", "TEXT NULL");

            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_save_path_mappings_review_status
                ON save_path_mappings (source_name, review_status, enabled);
            """;
            command.ExecuteNonQuery();
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
                string existingColumnName = reader.GetString(1);

                if (existingColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static SavePathMapping ReadMapping(SqliteDataReader reader)
        {
            string pathKindText = reader.GetString(reader.GetOrdinal("path_kind"));

            if (!Enum.TryParse(pathKindText, ignoreCase: true, out SavePathKind pathKind))
                pathKind = SavePathKind.Directory;

            return new SavePathMapping(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                SteamAppId: reader.GetString(reader.GetOrdinal("steam_app_id")),
                GameName: GetNullableString(reader, "game_name"),
                Platform: reader.GetString(reader.GetOrdinal("platform")),
                PathTemplate: reader.GetString(reader.GetOrdinal("path_template")),
                PathKind: pathKind,
                SourceName: reader.GetString(reader.GetOrdinal("source_name")),
                SourceUrl: GetNullableString(reader, "source_url"),
                SourceLicense: GetNullableString(reader, "source_license"),
                Notes: GetNullableString(reader, "notes"),
                Priority: reader.GetInt32(reader.GetOrdinal("priority")),
                Enabled: reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
                ReviewStatus: GetNullableString(reader, "review_status") ?? "Pending",
                ReviewNotes: GetNullableString(reader, "review_notes"),
                ReviewedUtc: GetNullableDateTimeOffset(reader, "reviewed_utc"));
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

        private static DateTimeOffset? GetNullableDateTimeOffset(
            SqliteDataReader reader,
            string columnName)
        {
            string? value = GetNullableString(reader, columnName);

            if (string.IsNullOrWhiteSpace(value))
                return null;

            return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
                ? parsed
                : null;
        }

        private static int GetInt(
            SqliteDataReader reader,
            string columnName)
        {
            int ordinal = reader.GetOrdinal(columnName);

            if (reader.IsDBNull(ordinal))
                return 0;

            return Convert.ToInt32(reader.GetValue(ordinal));
        }
    }
}