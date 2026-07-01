namespace GameSave.Data
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
        """;
    }
}