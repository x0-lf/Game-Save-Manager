using GameSaves.Core.Data;
using GameSaves.Infrastructure.Data.Migrations;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace GameSaves.Infrastructure.Data
{
    public sealed class SchemaMigrator : ISchemaMigrator
    {
        public const int DefaultBackupRetentionCount = 10;

        private readonly IReadOnlyList<ISchemaMigration> _migrations;

        public SchemaMigrator()
            : this(GetDefaultMigrations())
        {
        }

        // Internal on purpose: a DI container resolves IEnumerable<ISchemaMigration>
        // to an empty sequence, and a migrator built that way migrates nothing.
        internal SchemaMigrator(IEnumerable<ISchemaMigration> migrations)
        {
            _migrations = migrations
                .OrderBy(m => m.Version)
                .ToList();
        }

        public static IReadOnlyList<ISchemaMigration> GetDefaultMigrations() =>
        [
            new V001__BaselineSchema(),
            new V002__ReviewColumnsAndProvenance(),
            new V003__SyncAndSecretStorage(),
            new V004__CatalogAndMappingIndexes(),
            new V005__DropDuplicateTransferItemsIndex()
        ];

        public MigrationPlan Plan(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            bool integrityPassed = VerifyIntegrity(databasePath, out string integrityMessage);

            // A file that fails the integrity check has no trustworthy history;
            // the failed check is the answer the caller needs.
            IReadOnlyList<SchemaMigrationRecord> applied = integrityPassed
                ? GetAppliedMigrations(databasePath)
                : Array.Empty<SchemaMigrationRecord>();

            int currentVersion = applied.Count > 0 ? applied.Max(m => m.Id) : 0;

            var pending = GetPending(applied.Select(a => a.Id).ToHashSet())
                .Select(m => new SchemaMigrationInfo(m.Version, m.Name, m.Description))
                .ToList();

            int targetVersion = _migrations.Count > 0
                ? Math.Max(currentVersion, _migrations.Max(m => m.Version))
                : currentVersion;

            return new MigrationPlan(
                DatabasePath: databasePath,
                CurrentVersion: currentVersion,
                TargetVersion: targetVersion,
                PendingMigrations: pending,
                IntegrityCheckPassed: integrityPassed,
                IntegrityMessage: integrityMessage,
                PlannedBackupDirectory: pending.Count > 0 && File.Exists(databasePath)
                    ? GetBackupDirectory(databasePath)
                    : null);
        }

        public MigrationExecutionResult Migrate(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            bool fileExisted = File.Exists(databasePath);
            int previousVersion = 0;
            string? backupPath = null;

            if (fileExisted)
            {
                // Read the history first: the integrity check and the snapshot are
                // only worth their cost when there is something to apply, and this
                // runs on every application start.
                try
                {
                    IReadOnlyList<SchemaMigrationRecord> applied = GetAppliedMigrations(databasePath);
                    previousVersion = applied.Count > 0 ? applied.Max(m => m.Id) : 0;

                    if (GetPending(applied.Select(a => a.Id).ToHashSet()).Count == 0)
                    {
                        return new MigrationExecutionResult(
                            Success: true,
                            PreviousVersion: previousVersion,
                            CurrentVersion: previousVersion,
                            AppliedMigrations: Array.Empty<string>(),
                            PreMigrationBackupPath: null,
                            RolledBack: false,
                            ErrorMessage: null);
                    }
                }
                catch (SqliteException ex) when (IsLockOrBusyError(ex))
                {
                    return Failed(previousVersion, null, rolledBack: false, LockedMessage);
                }
                catch (SqliteException)
                {
                    // Unreadable history: the integrity check below says why.
                }

                if (!VerifyIntegrity(databasePath, out string integrityError))
                {
                    return Failed(
                        previousVersion,
                        null,
                        rolledBack: false,
                        $"Cannot migrate database: integrity check failed with '{integrityError}'.");
                }

                try
                {
                    backupPath = Backup(databasePath);
                }
                catch (Exception ex)
                {
                    return Failed(
                        previousVersion,
                        null,
                        rolledBack: false,
                        $"Pre-migration database backup failed: {ex.Message}. Migration refused for safety.");
                }
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true
            }.ToString();

            var appliedNames = new List<string>();
            ISchemaMigration? current = null;
            SqliteTransaction? transaction = null;

            using var connection = new SqliteConnection(connectionString);

            try
            {
                connection.Open();

                // One BEGIN IMMEDIATE transaction for every pending migration: the
                // history is re-read under the write lock, so two processes starting
                // together cannot both apply a migration, and the schema either
                // reaches the target version or stays exactly where it was.
                transaction = connection.BeginTransaction();

                EnsureSchemaMigrationsTable(connection, transaction);
                HashSet<int> appliedIds = ReadAppliedIds(connection, transaction);
                previousVersion = appliedIds.Count > 0 ? appliedIds.Max() : 0;
                int currentVersion = previousVersion;

                foreach (ISchemaMigration migration in GetPending(appliedIds))
                {
                    current = migration;
                    migration.Up(connection, transaction);
                    RecordMigration(connection, transaction, migration);

                    appliedNames.Add(migration.Name);
                    currentVersion = Math.Max(currentVersion, migration.Version);
                }

                current = null;
                transaction.Commit();

                return new MigrationExecutionResult(
                    Success: true,
                    PreviousVersion: previousVersion,
                    CurrentVersion: currentVersion,
                    AppliedMigrations: appliedNames,
                    PreMigrationBackupPath: backupPath,
                    RolledBack: false,
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                // Nothing commits once the transaction has begun and failed: if the
                // explicit rollback throws too, SQLite rolls back when the
                // connection closes.
                bool rolledBack = transaction is not null;
                try
                {
                    transaction?.Rollback();
                }
                catch (Exception)
                {
                }

                string message = ex is SqliteException sqlite && IsLockOrBusyError(sqlite)
                    ? LockedMessage
                    : current is null
                        ? $"Schema migration failed: {ex.Message}. No migration was applied."
                        : $"Migration '{current.Name}' (v{current.Version}) failed: {ex.Message}. No migration was applied.";

                return Failed(previousVersion, backupPath, rolledBack, message);
            }
            finally
            {
                transaction?.Dispose();
            }
        }

        public string Backup(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException($"Database file not found: {databasePath}", databasePath);

            string backupDir = GetBackupDirectory(databasePath);
            Directory.CreateDirectory(backupDir);

            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string backupFileName = $"gamesave-pre-migration-{timestamp}-{Guid.NewGuid().ToString("N")[..8]}.db";
            string backupPath = Path.Combine(backupDir, backupFileName);

            var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = databasePath };
            var destBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Pooling = false
            };

            using (var sourceConn = new SqliteConnection(sourceBuilder.ToString()))
            using (var destConn = new SqliteConnection(destBuilder.ToString()))
            {
                sourceConn.Open();
                destConn.Open();
                sourceConn.BackupDatabase(destConn);

                // The snapshot is a schema recovery point, not a credential store:
                // keeping the DPAPI-protected OAuth tokens in up to ten copies would
                // mean deleting a remote profile no longer removes its secrets.
                using var scrub = destConn.CreateCommand();
                scrub.CommandText = "DROP TABLE IF EXISTS protected_sync_secrets; VACUUM;";
                scrub.ExecuteNonQuery();
            }

            PruneOldBackups(backupDir);

            return backupPath;
        }

        public bool VerifyIntegrity(string databasePath, out string message)
        {
            if (!File.Exists(databasePath))
            {
                message = "Database file does not exist (clean state).";
                return true;
            }

            try
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";

                object? result = command.ExecuteScalar();
                string output = result?.ToString() ?? string.Empty;

                if (string.Equals(output, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    message = "ok";
                    return true;
                }

                message = string.IsNullOrWhiteSpace(output) ? "quick_check returned empty response." : output;
                return false;
            }
            catch (SqliteException ex) when (IsLockOrBusyError(ex))
            {
                message = "Database is locked by another process (SQLITE_BUSY).";
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public IReadOnlyList<SchemaMigrationRecord> GetAppliedMigrations(string databasePath)
        {
            if (!File.Exists(databasePath))
                return Array.Empty<SchemaMigrationRecord>();

            var results = new List<SchemaMigrationRecord>();
            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            if (!TableExists(connection, "schema_migrations"))
                return Array.Empty<SchemaMigrationRecord>();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, applied_utc FROM schema_migrations ORDER BY id ASC;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // CURRENT_TIMESTAMP is UTC without an offset.
                DateTimeOffset appliedUtc = DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

                results.Add(new SchemaMigrationRecord(reader.GetInt32(0), reader.GetString(1), appliedUtc));
            }

            return results;
        }

        private const string LockedMessage =
            "Database is locked by another process (SQLITE_BUSY / SQLITE_LOCKED). Please close running instances of GameSave Manager and retry.";

        private static MigrationExecutionResult Failed(
            int previousVersion,
            string? backupPath,
            bool rolledBack,
            string? errorMessage) =>
            new(
                Success: false,
                PreviousVersion: previousVersion,
                CurrentVersion: previousVersion,
                AppliedMigrations: Array.Empty<string>(),
                PreMigrationBackupPath: backupPath,
                RolledBack: rolledBack,
                ErrorMessage: errorMessage);

        private List<ISchemaMigration> GetPending(HashSet<int> appliedVersions) =>
            _migrations.Where(m => !appliedVersions.Contains(m.Version)).ToList();

        private static string GetBackupDirectory(string databasePath) =>
            Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "backups");

        private static void EnsureSchemaMigrationsTable(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                applied_utc TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
        }

        private static HashSet<int> ReadAppliedIds(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM schema_migrations;";

            var ids = new HashSet<int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt32(0));

            return ids;
        }

        private static void RecordMigration(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISchemaMigration migration)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO schema_migrations (id, name, applied_utc)
            VALUES ($id, $name, CURRENT_TIMESTAMP);
            """;
            command.Parameters.AddWithValue("$id", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.ExecuteNonQuery();
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;";
            command.Parameters.AddWithValue("$table", tableName);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
        }

        private static bool IsLockOrBusyError(SqliteException ex)
        {
            return ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY = 5, SQLITE_LOCKED = 6
        }

        private static void PruneOldBackups(string backupDirectory)
        {
            try
            {
                if (!Directory.Exists(backupDirectory))
                    return;

                var files = new DirectoryInfo(backupDirectory)
                    .GetFiles("gamesave-pre-migration-*.db")
                    .OrderByDescending(f => f.Name)
                    .ToList();

                if (files.Count > DefaultBackupRetentionCount)
                {
                    foreach (FileInfo oldFile in files.Skip(DefaultBackupRetentionCount))
                    {
                        try
                        {
                            File.Delete(oldFile.FullName);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                // Never fail migration due to backup retention pruning
            }
        }
    }
}
