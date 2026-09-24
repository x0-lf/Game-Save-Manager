using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V003__SyncAndSecretStorage : ISchemaMigration
    {
        public int Version => 3;
        public string Name => "V003__SyncAndSecretStorage";
        public string Description => "Formalize sync remote profiles, protected secrets, transfer runs, and manual backup presets in schema catalog.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_remote_profiles (
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

            CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_remote_profiles_display_name
                ON sync_remote_profiles(display_name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS protected_sync_secrets (
                owner_id TEXT NOT NULL,
                secret_name TEXT NOT NULL,
                protection_scheme TEXT NOT NULL,
                format_version INTEGER NOT NULL,
                protected_payload BLOB NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (owner_id, secret_name)
            );

            CREATE INDEX IF NOT EXISTS idx_protected_sync_secrets_owner
                ON protected_sync_secrets(owner_id);

            CREATE TABLE IF NOT EXISTS transfer_runs (
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

            CREATE TABLE IF NOT EXISTS transfer_items (
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

            CREATE INDEX IF NOT EXISTS idx_transfer_items_run_id
                ON transfer_items (run_id);

            CREATE TABLE IF NOT EXISTS manual_backup_presets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                destination_root TEXT NOT NULL,
                include_userdata INTEGER NOT NULL,
                include_mappings INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                last_used_utc TEXT NULL
            );
            """;
            command.ExecuteNonQuery();
        }
    }
}
