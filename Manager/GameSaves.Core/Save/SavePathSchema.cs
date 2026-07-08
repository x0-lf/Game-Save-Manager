namespace GameSaves.Core.Save
{
    public static class SavePathSchema
    {
        public const string CreateSchemaSql = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS schema_migrations (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            applied_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS save_path_mappings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            steam_app_id TEXT NOT NULL,
            game_name TEXT NULL,
            platform TEXT NOT NULL,
            path_template TEXT NOT NULL,
            path_kind TEXT NOT NULL DEFAULT 'Directory',
            source_name TEXT NOT NULL,
            source_url TEXT NULL,
            source_license TEXT NULL,
            notes TEXT NULL,
            priority INTEGER NOT NULL DEFAULT 100,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

            UNIQUE (steam_app_id, platform, path_template)
        );

        CREATE INDEX IF NOT EXISTS idx_save_path_mappings_app_platform
            ON save_path_mappings (steam_app_id, platform);

        CREATE TABLE IF NOT EXISTS verification_results (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            mapping_id INTEGER NOT NULL,
            steam_app_id TEXT NOT NULL,
            expanded_path TEXT NOT NULL,
            normalized_path TEXT NOT NULL,
            exists_flag INTEGER NOT NULL,
            is_directory INTEGER NOT NULL,
            file_count INTEGER NOT NULL DEFAULT 0,
            total_bytes INTEGER NOT NULL DEFAULT 0,
            confidence INTEGER NOT NULL,
            last_verified_utc TEXT NOT NULL,
            error TEXT NULL,

            FOREIGN KEY (mapping_id)
                REFERENCES save_path_mappings(id)
                ON DELETE CASCADE,

            UNIQUE (mapping_id, normalized_path)
        );

        CREATE INDEX IF NOT EXISTS idx_verification_results_app
            ON verification_results (steam_app_id);

        CREATE TABLE IF NOT EXISTS backup_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            destination_root TEXT NOT NULL,
            dry_run INTEGER NOT NULL,
            item_count INTEGER NOT NULL DEFAULT 0,
            total_bytes INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS backup_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            backup_run_id INTEGER NOT NULL,
            steam_app_id TEXT NOT NULL,
            game_name TEXT NOT NULL,
            source_path TEXT NOT NULL,
            destination_path TEXT NOT NULL,
            copied INTEGER NOT NULL,
            bytes INTEGER NOT NULL DEFAULT 0,
            sha256 TEXT NULL,
            error TEXT NULL,

            FOREIGN KEY (backup_run_id)
                REFERENCES backup_runs(id)
                ON DELETE CASCADE
        );

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