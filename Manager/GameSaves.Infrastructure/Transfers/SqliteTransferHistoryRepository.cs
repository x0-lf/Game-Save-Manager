using GameSaves.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace GameSaves.Infrastructure.Transfers
{
    public sealed class SqliteTransferHistoryRepository : ITransferHistoryRepository
    {
        private readonly string _databasePath;
        private readonly string _connectionString;

        public SqliteTransferHistoryRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            _databasePath = databasePath;

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();
        }

        public long RecordRun(TransferRunRecord record)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using var runCommand = connection.CreateCommand();
            runCommand.Transaction = transaction;
            runCommand.CommandText = """
            INSERT INTO transfer_runs (
                kind, game_name, steam_app_id,
                source_account_id, target_account_id,
                dry_run, overwrite_enabled, backup_enabled,
                files_considered, files_copied, files_skipped, files_failed,
                bytes_copied, files_backed_up,
                backup_root_path, blocked_reason,
                started_utc, completed_utc)
            VALUES (
                $kind, $game_name, $steam_app_id,
                $source_account_id, $target_account_id,
                $dry_run, $overwrite_enabled, $backup_enabled,
                $files_considered, $files_copied, $files_skipped, $files_failed,
                $bytes_copied, $files_backed_up,
                $backup_root_path, $blocked_reason,
                $started_utc, $completed_utc);
            SELECT last_insert_rowid();
            """;

            runCommand.Parameters.AddWithValue("$kind", record.Kind.ToString());
            runCommand.Parameters.AddWithValue("$game_name", record.GameName);
            runCommand.Parameters.AddWithValue("$steam_app_id", record.SteamAppId);
            runCommand.Parameters.AddWithValue("$source_account_id", record.SourceAccountId);
            runCommand.Parameters.AddWithValue("$target_account_id", record.TargetAccountId);
            runCommand.Parameters.AddWithValue("$dry_run", record.DryRun ? 1 : 0);
            runCommand.Parameters.AddWithValue("$overwrite_enabled", record.OverwriteEnabled ? 1 : 0);
            runCommand.Parameters.AddWithValue("$backup_enabled", record.BackupEnabled ? 1 : 0);
            runCommand.Parameters.AddWithValue("$files_considered", record.FilesConsidered);
            runCommand.Parameters.AddWithValue("$files_copied", record.FilesCopied);
            runCommand.Parameters.AddWithValue("$files_skipped", record.FilesSkipped);
            runCommand.Parameters.AddWithValue("$files_failed", record.FilesFailed);
            runCommand.Parameters.AddWithValue("$bytes_copied", record.BytesCopied);
            runCommand.Parameters.AddWithValue("$files_backed_up", record.FilesBackedUp);
            runCommand.Parameters.AddWithValue("$backup_root_path", (object?)record.BackupRootPath ?? DBNull.Value);
            runCommand.Parameters.AddWithValue("$blocked_reason", (object?)record.BlockedReason ?? DBNull.Value);
            runCommand.Parameters.AddWithValue("$started_utc", record.StartedUtc.ToString("O"));
            runCommand.Parameters.AddWithValue("$completed_utc", record.CompletedUtc.ToString("O"));

            long runId = (long)runCommand.ExecuteScalar()!;

            if (record.Items.Count > 0)
            {
                using var itemCommand = connection.CreateCommand();
                itemCommand.Transaction = transaction;
                itemCommand.CommandText = """
                INSERT INTO transfer_items (
                    run_id, source_file, target_file, bytes,
                    copied, status, error, backup_file)
                VALUES (
                    $run_id, $source_file, $target_file, $bytes,
                    $copied, $status, $error, $backup_file);
                """;

                SqliteParameter pRunId = itemCommand.Parameters.Add("$run_id", SqliteType.Integer);
                SqliteParameter pSource = itemCommand.Parameters.Add("$source_file", SqliteType.Text);
                SqliteParameter pTarget = itemCommand.Parameters.Add("$target_file", SqliteType.Text);
                SqliteParameter pBytes = itemCommand.Parameters.Add("$bytes", SqliteType.Integer);
                SqliteParameter pCopied = itemCommand.Parameters.Add("$copied", SqliteType.Integer);
                SqliteParameter pStatus = itemCommand.Parameters.Add("$status", SqliteType.Text);
                SqliteParameter pError = itemCommand.Parameters.Add("$error", SqliteType.Text);
                SqliteParameter pBackup = itemCommand.Parameters.Add("$backup_file", SqliteType.Text);

                foreach (TransferRunItemRecord item in record.Items)
                {
                    pRunId.Value = runId;
                    pSource.Value = item.SourceFile;
                    pTarget.Value = item.TargetFile;
                    pBytes.Value = item.Bytes;
                    pCopied.Value = item.Copied ? 1 : 0;
                    pStatus.Value = item.Status;
                    pError.Value = (object?)item.Error ?? DBNull.Value;
                    pBackup.Value = (object?)item.BackupFile ?? DBNull.Value;

                    itemCommand.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return runId;
        }

        public IReadOnlyList<TransferRunInfo> GetRecentRuns(int limit)
        {
            var runs = new List<TransferRunInfo>();

            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            SELECT
                id, kind, game_name, steam_app_id,
                source_account_id, target_account_id,
                dry_run, overwrite_enabled, backup_enabled,
                files_considered, files_copied, files_skipped, files_failed,
                bytes_copied, files_backed_up,
                backup_root_path, blocked_reason,
                started_utc, completed_utc
            FROM transfer_runs
            ORDER BY id DESC
            LIMIT $limit;
            """;

            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                runs.Add(new TransferRunInfo(
                    Id: reader.GetInt64(0),
                    Kind: ParseKind(reader.GetString(1)),
                    GameName: reader.GetString(2),
                    SteamAppId: reader.GetString(3),
                    SourceAccountId: reader.GetString(4),
                    TargetAccountId: reader.GetString(5),
                    DryRun: reader.GetInt64(6) != 0,
                    OverwriteEnabled: reader.GetInt64(7) != 0,
                    BackupEnabled: reader.GetInt64(8) != 0,
                    FilesConsidered: (int)reader.GetInt64(9),
                    FilesCopied: (int)reader.GetInt64(10),
                    FilesSkipped: (int)reader.GetInt64(11),
                    FilesFailed: (int)reader.GetInt64(12),
                    BytesCopied: reader.GetInt64(13),
                    FilesBackedUp: (int)reader.GetInt64(14),
                    BackupRootPath: reader.IsDBNull(15) ? null : reader.GetString(15),
                    BlockedReason: reader.IsDBNull(16) ? null : reader.GetString(16),
                    StartedUtc: DateTimeOffset.Parse(reader.GetString(17)),
                    CompletedUtc: DateTimeOffset.Parse(reader.GetString(18))));
            }

            return runs;
        }

        public IReadOnlyList<TransferRunItemRecord> GetRunItems(long runId)
        {
            var items = new List<TransferRunItemRecord>();

            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
            SELECT source_file, target_file, bytes, copied, status, error, backup_file
            FROM transfer_items
            WHERE run_id = $run_id
            ORDER BY id ASC;
            """;

            command.Parameters.AddWithValue("$run_id", runId);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                items.Add(new TransferRunItemRecord(
                    SourceFile: reader.GetString(0),
                    TargetFile: reader.GetString(1),
                    Bytes: reader.GetInt64(2),
                    Copied: reader.GetInt64(3) != 0,
                    Status: reader.GetString(4),
                    Error: reader.IsDBNull(5) ? null : reader.GetString(5),
                    BackupFile: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return items;
        }

        public int CountRuns()
        {
            using SqliteConnection connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM transfer_runs;";

            return Convert.ToInt32(command.ExecuteScalar());
        }

        private SqliteConnection OpenConnection()
        {
            string? directory = Path.GetDirectoryName(_databasePath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
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
                ON transfer_items(run_id);
            """;
            command.ExecuteNonQuery();

            return connection;
        }

        private static TransferRunKind ParseKind(string value)
        {
            return Enum.TryParse(value, ignoreCase: true, out TransferRunKind kind)
                ? kind
                : TransferRunKind.TransferCopy;
        }
    }
}
