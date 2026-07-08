using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GameSaves.External
{
    public sealed class ExternalHarvestDatabase
    {
        private readonly string _connectionString;

        public ExternalHarvestDatabase(string databasePath)
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
            command.CommandText = ExternalHarvestSchema.CreateSchemaSql;
            command.ExecuteNonQuery();
        }

        public long StartHarvestRun(string outputRoot, int requestsPerMinute)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO external_harvest_runs (
                source_name,
                output_root,
                requested_per_minute
            )
            VALUES (
                'PCGamingWiki',
                $output_root,
                $requests_per_minute
            );

            SELECT last_insert_rowid();
            """;

            command.Parameters.AddWithValue("$output_root", outputRoot);
            command.Parameters.AddWithValue("$requests_per_minute", requestsPerMinute);

            return (long)(command.ExecuteScalar() ?? 0L);
        }

        public void CompleteHarvestRun(
            long harvestRunId,
            int titlesIndexed,
            int titlesProcessed,
            int titlesFailed,
            int mappingsExtracted,
            string stoppedReason)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE external_harvest_runs
            SET completed_utc = CURRENT_TIMESTAMP,
                titles_indexed = $titles_indexed,
                titles_processed = $titles_processed,
                titles_failed = $titles_failed,
                mappings_extracted = $mappings_extracted,
                stopped_reason = $stopped_reason
            WHERE id = $id;
            """;

            command.Parameters.AddWithValue("$id", harvestRunId);
            command.Parameters.AddWithValue("$titles_indexed", titlesIndexed);
            command.Parameters.AddWithValue("$titles_processed", titlesProcessed);
            command.Parameters.AddWithValue("$titles_failed", titlesFailed);
            command.Parameters.AddWithValue("$mappings_extracted", mappingsExtracted);
            command.Parameters.AddWithValue("$stopped_reason", stoppedReason);

            command.ExecuteNonQuery();
        }

        public void UpsertPcgwTitle(PcgwTitle title)
        {
            string steamAppIdsJson = JsonSerializer.Serialize(title.SteamAppIds);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO external_pcgamingwiki_pages (
                page_id,
                page_name,
                display_title,
                steam_app_ids,
                source_url,
                harvest_status
            )
            VALUES (
                $page_id,
                $page_name,
                $display_title,
                $steam_app_ids,
                $source_url,
                'Pending'
            )
            ON CONFLICT (page_id)
            DO UPDATE SET
                page_name = excluded.page_name,
                display_title = excluded.display_title,
                steam_app_ids = excluded.steam_app_ids,
                source_url = excluded.source_url;
            """;

            command.Parameters.AddWithValue("$page_id", title.PageId);
            command.Parameters.AddWithValue("$page_name", title.PageName);
            command.Parameters.AddWithValue("$display_title", ToDbValue(title.DisplayTitle));
            command.Parameters.AddWithValue("$steam_app_ids", steamAppIdsJson);
            command.Parameters.AddWithValue("$source_url", title.SourceUrl);

            command.ExecuteNonQuery();

            foreach (string steamAppId in title.SteamAppIds)
            {
                UpsertGameTitle(
                    steamAppId,
                    title.DisplayTitle ?? title.PageName.Replace('_', ' '),
                    title.PageId,
                    title.PageName,
                    title.SourceUrl);
            }
        }

        public List<PcgwTitle> GetPendingPcgwTitles(int limit)
        {
            var results = new List<PcgwTitle>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT
                page_id,
                page_name,
                display_title,
                steam_app_ids,
                source_url
            FROM external_pcgamingwiki_pages
            WHERE harvest_status IN ('Pending', 'FailedRetryable')
            ORDER BY page_id ASC
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string steamAppIdsJson = reader.GetString(reader.GetOrdinal("steam_app_ids"));
                List<string>? steamAppIds = JsonSerializer.Deserialize<List<string>>(steamAppIdsJson);

                if (steamAppIds is null || steamAppIds.Count == 0)
                    continue;

                results.Add(new PcgwTitle(
                    reader.GetInt32(reader.GetOrdinal("page_id")),
                    reader.GetString(reader.GetOrdinal("page_name")),
                    GetNullableString(reader, "display_title"),
                    steamAppIds,
                    reader.GetString(reader.GetOrdinal("source_url"))));
            }

            return results;
        }

        public void MarkPcgwTitleHarvested(
            PcgwTitle title,
            string rawWikitextPath,
            string extractedJsonPath,
            string rawSha256)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE external_pcgamingwiki_pages
            SET raw_wikitext_path = $raw_wikitext_path,
                extracted_json_path = $extracted_json_path,
                raw_sha256 = $raw_sha256,
                last_fetched_utc = CURRENT_TIMESTAMP,
                last_extracted_utc = CURRENT_TIMESTAMP,
                harvest_status = 'Harvested',
                error = NULL
            WHERE page_id = $page_id;
            """;

            command.Parameters.AddWithValue("$page_id", title.PageId);
            command.Parameters.AddWithValue("$raw_wikitext_path", rawWikitextPath);
            command.Parameters.AddWithValue("$extracted_json_path", extractedJsonPath);
            command.Parameters.AddWithValue("$raw_sha256", rawSha256);

            command.ExecuteNonQuery();
        }

        public void MarkPcgwTitleFailed(PcgwTitle title, string error, bool retryable)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE external_pcgamingwiki_pages
            SET harvest_status = $harvest_status,
                error = $error
            WHERE page_id = $page_id;
            """;

            command.Parameters.AddWithValue("$page_id", title.PageId);
            command.Parameters.AddWithValue("$harvest_status", retryable ? "FailedRetryable" : "FailedPermanent");
            command.Parameters.AddWithValue("$error", error);

            command.ExecuteNonQuery();
        }

        private void UpsertGameTitle(
            string steamAppId,
            string title,
            int pcgwPageId,
            string pcgwPageName,
            string sourceUrl)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO game_titles (
                steam_app_id,
                title,
                pcgw_page_id,
                pcgw_page_name,
                source_name,
                source_url,
                source_license,
                notes
            )
            VALUES (
                $steam_app_id,
                $title,
                $pcgw_page_id,
                $pcgw_page_name,
                'PCGamingWiki',
                $source_url,
                'CC-BY-NC-SA unless otherwise noted',
                'Imported from PCGamingWiki title index.'
            )
            ON CONFLICT (steam_app_id)
            DO UPDATE SET
                title = excluded.title,
                pcgw_page_id = excluded.pcgw_page_id,
                pcgw_page_name = excluded.pcgw_page_name,
                source_url = excluded.source_url,
                last_updated_utc = CURRENT_TIMESTAMP;
            """;

            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$pcgw_page_id", pcgwPageId);
            command.Parameters.AddWithValue("$pcgw_page_name", pcgwPageName);
            command.Parameters.AddWithValue("$source_url", sourceUrl);

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