using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V006__ScheduledBackupJobs : ISchemaMigration
    {
        public int Version => 6;
        public string Name => "V006__ScheduledBackupJobs";
        public string Description => "Add scheduled_backup_jobs, the opt-in list of unattended backups.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduled_backup_jobs (
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
            """;
            command.ExecuteNonQuery();
        }
    }
}
