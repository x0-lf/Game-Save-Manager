namespace GameSaves.External
{
    public static class ExternalHarvestSchema
    {
        public const string CreateSchemaSql = """
        PRAGMA foreign_keys = ON;

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

        CREATE INDEX IF NOT EXISTS idx_game_titles_title
            ON game_titles (title);

        CREATE TABLE IF NOT EXISTS external_pcgamingwiki_pages (
            page_id INTEGER PRIMARY KEY,
            page_name TEXT NOT NULL,
            display_title TEXT NULL,
            steam_app_ids TEXT NOT NULL,
            source_url TEXT NOT NULL,
            raw_wikitext_path TEXT NULL,
            extracted_json_path TEXT NULL,
            raw_sha256 TEXT NULL,
            last_fetched_utc TEXT NULL,
            last_extracted_utc TEXT NULL,
            harvest_status TEXT NOT NULL DEFAULT 'Pending',
            error TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_external_pcgamingwiki_pages_status
            ON external_pcgamingwiki_pages (harvest_status);

        CREATE TABLE IF NOT EXISTS external_harvest_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_name TEXT NOT NULL,
            started_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            completed_utc TEXT NULL,
            output_root TEXT NOT NULL,
            requested_per_minute INTEGER NOT NULL,
            titles_indexed INTEGER NOT NULL DEFAULT 0,
            titles_processed INTEGER NOT NULL DEFAULT 0,
            titles_failed INTEGER NOT NULL DEFAULT 0,
            mappings_extracted INTEGER NOT NULL DEFAULT 0,
            stopped_reason TEXT NULL
        );
        """;
    }
}