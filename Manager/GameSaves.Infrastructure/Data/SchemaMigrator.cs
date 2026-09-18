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

        public SchemaMigrator(IEnumerable<ISchemaMigration>? migrations = null)
        {
            _migrations = (migrations ?? GetDefaultMigrations())
                .OrderBy(m => m.Version)
                .ToList();
        }

        public static IReadOnlyList<ISchemaMigration> GetDefaultMigrations() =>
        [
            new V001__BaselineSchema(),
            new V002__ReviewColumnsAndProvenance(),
            new V003__SyncAndSecretStorage(),
            new V004__CatalogAndMappingIndexes()
        ];

        public MigrationPlan Plan(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            bool integrityPassed = VerifyIntegrity(databasePath, out string integrityMessage);

            int currentVersion = 0;
            IReadOnlyList<SchemaMigrationRecord> applied = Array.Empty<SchemaMigrationRecord>();

            if (File.Exists(databasePath))
            {
                applied = GetAppliedMigrations(databasePath);
                currentVersion = applied.Count > 0 ? applied.Max(m => m.Id) : 0;
            }

            var appliedNames = new HashSet<string>(applied.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

            var pending = _migrations
                .Where(m => m.Version > currentVersion || !appliedNames.Contains(m.Name))
                .Select(m => new SchemaMigrationInfo(m.Version, m.Name, m.Description))
                .ToList();

            int targetVersion = _migrations.Count > 0 ? _migrations.Max(m => m.Version) : currentVersion;

            string? plannedBackupPath = null;
            if (pending.Count > 0 && File.Exists(databasePath))
            {
                string dbDir = Path.GetDirectoryName(databasePath) ?? ".";
                string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                plannedBackupPath = Path.Combine(dbDir, "backups", $"gamesave-pre-migration-{timestamp}-v{targetVersion}.db");
            }

            return new MigrationPlan(
                DatabasePath: databasePath,
                CurrentVersion: currentVersion,
                TargetVersion: targetVersion,
                PendingMigrations: pending,
                IntegrityCheckPassed: integrityPassed,
                IntegrityMessage: integrityMessage,
                PlannedBackupPath: plannedBackupPath);
        }

        public MigrationExecutionResult Migrate(string databasePath, bool forceBackup = false)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            bool fileExisted = File.Exists(databasePath);

            // Step 1: Pre-flight integrity check
            if (fileExisted)
            {
                if (!VerifyIntegrity(databasePath, out string integrityError))
                {
                    return new MigrationExecutionResult(
                        Success: false,
                        PreviousVersion: 0,
                        CurrentVersion: 0,
                        AppliedMigrations: Array.Empty<string>(),
                        PreMigrationBackupPath: null,
                        RolledBack: false,
                        ErrorMessage: $"Cannot migrate database: integrity check failed with '{integrityError}'.");
                }
            }

            // Step 2: Determine applied & pending migrations
            IReadOnlyList<SchemaMigrationRecord> appliedRecords = fileExisted
                ? GetAppliedMigrations(databasePath)
                : Array.Empty<SchemaMigrationRecord>();

            int previousVersion = appliedRecords.Count > 0 ? appliedRecords.Max(m => m.Id) : 0;
            var appliedNames = new HashSet<string>(appliedRecords.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

            var pending = _migrations
                .Where(m => m.Version > previousVersion || !appliedNames.Contains(m.Name))
                .OrderBy(m => m.Version)
                .ToList();

            // Step 3: No-op if already up to date
            if (pending.Count == 0 && !forceBackup)
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

            // Step 4: Create pre-migration backup if file exists and migrations pending
            string? backupPath = null;
            if (fileExisted && (pending.Count > 0 || forceBackup))
            {
                try
                {
                    backupPath = Backup(databasePath);
                }
                catch (Exception ex)
                {
                    return new MigrationExecutionResult(
                        Success: false,
                        PreviousVersion: previousVersion,
                        CurrentVersion: previousVersion,
                        AppliedMigrations: Array.Empty<string>(),
                        PreMigrationBackupPath: null,
                        RolledBack: false,
                        ErrorMessage: $"Pre-migration database backup failed: {ex.Message}. Migration refused for safety.");
                }
            }

            // Step 5: Execute pending migrations within transactions
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true
            }.ToString();

            var appliedNamesList = new List<string>();
            int currentVersion = previousVersion;

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                EnsureSchemaMigrationsTable(connection);

                foreach (ISchemaMigration migration in pending)
                {
                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        migration.Up(connection, transaction);
                        RecordMigration(connection, transaction, migration);
                        transaction.Commit();

                        appliedNamesList.Add(migration.Name);
                        currentVersion = Math.Max(currentVersion, migration.Version);
                    }
                    catch (Exception ex)
                    {
                        try { transaction.Rollback(); } catch { }
                        connection.Close();
                        SqliteConnection.ClearPool(connection);

                        bool restored = false;
                        string? restoreError = null;

                        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                        {
                            try
                            {
                                File.Copy(backupPath, databasePath, overwrite: true);
                                restored = true;
                            }
                            catch (Exception copyEx)
                            {
                                restoreError = copyEx.Message;
                            }
                        }

                        return new MigrationExecutionResult(
                            Success: false,
                            PreviousVersion: previousVersion,
                            CurrentVersion: previousVersion,
                            AppliedMigrations: appliedNamesList,
                            PreMigrationBackupPath: backupPath,
                            RolledBack: restored,
                            ErrorMessage: $"Migration '{migration.Name}' (v{migration.Version}) failed: {ex.Message}. " +
                                (restored
                                    ? "Pre-migration database snapshot was restored successfully."
                                    : $"Warning: snapshot restore failed ({restoreError})."));
                    }
                }
            }
            catch (SqliteException ex) when (IsLockOrBusyError(ex))
            {
                return new MigrationExecutionResult(
                    Success: false,
                    PreviousVersion: previousVersion,
                    CurrentVersion: previousVersion,
                    AppliedMigrations: appliedNamesList,
                    PreMigrationBackupPath: backupPath,
                    RolledBack: false,
                    ErrorMessage: "Database is locked by another process (SQLITE_BUSY / SQLITE_LOCKED). Please close running instances of GameSave Manager and retry.");
            }

            // Clean up older backups
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                PruneOldBackups(Path.GetDirectoryName(backupPath)!);
            }

            return new MigrationExecutionResult(
                Success: true,
                PreviousVersion: previousVersion,
                CurrentVersion: currentVersion,
                AppliedMigrations: appliedNamesList,
                PreMigrationBackupPath: backupPath,
                RolledBack: false,
                ErrorMessage: null);
        }

        public string Backup(string databasePath, string? destinationDirectory = null)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException($"Database file not found: {databasePath}", databasePath);

            string dbDir = Path.GetDirectoryName(databasePath) ?? ".";
            string backupDir = destinationDirectory ?? Path.Combine(dbDir, "backups");
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
                destConn.Close();
                SqliteConnection.ClearPool(destConn);
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

            try
            {
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                if (!TableExists(connection, "schema_migrations"))
                    return Array.Empty<SchemaMigrationRecord>();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, applied_utc FROM schema_migrations ORDER BY id ASC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    string appliedUtcText = reader.GetString(2);
                    DateTimeOffset appliedUtc = DateTimeOffset.TryParse(appliedUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                        ? dto
                        : DateTimeOffset.UtcNow;

                    results.Add(new SchemaMigrationRecord(id, name, appliedUtc));
                }
            }
            catch (Exception)
            {
                // Fallback on table error
            }

            return results;
        }

        private static void EnsureSchemaMigrationsTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                applied_utc TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
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
            VALUES ($id, $name, CURRENT_TIMESTAMP)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name,
                applied_utc = excluded.applied_utc;
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
