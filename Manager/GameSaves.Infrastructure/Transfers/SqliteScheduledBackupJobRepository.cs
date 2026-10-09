using System.Globalization;
using GameSaves.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Transfers
{
    /// <summary>
    /// Scheduled backup jobs in the app database. The table comes from the
    /// V006 migration, which runs before the first connection.
    /// </summary>
    public sealed class SqliteScheduledBackupJobRepository : IScheduledBackupJobRepository
    {
        private readonly string _connectionString;

        public SqliteScheduledBackupJobRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();
        }

        public IReadOnlyList<ScheduledBackupJob> GetAll()
        {
            var jobs = new List<ScheduledBackupJob>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
            SELECT id, steam_app_id, game_name, steam_account_id, destination_root,
                   include_userdata, include_mappings, created_utc, last_run_utc, last_outcome
            FROM scheduled_backup_jobs
            ORDER BY game_name COLLATE NOCASE, created_utc;
            """;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                jobs.Add(new ScheduledBackupJob(
                    Id: Guid.Parse(reader.GetString(0)),
                    SteamAppId: reader.GetString(1),
                    GameName: reader.GetString(2),
                    SteamAccountId: reader.GetString(3),
                    DestinationRoot: reader.GetString(4),
                    IncludeSteamUserDataGameFolder: reader.GetInt64(5) != 0,
                    IncludeApprovedMappings: reader.GetInt64(6) != 0,
                    CreatedUtc: ParseUtc(reader.GetString(7)),
                    LastRunUtc: reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8)),
                    LastOutcome: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return jobs;
        }

        public void Add(ScheduledBackupJob job) => Execute(
            """
            INSERT INTO scheduled_backup_jobs (
                id, steam_app_id, game_name, steam_account_id, destination_root,
                include_userdata, include_mappings, created_utc)
            VALUES ($id, $app, $game, $account, $destination, $userdata, $mappings, $created);
            """,
            ("$id", job.Id.ToString("D")),
            ("$app", job.SteamAppId),
            ("$game", job.GameName),
            ("$account", job.SteamAccountId),
            ("$destination", job.DestinationRoot),
            ("$userdata", job.IncludeSteamUserDataGameFolder ? 1 : 0),
            ("$mappings", job.IncludeApprovedMappings ? 1 : 0),
            ("$created", job.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)));

        public void RecordOutcome(Guid id, DateTimeOffset runUtc, string outcome) => Execute(
            """
            UPDATE scheduled_backup_jobs
            SET last_run_utc = $run, last_outcome = $outcome
            WHERE id = $id;
            """,
            ("$id", id.ToString("D")),
            ("$run", runUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$outcome", outcome));

        public void Delete(Guid id) => Execute(
            "DELETE FROM scheduled_backup_jobs WHERE id = $id;",
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
