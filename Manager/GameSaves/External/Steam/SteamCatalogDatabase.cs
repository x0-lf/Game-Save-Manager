using Microsoft.Data.Sqlite;

namespace GameSave.External.Steam
{
    public sealed class SteamCatalogDatabase
    {
        private readonly string _connectionString;

        public SteamCatalogDatabase(string databasePath)
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
            command.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS steam_catalog_apps (
                steam_app_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                app_kind TEXT NOT NULL,
                last_modified_unix INTEGER NULL,
                price_change_number TEXT NULL,
                source_name TEXT NOT NULL DEFAULT 'Steam IStoreService/GetAppList',
                first_seen_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                last_seen_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                last_imported_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_steam_catalog_apps_kind
                ON steam_catalog_apps (app_kind);

            CREATE INDEX IF NOT EXISTS idx_steam_catalog_apps_name
                ON steam_catalog_apps (name);

            CREATE TABLE IF NOT EXISTS steam_catalog_fetch_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                completed_utc TEXT NULL,
                app_kind TEXT NOT NULL,
                output_root TEXT NOT NULL,
                apps_fetched INTEGER NOT NULL DEFAULT 0,
                max_results_per_page INTEGER NOT NULL,
                if_modified_since_unix INTEGER NULL,
                error TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS steam_catalog_harvest_queue (
                steam_app_id TEXT PRIMARY KEY,
                app_kind TEXT NOT NULL,
                name TEXT NOT NULL,
                queue_status TEXT NOT NULL DEFAULT 'Pending',
                queued_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                exported_utc TEXT NULL,
                harvested_utc TEXT NULL,
                reviewed_utc TEXT NULL,
                last_error TEXT NULL,

                FOREIGN KEY (steam_app_id)
                    REFERENCES steam_catalog_apps(steam_app_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_steam_catalog_harvest_queue_status
                ON steam_catalog_harvest_queue (queue_status, steam_app_id);

            CREATE TABLE IF NOT EXISTS game_titles (
                steam_app_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                platform_hint TEXT NULL,
                pcgw_page_id INTEGER NULL,
                pcgw_page_name TEXT NULL,
                source_name TEXT NOT NULL,
                source_url TEXT NULL,
                source_license TEXT NULL,
                first_seen_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                last_updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                notes TEXT NULL
            );
            """;

            command.ExecuteNonQuery();
        }

        public long StartFetchRun(
            SteamCatalogAppKind kind,
            string outputRoot,
            int maxResultsPerPage,
            long? ifModifiedSinceUnix)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO steam_catalog_fetch_runs (
                app_kind,
                output_root,
                max_results_per_page,
                if_modified_since_unix
            )
            VALUES (
                $app_kind,
                $output_root,
                $max_results_per_page,
                $if_modified_since_unix
            );

            SELECT last_insert_rowid();
            """;

            command.Parameters.AddWithValue("$app_kind", kind.ToString());
            command.Parameters.AddWithValue("$output_root", outputRoot);
            command.Parameters.AddWithValue("$max_results_per_page", maxResultsPerPage);
            command.Parameters.AddWithValue("$if_modified_since_unix", ToDbValue(ifModifiedSinceUnix));

            return (long)(command.ExecuteScalar() ?? 0L);
        }

        public void CompleteFetchRun(
            long runId,
            int appsFetched,
            string? error)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE steam_catalog_fetch_runs
            SET completed_utc = CURRENT_TIMESTAMP,
                apps_fetched = $apps_fetched,
                error = $error
            WHERE id = $id;
            """;

            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$apps_fetched", appsFetched);
            command.Parameters.AddWithValue("$error", ToDbValue(error));

            command.ExecuteNonQuery();
        }

        public void UpsertApps(IEnumerable<SteamCatalogApp> apps)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            foreach (SteamCatalogApp app in apps)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = """
                INSERT INTO steam_catalog_apps (
                    steam_app_id,
                    name,
                    app_kind,
                    last_modified_unix,
                    price_change_number,
                    source_name,
                    last_seen_utc,
                    last_imported_utc
                )
                VALUES (
                    $steam_app_id,
                    $name,
                    $app_kind,
                    $last_modified_unix,
                    $price_change_number,
                    'Steam IStoreService/GetAppList',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (steam_app_id)
                DO UPDATE SET
                    name = excluded.name,
                    app_kind = excluded.app_kind,
                    last_modified_unix = excluded.last_modified_unix,
                    price_change_number = excluded.price_change_number,
                    last_seen_utc = CURRENT_TIMESTAMP,
                    last_imported_utc = CURRENT_TIMESTAMP;
                """;

                command.Parameters.AddWithValue("$steam_app_id", app.SteamAppId);
                command.Parameters.AddWithValue("$name", app.Name);
                command.Parameters.AddWithValue("$app_kind", app.Kind.ToString());
                command.Parameters.AddWithValue("$last_modified_unix", ToDbValue(app.LastModifiedUnix));
                command.Parameters.AddWithValue("$price_change_number", ToDbValue(app.PriceChangeNumber));

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public int SyncGameTitlesFromCatalog(SteamCatalogAppKind kind)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO game_titles (
                steam_app_id,
                title,
                platform_hint,
                source_name,
                source_url,
                source_license,
                notes
            )
            SELECT
                steam_app_id,
                name,
                'windows',
                'SteamCatalog',
                'https://store.steampowered.com/app/' || steam_app_id,
                NULL,
                'Imported from Steam catalog. Save-path support not confirmed.'
            FROM steam_catalog_apps
            WHERE app_kind = $app_kind
            ON CONFLICT (steam_app_id)
            DO UPDATE SET
                title = excluded.title,
                source_url = excluded.source_url,
                last_updated_utc = CURRENT_TIMESTAMP;
            """;

            command.Parameters.AddWithValue("$app_kind", kind.ToString());

            return command.ExecuteNonQuery();
        }

        public List<string> GetMissingAppIdsForHarvest(
            SteamCatalogAppKind kind,
            int limit,
            bool excludeAlreadyPcgwLinked)
        {
            var results = new List<string>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            string excludePcgwSql = excludeAlreadyPcgwLinked
                ? """
                  AND NOT EXISTS (
                      SELECT 1
                      FROM game_titles gt
                      WHERE gt.steam_app_id = c.steam_app_id
                        AND gt.pcgw_page_id IS NOT NULL
                  )
                  """
                : string.Empty;

            command.CommandText = $"""
            SELECT c.steam_app_id
            FROM steam_catalog_apps c
            WHERE c.app_kind = $app_kind
              AND NOT EXISTS (
                  SELECT 1
                  FROM save_path_mappings m
                  WHERE m.steam_app_id = c.steam_app_id
                    AND m.enabled = 1
              )
              {excludePcgwSql}
            ORDER BY CAST(c.steam_app_id AS INTEGER) ASC
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$app_kind", kind.ToString());
            command.Parameters.AddWithValue("$limit", limit <= 0 ? int.MaxValue : limit);

            using var reader = command.ExecuteReader();

            while (reader.Read())
                results.Add(reader.GetString(0));

            return results;
        }

        public int CountCatalogApps(SteamCatalogAppKind kind)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT COUNT(*)
            FROM steam_catalog_apps
            WHERE app_kind = $app_kind;
            """;

            command.Parameters.AddWithValue("$app_kind", kind.ToString());

            return Convert.ToInt32(command.ExecuteScalar());
        }

        public int EnqueueMissingGamesForHarvest()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO steam_catalog_harvest_queue (
                steam_app_id,
                app_kind,
                name,
                queue_status
            )
            SELECT
                c.steam_app_id,
                c.app_kind,
                c.name,
                'Pending'
            FROM steam_catalog_apps c
            WHERE c.app_kind = 'Game'
              AND NOT EXISTS (
                  SELECT 1
                  FROM save_path_mappings m
                  WHERE m.steam_app_id = c.steam_app_id
                    AND m.enabled = 1
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM steam_catalog_harvest_queue q
                  WHERE q.steam_app_id = c.steam_app_id
              );
            """;

            return command.ExecuteNonQuery();
        }

        public List<string> ExportNextQueuedAppIdsForHarvest(int limit)
        {
            var appIds = new List<string>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = """
                SELECT steam_app_id
                FROM steam_catalog_harvest_queue
                WHERE queue_status = 'Pending'
                ORDER BY CAST(steam_app_id AS INTEGER)
                LIMIT $limit;
                """;

                selectCommand.Parameters.AddWithValue("$limit", limit <= 0 ? 1000 : limit);

                using var reader = selectCommand.ExecuteReader();

                while (reader.Read())
                    appIds.Add(reader.GetString(0));
            }

            foreach (string appId in appIds)
            {
                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                UPDATE steam_catalog_harvest_queue
                SET queue_status = 'Exported',
                    exported_utc = CURRENT_TIMESTAMP
                WHERE steam_app_id = $steam_app_id;
                """;

                updateCommand.Parameters.AddWithValue("$steam_app_id", appId);
                updateCommand.ExecuteNonQuery();
            }

            transaction.Commit();

            return appIds;
        }

        public int CountHarvestQueueByStatus(string status)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT COUNT(*)
            FROM steam_catalog_harvest_queue
            WHERE queue_status = $queue_status;
            """;

            command.Parameters.AddWithValue("$queue_status", status);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void MarkHarvestQueueStatus(
            string steamAppId,
            string status,
            string? error = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            UPDATE steam_catalog_harvest_queue
            SET queue_status = $queue_status,
                harvested_utc = CASE
                    WHEN $queue_status IN ('Harvested', 'NoPcgwPage', 'ExtractedNoPaths', 'FailedRetryable', 'FailedPermanent')
                    THEN CURRENT_TIMESTAMP
                    ELSE harvested_utc
                END,
                reviewed_utc = CASE
                    WHEN $queue_status = 'ReviewedSupported'
                    THEN CURRENT_TIMESTAMP
                    ELSE reviewed_utc
                END,
                last_error = $last_error
            WHERE steam_app_id = $steam_app_id;
            """;

            command.Parameters.AddWithValue("$steam_app_id", steamAppId);
            command.Parameters.AddWithValue("$queue_status", status);
            command.Parameters.AddWithValue("$last_error", ToDbValue(error));

            command.ExecuteNonQuery();
        }

        public List<(string SteamAppId, string Name, string Status)> GetHarvestQueuePreview(
            string status,
            int limit)
        {
            var results = new List<(string SteamAppId, string Name, string Status)>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT steam_app_id, name, queue_status
            FROM steam_catalog_harvest_queue
            WHERE queue_status = $queue_status
            ORDER BY CAST(steam_app_id AS INTEGER)
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$queue_status", status);
            command.Parameters.AddWithValue("$limit", limit <= 0 ? 20 : limit);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }

            return results;
        }

        public static int EnqueueMissingGamesForHarvest(string databasePath)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            return database.EnqueueMissingGamesForHarvest();
        }


        public static void MarkHarvestQueueStatus(
            string databasePath,
            string steamAppId,
            string status,
            string? error = null)
        {
            var database = new SteamCatalogDatabase(databasePath);
            database.Initialize();

            database.MarkHarvestQueueStatus(steamAppId, status, error);
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value;
        }

        private static object ToDbValue(long? value)
        {
            return value is null
                ? DBNull.Value
                : value.Value;
        }
    }
}