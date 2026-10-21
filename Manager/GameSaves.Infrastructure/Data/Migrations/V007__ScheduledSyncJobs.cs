using GameSaves.Core.Data;
using System.Data.Common;

namespace GameSaves.Infrastructure.Data.Migrations
{
    public sealed class V007__ScheduledSyncJobs : ISchemaMigration
    {
        public int Version => 7;
        public string Name => "V007__ScheduledSyncJobs";
        public string Description => "Add scheduled_sync_jobs, the opt-in list of unattended profile syncs.";

        public void Up(DbConnection connection, DbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // One job per profile: the run lock is keyed by the profile, and
            // two jobs for one profile would only race each other.
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduled_sync_jobs (
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
            """;
            command.ExecuteNonQuery();
        }
    }
}
