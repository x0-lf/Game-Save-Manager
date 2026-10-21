-- Game Save Manager database fixture
-- app-version: 0.1.0
-- schema-version: 7
-- generated-by: GameSaves.Tests MigrationFixtureTests.RegenerateFixturesForTheCurrentVersion
-- Replayed by MigrationFixtureTests into a temporary file; never run it against a real database.
PRAGMA foreign_keys = OFF;
BEGIN;
CREATE TABLE schema_migrations (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    applied_utc TEXT NOT NULL
);
CREATE TABLE save_path_mappings (
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
    enabled INTEGER NOT NULL DEFAULT 0,
    review_status TEXT NOT NULL DEFAULT 'Pending',
    review_notes TEXT NULL,
    reviewed_utc TEXT NULL,
    created_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,

    UNIQUE (steam_app_id, platform, path_template)
);
CREATE TABLE verification_results (
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
CREATE TABLE backup_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc TEXT NOT NULL,
    completed_utc TEXT NULL,
    destination_root TEXT NOT NULL,
    dry_run INTEGER NOT NULL,
    item_count INTEGER NOT NULL DEFAULT 0,
    total_bytes INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE backup_items (
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
CREATE TABLE game_titles (
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
CREATE TABLE external_pcgamingwiki_pages (
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
CREATE TABLE external_harvest_runs (
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
CREATE TABLE sync_remote_profiles (
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    provider_kind INTEGER NOT NULL,
    account_display_name TEXT NULL,
    remote_root_display_name TEXT NULL,
    provider_settings_json TEXT NOT NULL,
    provider_settings_version INTEGER NOT NULL,
    remote_folder_id TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    last_used_utc TEXT NULL,
    last_successful_connection_utc TEXT NULL
);
CREATE TABLE protected_sync_secrets (
    owner_id TEXT NOT NULL,
    secret_name TEXT NOT NULL,
    protection_scheme TEXT NOT NULL,
    format_version INTEGER NOT NULL,
    protected_payload BLOB NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (owner_id, secret_name)
);
CREATE TABLE transfer_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    game_name TEXT NOT NULL,
    steam_app_id TEXT NOT NULL,
    source_account_id TEXT NOT NULL,
    target_account_id TEXT NOT NULL,
    dry_run INTEGER NOT NULL,
    overwrite_enabled INTEGER NOT NULL,
    backup_enabled INTEGER NOT NULL,
    files_considered INTEGER NOT NULL,
    files_copied INTEGER NOT NULL,
    files_skipped INTEGER NOT NULL,
    files_failed INTEGER NOT NULL,
    bytes_copied INTEGER NOT NULL,
    files_backed_up INTEGER NOT NULL,
    backup_root_path TEXT NULL,
    blocked_reason TEXT NULL,
    started_utc TEXT NOT NULL,
    completed_utc TEXT NOT NULL
);
CREATE TABLE transfer_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES transfer_runs(id),
    source_file TEXT NOT NULL,
    target_file TEXT NOT NULL,
    bytes INTEGER NOT NULL,
    copied INTEGER NOT NULL,
    status TEXT NOT NULL,
    error TEXT NULL,
    backup_file TEXT NULL
);
CREATE TABLE manual_backup_presets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
    destination_root TEXT NOT NULL,
    include_userdata INTEGER NOT NULL,
    include_mappings INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    last_used_utc TEXT NULL
);
CREATE TABLE scheduled_backup_jobs (
    id TEXT PRIMARY KEY,
    steam_app_id TEXT NOT NULL,
    game_name TEXT NOT NULL,
    steam_account_id TEXT NOT NULL,
    destination_root TEXT NOT NULL,
    include_userdata INTEGER NOT NULL,
    include_mappings INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    last_run_utc TEXT NULL,
    last_outcome TEXT NULL
);
CREATE TABLE scheduled_sync_jobs (
    id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL UNIQUE,
    profile_name TEXT NOT NULL,
    upload INTEGER NOT NULL,
    download INTEGER NOT NULL,
    archive_sync INTEGER NOT NULL,
    created_utc TEXT NOT NULL,
    last_run_utc TEXT NULL,
    last_outcome TEXT NULL
);
CREATE INDEX idx_save_path_mappings_app_platform
    ON save_path_mappings (steam_app_id, platform);
CREATE INDEX idx_verification_results_app
    ON verification_results (steam_app_id);
CREATE INDEX idx_game_titles_title
    ON game_titles (title);
CREATE INDEX idx_external_pcgamingwiki_pages_status
    ON external_pcgamingwiki_pages (harvest_status);
CREATE INDEX idx_save_path_mappings_review_status
    ON save_path_mappings (source_name, review_status, enabled);
CREATE UNIQUE INDEX idx_sync_remote_profiles_display_name
    ON sync_remote_profiles(display_name COLLATE NOCASE);
CREATE INDEX idx_protected_sync_secrets_owner
    ON protected_sync_secrets(owner_id);
CREATE INDEX idx_transfer_items_run_id
    ON transfer_items (run_id);
CREATE INDEX idx_game_titles_source
    ON game_titles(source_name);
CREATE INDEX idx_save_path_mappings_source_enabled
    ON save_path_mappings(source_name, enabled);
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (1, 'V001__BaselineSchema', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (2, 'V002__ReviewColumnsAndProvenance', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (3, 'V003__SyncAndSecretStorage', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (4, 'V004__CatalogAndMappingIndexes', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (5, 'V005__DropDuplicateTransferItemsIndex', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (6, 'V006__ScheduledBackupJobs', '2026-10-21 19:02:08');
INSERT INTO "schema_migrations" ("id", "name", "applied_utc") VALUES (7, 'V007__ScheduledSyncJobs', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (1, '220', 'Half-Life 2', 'windows', '{GameInstallPath}\hl2\SAVE', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/220/HalfLife_2/', 'MIT', 'Half-Life 2 save directory', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (2, '620', 'Portal 2', 'windows', '{SteamRoot}\userdata\{AccountId}\620\remote', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/620/Portal_2/', 'MIT', 'Portal 2 Steam Cloud remote save directory', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (3, '550', 'Left 4 Dead 2', 'windows', '{GameInstallPath}\left4dead2\save', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/550/Left_4_Dead_2/', 'MIT', 'Left 4 Dead 2 local save directory', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (4, '105600', 'Terraria', 'windows', '{Documents}\My Games\Terraria', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/105600/Terraria/', 'MIT', 'Terraria player and world saves in Documents', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (5, '413150', 'Stardew Valley', 'windows', '%APPDATA%\StardewValley\Saves', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/413150/Stardew_Valley/', 'MIT', 'Stardew Valley farm saves in AppData Roaming', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (6, '292030', 'The Witcher 3: Wild Hunt', 'windows', '{Documents}\The Witcher 3\gamesaves', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/292030/The_Witcher_3_Wild_Hunt/', 'MIT', 'The Witcher 3 save games in Documents', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (7, '1091500', 'Cyberpunk 2077', 'windows', '{UserProfile}\Saved Games\CD Projekt Red\Cyberpunk 2077', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/1091500/Cyberpunk_2077/', 'MIT', 'Cyberpunk 2077 saves in Saved Games', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (8, '1245620', 'Elden Ring', 'windows', '%APPDATA%\EldenRing', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/1245620/ELDEN_RING/', 'MIT', 'Elden Ring save files in AppData Roaming', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (9, '374320', 'Dark Souls III', 'windows', '%APPDATA%\DarkSoulsIII', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/374320/DARK_SOULS_III/', 'MIT', 'Dark Souls III saves in AppData Roaming', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (10, '814380', 'Sekiro: Shadows Die Twice', 'windows', '%APPDATA%\Sekiro', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/814380/Sekiro_Shadows_Die_Twice/', 'MIT', 'Sekiro saves in AppData Roaming', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (11, '1086940', 'Baldur''s Gate 3', 'windows', '{LocalAppData}\Larian Studios\Baldur''s Gate 3\PlayerProfiles\Public\Savegames\Story', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/1086940/Baldurs_Gate_3/', 'MIT', 'Baldur''s Gate 3 story saves in Local AppData', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (12, '1145360', 'Hades', 'windows', '{Documents}\Saved Games\Hades', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/1145360/Hades/', 'MIT', 'Hades save files in Saved Games', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (13, '367520', 'Hollow Knight', 'windows', '{LocalAppData}Low\Team Cherry\Hollow Knight', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/367520/Hollow_Knight/', 'MIT', 'Hollow Knight saves in LocalLow', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (14, '646570', 'Slay the Spire', 'windows', '{GameInstallPath}\preferences', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/646570/Slay_the_Spire/', 'MIT', 'Slay the Spire preferences and save progress', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (15, '582010', 'Monster Hunter: World', 'windows', '{SteamRoot}\userdata\{AccountId}\582010\remote', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/582010/Monster_Hunter_World/', 'MIT', 'Monster Hunter World saves in Steam Cloud remote', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (16, '227300', 'Euro Truck Simulator 2', 'windows', '{Documents}\Euro Truck Simulator 2\profiles', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/227300/Euro_Truck_Simulator_2/', 'MIT', 'Euro Truck Simulator 2 profiles and save data', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (17, '271590', 'Grand Theft Auto V', 'windows', '{Documents}\Rockstar Games\GTA V\Profiles', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/271590/Grand_Theft_Auto_V/', 'MIT', 'GTA V story mode profile saves', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (18, '377160', 'Fallout 4', 'windows', '{Documents}\My Games\Fallout4\Saves', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/377160/Fallout_4/', 'MIT', 'Fallout 4 save games in Documents', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (19, '489830', 'The Elder Scrolls V: Skyrim Special Edition', 'windows', '{Documents}\My Games\Skyrim Special Edition\Saves', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/489830/The_Elder_Scrolls_V_Skyrim_Special_Edition/', 'MIT', 'Skyrim Special Edition save games in Documents', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (20, '294100', 'RimWorld', 'windows', '{LocalAppData}Low\Ludeon Studios\RimWorld by Ludeon Studios\Saves', 'Directory', 'CuratedSeed', 'https://store.steampowered.com/app/294100/RimWorld/', 'MIT', 'RimWorld colony saves in LocalLow', 100, 1, 'Approved', 'Curated project seed distribution', '2026-10-21 19:02:08', '2026-10-21 19:02:08', '2026-10-21 19:02:08');
INSERT INTO "save_path_mappings" ("id", "steam_app_id", "game_name", "platform", "path_template", "path_kind", "source_name", "source_url", "source_license", "notes", "priority", "enabled", "review_status", "review_notes", "reviewed_utc", "created_utc", "updated_utc") VALUES (21, '440', 'Fixture Pending Game', 'windows', '%USERPROFILE%\Documents\Fixture\Saves', 'Directory', 'fixture', 'https://example.invalid/fixture', NULL, NULL, 100, 0, 'Pending', NULL, NULL, '2026-10-21 20:00:00', '2026-10-21 20:00:00');
INSERT INTO "verification_results" ("id", "mapping_id", "steam_app_id", "expanded_path", "normalized_path", "exists_flag", "is_directory", "file_count", "total_bytes", "confidence", "last_verified_utc", "error") VALUES (1, 21, '440', 'C:\Users\fixture\Documents\Fixture\Saves', 'c:\users\fixture\documents\fixture\saves', 1, 1, 3, 4096, 2, '2026-10-21 20:00:00', NULL);
INSERT INTO "backup_runs" ("id", "started_utc", "completed_utc", "destination_root", "dry_run", "item_count", "total_bytes") VALUES (1, '2026-10-21 18:00:00', '2026-10-21 18:00:05', 'D:\Backups', 0, 1, 4096);
INSERT INTO "backup_items" ("id", "backup_run_id", "steam_app_id", "game_name", "source_path", "destination_path", "copied", "bytes", "sha256", "error") VALUES (1, 1, '440', 'Fixture Pending Game', 'C:\Users\fixture\Documents\Fixture\Saves\slot1.sav', 'D:\Backups\440\slot1.sav', 1, 4096, '0000000000000000000000000000000000000000000000000000000000000000', NULL);
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('220', 'Half-Life 2', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/220/HalfLife_2/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('620', 'Portal 2', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/620/Portal_2/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('550', 'Left 4 Dead 2', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/550/Left_4_Dead_2/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('105600', 'Terraria', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/105600/Terraria/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('413150', 'Stardew Valley', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/413150/Stardew_Valley/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('292030', 'The Witcher 3: Wild Hunt', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/292030/The_Witcher_3_Wild_Hunt/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('1091500', 'Cyberpunk 2077', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/1091500/Cyberpunk_2077/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('1245620', 'Elden Ring', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/1245620/ELDEN_RING/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('374320', 'Dark Souls III', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/374320/DARK_SOULS_III/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('814380', 'Sekiro: Shadows Die Twice', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/814380/Sekiro_Shadows_Die_Twice/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('1086940', 'Baldur''s Gate 3', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/1086940/Baldurs_Gate_3/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('1145360', 'Hades', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/1145360/Hades/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('367520', 'Hollow Knight', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/367520/Hollow_Knight/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('646570', 'Slay the Spire', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/646570/Slay_the_Spire/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('582010', 'Monster Hunter: World', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/582010/Monster_Hunter_World/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('227300', 'Euro Truck Simulator 2', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/227300/Euro_Truck_Simulator_2/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('271590', 'Grand Theft Auto V', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/271590/Grand_Theft_Auto_V/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('377160', 'Fallout 4', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/377160/Fallout_4/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('489830', 'The Elder Scrolls V: Skyrim Special Edition', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/489830/The_Elder_Scrolls_V_Skyrim_Special_Edition/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('294100', 'RimWorld', 'windows', NULL, NULL, 'CuratedSeed', 'https://store.steampowered.com/app/294100/RimWorld/', 'MIT', '2026-10-21 19:02:08', '2026-10-21 19:02:08', 'Curated project seed game title');
INSERT INTO "game_titles" ("steam_app_id", "title", "platform_hint", "pcgw_page_id", "pcgw_page_name", "source_name", "source_url", "source_license", "first_seen_utc", "last_updated_utc", "notes") VALUES ('440', 'Fixture Pending Game', 'windows', NULL, NULL, 'fixture', NULL, NULL, '2026-10-21 20:00:00', '2026-10-21 20:00:00', NULL);
INSERT INTO "external_pcgamingwiki_pages" ("page_id", "page_name", "display_title", "steam_app_ids", "source_url", "raw_wikitext_path", "extracted_json_path", "raw_sha256", "last_fetched_utc", "last_extracted_utc", "harvest_status", "error") VALUES (4242, 'Fixture_Pending_Game', 'Fixture Pending Game', '440', 'https://www.pcgamingwiki.com/wiki/Fixture_Pending_Game', NULL, NULL, NULL, '2026-10-21 19:01:00', NULL, 'Extracted', NULL);
INSERT INTO "external_harvest_runs" ("id", "source_name", "started_utc", "completed_utc", "output_root", "requested_per_minute", "titles_indexed", "titles_processed", "titles_failed", "mappings_extracted", "stopped_reason") VALUES (1, 'pcgamingwiki', '2026-10-21 19:00:00', '2026-10-21 19:05:00', 'D:\Harvest', 10, 1, 1, 0, 1, NULL);
INSERT INTO "sync_remote_profiles" ("id", "display_name", "provider_kind", "account_display_name", "remote_root_display_name", "provider_settings_json", "provider_settings_version", "remote_folder_id", "created_utc", "updated_utc", "last_used_utc", "last_successful_connection_utc") VALUES ('7d2f8f0e-5c3a-4b61-9a7e-0f1c2d3e4a5b', 'Fixture folder', 0, NULL, 'D:\GameSaveSync', '{"schemaVersion":1,"localFolderPath":"D:\\GameSaveSync"}', 1, NULL, '2026-10-18T20:00:00.0000000+00:00', '2026-10-18T20:00:00.0000000+00:00', '2026-10-21T19:00:00.0000000+00:00', '2026-10-21T19:00:00.0000000+00:00');
INSERT INTO "sync_remote_profiles" ("id", "display_name", "provider_kind", "account_display_name", "remote_root_display_name", "provider_settings_json", "provider_settings_version", "remote_folder_id", "created_utc", "updated_utc", "last_used_utc", "last_successful_connection_utc") VALUES ('1b9c4e2a-6d7f-4a30-8c15-2e4f6a8b0c1d', 'Fixture Google Drive', 2, 'fixture@example.invalid', 'GameSaveManager', '{"schemaVersion":1,"accountEmail":"fixture@example.invalid","requestedScope":"https://www.googleapis.com/auth/drive.file"}', 1, 'fixture-folder-id', '2026-10-19T20:00:00.0000000+00:00', '2026-10-19T20:00:00.0000000+00:00', NULL, NULL);
INSERT INTO "protected_sync_secrets" ("owner_id", "secret_name", "protection_scheme", "format_version", "protected_payload", "created_utc", "updated_utc") VALUES ('1b9c4e2a-6d7f-4a30-8c15-2e4f6a8b0c1d', 'oauth-token-data', 'dpapi-current-user-v1', 1, X'00112233445566778899AABBCCDDEEFF', '2026-10-19T20:00:00.0000000+00:00', '2026-10-19T20:00:00.0000000+00:00');
INSERT INTO "transfer_runs" ("id", "kind", "game_name", "steam_app_id", "source_account_id", "target_account_id", "dry_run", "overwrite_enabled", "backup_enabled", "files_considered", "files_copied", "files_skipped", "files_failed", "bytes_copied", "files_backed_up", "backup_root_path", "blocked_reason", "started_utc", "completed_utc") VALUES (1, 'ManualBackup', 'Fixture Pending Game', '440', '111', '-', 0, 0, 0, 1, 1, 0, 0, 4096, 0, 'D:\Backups', NULL, '2026-10-21T18:00:00.0000000+00:00', '2026-10-21T18:00:05.0000000+00:00');
INSERT INTO "transfer_runs" ("id", "kind", "game_name", "steam_app_id", "source_account_id", "target_account_id", "dry_run", "overwrite_enabled", "backup_enabled", "files_considered", "files_copied", "files_skipped", "files_failed", "bytes_copied", "files_backed_up", "backup_root_path", "blocked_reason", "started_utc", "completed_utc") VALUES (2, 'Sync', '(backup sync)', '-', 'device', 'remote', 0, 0, 0, 1, 1, 0, 0, 4096, 0, NULL, NULL, '2026-10-21T19:00:00.0000000+00:00', '2026-10-21T19:00:03.0000000+00:00');
INSERT INTO "transfer_items" ("id", "run_id", "source_file", "target_file", "bytes", "copied", "status", "error", "backup_file") VALUES (1, 1, 'C:\Users\fixture\Documents\Fixture\Saves\slot1.sav', 'D:\Backups\440\slot1.sav', 4096, 1, 'Copied', NULL, NULL);
INSERT INTO "transfer_items" ("id", "run_id", "source_file", "target_file", "bytes", "copied", "status", "error", "backup_file") VALUES (2, 2, 'run-20261021-180000', 'remote/run-20261021-180000', 4096, 1, 'Uploaded', NULL, NULL);
INSERT INTO "manual_backup_presets" ("id", "name", "destination_root", "include_userdata", "include_mappings", "created_utc", "last_used_utc") VALUES (1, 'Fixture preset', 'D:\Backups', 1, 1, '2026-10-21T19:02:08.9268638+00:00', NULL);
INSERT INTO "scheduled_backup_jobs" ("id", "steam_app_id", "game_name", "steam_account_id", "destination_root", "include_userdata", "include_mappings", "created_utc", "last_run_utc", "last_outcome") VALUES ('3a5c7e9b-1d2f-4b6a-9c8e-0f1a2b3c4d5e', '440', 'Fixture Pending Game', '111', 'D:\Backups', 1, 0, '2026-10-20T20:00:00.0000000+00:00', '2026-10-21T18:00:00.0000000+00:00', 'Completed: Backed up 1 file(s).');
INSERT INTO "scheduled_sync_jobs" ("id", "profile_id", "profile_name", "upload", "download", "archive_sync", "created_utc", "last_run_utc", "last_outcome") VALUES ('5e7a9c1b-3d4f-4c6e-8a2b-1c3d5e7f9a0b', '7d2f8f0e-5c3a-4b61-9a7e-0f1c2d3e4a5b', 'Fixture folder', 1, 0, 1, '2026-10-20T20:00:00.0000000+00:00', '2026-10-21T19:00:00.0000000+00:00', 'Completed: Uploaded 1 run(s).');
COMMIT;
