using System.Globalization;
using GameSaves.Core.Sync;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Sync
{
    /// <summary>
    /// Scheduled sync jobs in the app database. The table comes from the V007
    /// migration, which runs before the first connection.
    /// </summary>
    public sealed class SqliteScheduledSyncJobRepository : IScheduledSyncJobRepository
    {
        private readonly string _connectionString;

        public SqliteScheduledSyncJobRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();
        }

        public IReadOnlyList<ScheduledSyncJob> GetAll()
        {
            var jobs = new List<ScheduledSyncJob>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
            SELECT id, profile_id, profile_name, upload, download, archive_sync,
                   created_utc, last_run_utc, last_outcome
            FROM scheduled_sync_jobs
            ORDER BY profile_name COLLATE NOCASE, created_utc;
            """;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                jobs.Add(new ScheduledSyncJob(
                    Id: Guid.Parse(reader.GetString(0)),
                    ProfileId: Guid.Parse(reader.GetString(1)),
                    ProfileName: reader.GetString(2),
                    Upload: reader.GetInt64(3) != 0,
                    Download: reader.GetInt64(4) != 0,
                    ArchiveSync: reader.GetInt64(5) != 0,
                    CreatedUtc: ParseUtc(reader.GetString(6)),
                    LastRunUtc: reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                    LastOutcome: reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return jobs;
        }

        public void Add(ScheduledSyncJob job) => Execute(
            """
            INSERT INTO scheduled_sync_jobs (
                id, profile_id, profile_name, upload, download, archive_sync, created_utc)
            VALUES ($id, $profile, $name, $upload, $download, $archive, $created);
            """,
            ("$id", job.Id.ToString("D")),
            ("$profile", job.ProfileId.ToString("D")),
            ("$name", job.ProfileName),
            ("$upload", job.Upload ? 1 : 0),
            ("$download", job.Download ? 1 : 0),
            ("$archive", job.ArchiveSync ? 1 : 0),
            ("$created", job.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)));

        public void RecordOutcome(Guid id, DateTimeOffset runUtc, string outcome) => Execute(
            """
            UPDATE scheduled_sync_jobs
            SET last_run_utc = $run, last_outcome = $outcome
            WHERE id = $id;
            """,
            ("$id", id.ToString("D")),
            ("$run", runUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$outcome", outcome));

        public void Delete(Guid id) => Execute(
            "DELETE FROM scheduled_sync_jobs WHERE id = $id;",
            ("$id", id.ToString("D")));

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;

            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);

            command.ExecuteNonQuery();
        }

        private static DateTimeOffset ParseUtc(string value) =>
            DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
